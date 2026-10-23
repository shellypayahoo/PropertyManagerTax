using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Calendar;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;
using PropertyManager.Core.Entities;

namespace PropertyManager.Tests;

public sealed class CalendarTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly EmailTests.FakeMailSource mail = new();
    private readonly CalendarSyncService sync;

    public CalendarTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        sync = new CalendarSyncService(dbFactory, mail, new FixedTime(Now));
    }

    public void Dispose() => connection.Dispose();

    private static Property Taylor => new() { Name = "1354 Taylor St NW", Street = "1354 Taylor St NW" };
    private static Property FirstSt => new() { Name = "2228 1st St NW", Street = "2228 1st St NW" };

    private static CalendarEvent Event(string subject, DateOnly date, string? location = null) =>
        new() { Subject = subject, Location = location, StartsAt = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero), EndsAt = new DateTimeOffset(date.ToDateTime(new TimeOnly(16, 0)), TimeSpan.Zero) };

    [Theory]
    [InlineData("HVAC service - Taylor", true, false)]
    [InlineData("Plumber at 1354", true, false)]
    [InlineData("Inspection 1st St unit A", false, true)]
    [InlineData("Walkthrough 2228", false, true)]
    [InlineData("1st floor painting", false, false)]
    [InlineData("Lunch with Taylorsville crew", false, false)]
    public void Appointments_AreMatchedToProperties(string text, bool taylor, bool firstSt)
    {
        var ev = Event(text, new(2026, 3, 5));

        Assert.Equal(taylor, EventMatcher.MentionsProperty(ev.Text, Taylor));
        Assert.Equal(firstSt, EventMatcher.MentionsProperty(ev.Text, FirstSt));
        Assert.Equal(taylor ? "1354 Taylor St NW" : firstSt ? "2228 1st St NW" : null, EventMatcher.PropertyFor(ev, [Taylor, FirstSt])?.Name);
    }

    [Fact]
    public void Suggestions_FavorPropertyVendorAndTitleWords()
    {
        var request = new MaintenanceRequest
        {
            Title = "Kitchen sink leak",
            ReportedOn = new(2026, 3, 2),
            Unit = new Unit { Label = "B", Property = Taylor },
            Vendor = new Vendor { Name = "Ace Plumbing" },
        };
        var best = Event("Ace - fix sink, Taylor unit B", new(2026, 3, 6));
        var vendorOnly = Event("Ace Plumbing invoice review", new(2026, 3, 10));
        var propertyOnly = Event("Taylor St block party", new(2026, 3, 4));
        var tooLate = Event("Ace sink Taylor", new(2026, 9, 1));
        var unrelated = Event("Dentist", new(2026, 3, 3));

        var suggestions = EventMatcher.Suggest(request, [unrelated, tooLate, propertyOnly, vendorOnly, best]);

        var top = Assert.Single(suggestions);
        Assert.Same(best, top.Event);
        Assert.Equal(["1354 Taylor St NW", "Ace Plumbing", "Unit B", "\"sink\""], top.Reasons);
    }

    [Fact]
    public async Task Sync_AddsUpdatesAndRemovesEvents_KeepingIdsAndLinks()
    {
        mail.Events["cal"] =
        [
            new CalendarItem("e1", "HVAC Taylor", "1354 Taylor St NW", null, Now.AddDays(3), Now.AddDays(3).AddHours(1), false, false),
            new CalendarItem("e2", "Gutters", null, null, Now.AddDays(10), Now.AddDays(10).AddHours(2), false, false),
            new CalendarItem("e3", "Cancelled visit", null, null, Now.AddDays(5), Now.AddDays(5), false, true),
        ];
        await sync.SetCalendarsAsync([new CalendarInfo("cal", "Rentals")]);
        Assert.Equal(new CalendarSyncSummary(2, 0, 0), await sync.SyncAsync());
        Assert.Equal((Now - CalendarSyncService.Back, Now + CalendarSyncService.Ahead), mail.EventWindow);

        int hvacId;
        await using (var db = dbFactory.CreateDbContext())
        {
            var hvac = await db.CalendarEvents.SingleAsync(e => e.GraphId == "e1");
            hvacId = hvac.Id;
            db.MaintenanceRequests.Add(new MaintenanceRequest { UnitId = db.Units.First().Id, Title = "HVAC", ReportedOn = new(2026, 3, 1), CalendarEventId = hvac.Id });
            await db.SaveChangesAsync();
        }

        mail.Events["cal"] = [new CalendarItem("e1", "HVAC Taylor (moved)", "1354 Taylor St NW", null, Now.AddDays(4), Now.AddDays(4).AddHours(1), false, false)];
        Assert.Equal(new CalendarSyncSummary(0, 1, 1), await sync.SyncAsync());

        await using var check = dbFactory.CreateDbContext();
        var moved = await check.CalendarEvents.SingleAsync();
        Assert.Equal((hvacId, "HVAC Taylor (moved)"), (moved.Id, moved.Subject));
        Assert.Equal(hvacId, (await check.MaintenanceRequests.SingleAsync()).CalendarEventId);

        await sync.SetCalendarsAsync([]);
        await using var after = dbFactory.CreateDbContext();
        Assert.Empty(await after.CalendarEvents.ToListAsync());
        Assert.Null((await after.MaintenanceRequests.SingleAsync()).CalendarEventId);
    }

    [Fact]
    public void AllDayEvents_KeepTheirDate() =>
        Assert.Equal(new DateOnly(2026, 3, 5), new CalendarEvent { IsAllDay = true, StartsAt = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero) }.StartDate);

    [Fact]
    public async Task Graph_ReadsCalendarsAndEventsInUtc()
    {
        var http = new FakeGraph(new()
        {
            ["/calendars?"] = """{"value":[{"id":"c1","name":"Rentals"}]}""",
            ["/calendarView"] = """
                {"value":[
                  {"id":"e1","subject":"HVAC","location":{"displayName":"1354 Taylor St NW"},"bodyPreview":"Annual service",
                   "start":{"dateTime":"2026-03-05T15:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-03-05T16:00:00.0000000","timeZone":"UTC"},"isAllDay":false,"isCancelled":false},
                  {"id":"e2","subject":"Rent due","location":{"displayName":""},
                   "start":{"dateTime":"2026-04-01T00:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-04-02T00:00:00.0000000","timeZone":"UTC"},"isAllDay":true,"isCancelled":false}]}
                """,
        });
        var graph = new GraphMailSource(new HttpClient(http), new FakeToken());

        var calendars = await graph.GetCalendarsAsync();
        var events = await graph.GetEventsAsync("c1", Now, Now.AddDays(60));

        Assert.Equal([new CalendarInfo("c1", "Rentals")], calendars);
        Assert.Equal(new CalendarItem("e1", "HVAC", "1354 Taylor St NW", "Annual service", new(2026, 3, 5, 15, 0, 0, TimeSpan.Zero), new(2026, 3, 5, 16, 0, 0, TimeSpan.Zero), false, false), events[0]);
        Assert.Equal(new CalendarItem("e2", "Rent due", null, null, new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 4, 2, 0, 0, 0, TimeSpan.Zero), true, false), events[1]);
        var url = Uri.UnescapeDataString(http.Seen[1].RequestUri!.ToString());
        Assert.Contains("startDateTime=2026-03-01T12:00:00Z&endDateTime=2026-04-30T12:00:00Z", url);
        Assert.Contains("outlook.timezone=\"UTC\"", string.Join(", ", http.Seen[1].Headers.GetValues("Prefer")));
    }

    private sealed class FakeGraph(Dictionary<string, string> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            var body = responses.FirstOrDefault(r => url.Contains(r.Key)).Value;
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body ?? "{}") });
        }
    }

    private sealed class FakeToken : IMailTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("t");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
