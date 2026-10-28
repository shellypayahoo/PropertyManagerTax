using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Calendar;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class CalendarEventTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 16, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly EmailTests.FakeMailSource mail = new();
    private readonly CalendarEventService service;
    private readonly int requestId;
    private readonly int leaseId;
    private readonly int rentalsId;
    private readonly int personalId;

    public CalendarEventTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var unit = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B");
        var lease = new Lease
        {
            Unit = unit, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample", Phone = "202-555-0101" }],
            StartDate = new(2026, 1, 1), EndDate = new(2026, 12, 31), MonthlyRent = 1000m,
        };
        var request = new MaintenanceRequest
        {
            Unit = unit, Title = "Kitchen faucet leak", Description = "Drips under the sink", ReportedOn = new(2026, 10, 27),
            Vendor = new Vendor { Name = "Ace Plumbing", Phone = "202-555-0199" },
        };
        var rentals = new CalendarSource { GraphId = "rentals", Name = "Rentals" };
        var personal = new CalendarSource { GraphId = "personal", Name = "Calendar" };
        db.AddRange(lease, request, rentals, personal);
        db.SaveChanges();
        (requestId, leaseId, rentalsId, personalId) = (request.Id, lease.Id, rentals.Id, personal.Id);
        mail.Events["rentals"] = [];
        mail.Events["personal"] = [];
        service = new CalendarEventService(dbFactory, mail, new FixedTime(Now, Eastern));
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task Maintenance_visit_is_filled_in_added_and_linked()
    {
        var draft = await service.ForMaintenanceAsync(requestId);

        Assert.Equal("Ace Plumbing: Kitchen faucet leak – 1354 Taylor St NW – Unit B", draft.Subject);
        Assert.Equal(new DateOnly(2026, 10, 29), draft.Date);
        Assert.StartsWith("1354 Taylor St NW, Washington, DC", draft.Location);
        Assert.EndsWith("(Unit B)", draft.Location);
        Assert.Contains("Vendor: Ace Plumbing, 202-555-0199", draft.Notes);
        Assert.Contains("Tenant: Jane Sample, 202-555-0101", draft.Notes);

        draft.Start = new TimeOnly(14, 30);
        draft.DurationMinutes = 90;
        var ev = await service.AddAsync(rentalsId, draft);

        var (calendar, sent) = Assert.Single(mail.Created);
        Assert.Equal("rentals", calendar);
        Assert.Equal((new DateTime(2026, 10, 29, 14, 30, 0), new DateTime(2026, 10, 29, 16, 0, 0), "Eastern Standard Time", false, 60),
            (sent.Start, sent.End, sent.TimeZone, sent.IsAllDay, sent.ReminderMinutes!.Value));
        await using var db = dbFactory.CreateDbContext();
        var request = db.MaintenanceRequests.Single(m => m.Id == requestId);
        Assert.Equal((ev.Id, new DateOnly(2026, 10, 29), MaintenanceStatus.Scheduled), (request.CalendarEventId!.Value, request.ScheduledFor!.Value, request.Status));
        Assert.Equal("new-1", db.CalendarEvents.Single(e => e.Id == ev.Id).GraphId);
    }

    [Fact]
    public async Task Appointment_can_be_in_another_time_zone()
    {
        var draft = service.Blank();
        Assert.Equal("America/New_York", draft.TimeZone);
        draft.Subject = "Tank's vet appointment";
        draft.Start = new TimeOnly(13, 0);
        draft.TimeZone = "America/Denver";

        var ev = await service.AddAsync(personalId, draft);

        var sent = Assert.Single(mail.Created).Item;
        Assert.Equal(("Mountain Standard Time", new DateTime(2026, 10, 29, 13, 0, 0)), (sent.TimeZone, sent.Start));
        Assert.Equal("America/Denver", ev.TimeZone);
    }

    [Fact]
    public async Task Calendar_last_added_to_is_offered_first()
    {
        Assert.Equal(["Calendar", "Rentals"], (await service.CalendarsAsync()).Select(c => c.Name));

        var draft = service.Blank();
        draft.Subject = "Showing";
        await service.AddAsync(rentalsId, draft);

        Assert.Equal(["Rentals", "Calendar"], (await service.CalendarsAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task Lease_end_reminder_is_all_day_and_linked()
    {
        var draft = await service.ForLeaseEndAsync(leaseId, daysBefore: 60);

        Assert.Equal("Lease ends Dec 31, 2026: 1354 Taylor St NW – Unit B (Jane Sample)", draft.Subject);
        Assert.Equal(new DateOnly(2026, 11, 1), draft.Date);
        Assert.True(draft.AllDay);
        Assert.Equal(AppointmentDraft.AllDayReminder, draft.ReminderMinutes);

        var ev = await service.AddAsync(personalId, draft);

        var sent = Assert.Single(mail.Created).Item;
        Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 2), true), (sent.Start, sent.End, sent.IsAllDay));
        await using var db = dbFactory.CreateDbContext();
        Assert.Equal(ev.Id, db.Leases.Single(l => l.Id == leaseId).EndReminderEventId);
    }

    [Fact]
    public async Task Month_to_month_lease_has_no_end_to_remind_about()
    {
        await using (var db = dbFactory.CreateDbContext())
        {
            await db.Leases.Where(l => l.Id == leaseId).ExecuteUpdateAsync(u => u.SetProperty(l => l.EndDate, (DateOnly?)null));
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ForLeaseEndAsync(leaseId));
        Assert.Contains("month-to-month", ex.Message);
    }

    [Fact]
    public async Task Untitled_appointment_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(rentalsId, service.Blank()));
        Assert.Empty(mail.Created);
    }

    [Fact]
    public void Time_zones_are_named_the_way_Graph_expects()
    {
        Assert.Equal("Eastern Standard Time", CalendarEventService.WindowsZoneName(Eastern));
        Assert.Equal("UTC", CalendarEventService.WindowsZoneName(TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task Graph_posts_the_event_with_the_write_permission()
    {
        var http = new CapturingGraph("""
            {"id":"AAMk1","subject":"Showing","bodyPreview":"Bring keys","isAllDay":false,"isCancelled":false,
             "location":{"displayName":"1354 Taylor St NW"},
             "start":{"dateTime":"2026-10-29T14:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-29T15:00:00.0000000","timeZone":"UTC"}}
            """);
        var graph = new GraphMailSource(new HttpClient(http), new WriteToken());

        var created = await graph.CreateEventAsync("cal/1", new NewCalendarItem("Showing", "1354 Taylor St NW", "Bring keys",
            new DateTime(2026, 10, 29, 10, 0, 0), new DateTime(2026, 10, 29, 11, 0, 0), "Eastern Standard Time", false, 60));

        Assert.Equal(HttpMethod.Post, http.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/calendars/cal%2F1/events", http.Url);
        Assert.Equal("write", http.Token);
        using var body = JsonDocument.Parse(http.Body!);
        var root = body.RootElement;
        Assert.Equal("Showing", root.GetProperty("subject").GetString());
        Assert.Equal("2026-10-29T10:00:00", root.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("Eastern Standard Time", root.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal(60, root.GetProperty("reminderMinutesBeforeStart").GetInt32());
        Assert.Equal("1354 Taylor St NW", root.GetProperty("location").GetProperty("displayName").GetString());
        Assert.Equal(("AAMk1", new DateTimeOffset(2026, 10, 29, 14, 0, 0, TimeSpan.Zero)), (created.Id, created.Start));
    }

    [Fact]
    public async Task Ask_proposes_a_visit_and_adds_it_only_when_applied()
    {
        var conversation = new AssistantConversation();
        var storage = new LocalDocumentStorage(Path.Combine(Path.GetTempPath(), "pm-cal-" + Guid.NewGuid().ToString("N")));
        var actions = new AssistantActions(dbFactory, new ReceiptService(dbFactory, storage, TimeProvider.System),
            new LeaseDocumentService(dbFactory, storage, TimeProvider.System), calendarEvents: service) { Conversation = conversation };

        var answer = await actions.ProposeAddCalendarEventAsync("2026-11-03", time: "10am", maintenanceRequestId: requestId, calendar: "rentals");

        Assert.Contains("Proposed change 1", answer);
        var change = Assert.Single(conversation.Proposals);
        Assert.Contains("Calendar: Rentals", change.Details);
        Assert.Contains(change.Details, d => d.StartsWith("When: Tue, Nov 3, 2026 10:00 AM"));
        Assert.Empty(mail.Created);

        Assert.Contains("Added", await change.Apply(CancellationToken.None));
        var sent = Assert.Single(mail.Created).Item;
        Assert.Equal(new DateTime(2026, 11, 3, 10, 0, 0), sent.Start);
        await using var db = dbFactory.CreateDbContext();
        Assert.Equal(MaintenanceStatus.Scheduled, db.MaintenanceRequests.Single(m => m.Id == requestId).Status);
    }

    [Fact]
    public async Task Ask_reminder_without_a_time_is_all_day_at_the_property()
    {
        var conversation = new AssistantConversation();
        var storage = new LocalDocumentStorage(Path.Combine(Path.GetTempPath(), "pm-cal-" + Guid.NewGuid().ToString("N")));
        var actions = new AssistantActions(dbFactory, new ReceiptService(dbFactory, storage, TimeProvider.System),
            new LeaseDocumentService(dbFactory, storage, TimeProvider.System), calendarEvents: service) { Conversation = conversation };

        await actions.ProposeAddCalendarEventAsync("2026-11-10", title: "DC rental license renewal", property: "Taylor", unit: "B");
        await Assert.Single(conversation.Proposals).Apply(CancellationToken.None);

        var sent = Assert.Single(mail.Created).Item;
        Assert.True(sent.IsAllDay);
        Assert.Equal("DC rental license renewal", sent.Subject);
        Assert.StartsWith("1354 Taylor St NW, Washington, DC", sent.Location);
        Assert.EndsWith("(Unit B)", sent.Location);
    }

    [Theory]
    [InlineData("10am", 10, 0)]
    [InlineData("2:30 PM", 14, 30)]
    [InlineData("12 pm", 12, 0)]
    [InlineData("12am", 0, 0)]
    [InlineData("14:15", 14, 15)]
    public void Times_are_understood(string text, int hour, int minute) =>
        Assert.Equal(new TimeOnly(hour, minute), AssistantActions.ParseTime(text));

    private sealed class CapturingGraph(string response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Token { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (Method, Url, Token) = (request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter);
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(response) };
        }
    }

    private sealed class WriteToken : IMailTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("read");
        public Task<string> GetCalendarWriteTokenAsync(CancellationToken ct = default) => Task.FromResult("write");
    }

    private sealed class FixedTime(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}

public class CalendarTimeZoneTests
{
    [Fact]
    public async Task Created_event_times_are_read_in_the_zone_Graph_gives()
    {
        var http = new ZoneGraph("""
            {"id":"AAMk2","subject":"Plumber","isAllDay":false,"isCancelled":false,"originalStartTimeZone":"Mountain Standard Time",
             "start":{"dateTime":"2026-10-29T10:00:00.0000000","timeZone":"Mountain Standard Time"},
             "end":{"dateTime":"2026-10-29T11:00:00.0000000","timeZone":"Mountain Standard Time"}}
            """);
        var graph = new GraphMailSource(new HttpClient(http), new Token());

        var created = await graph.CreateEventAsync("cal", new NewCalendarItem("Plumber", null, null,
            new DateTime(2026, 10, 29, 10, 0, 0), new DateTime(2026, 10, 29, 11, 0, 0), "Mountain Standard Time", false, null));

        // 10 AM Mountain daylight time is 4 PM UTC, not 10 AM UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 16, 0, 0, TimeSpan.Zero), created.Start);
        Assert.Equal("America/Denver", created.TimeZone);
    }

    [Fact]
    public async Task All_day_event_stays_on_its_date_in_any_zone()
    {
        var http = new ZoneGraph("""
            {"id":"AAMk3","subject":"Dishwasher delivery","isAllDay":true,"isCancelled":false,
             "start":{"dateTime":"2026-10-30T00:00:00.0000000","timeZone":"Eastern Standard Time"},
             "end":{"dateTime":"2026-10-31T00:00:00.0000000","timeZone":"Eastern Standard Time"}}
            """);
        var graph = new GraphMailSource(new HttpClient(http), new Token());

        var created = await graph.CreateEventAsync("cal", new NewCalendarItem("Dishwasher delivery", null, null,
            new DateTime(2026, 10, 30), new DateTime(2026, 10, 31), "Eastern Standard Time", true, null));

        Assert.Equal(new DateOnly(2026, 10, 30), new CalendarEvent { StartsAt = created.Start, IsAllDay = true }.StartDate);
    }

    [Theory]
    [InlineData("Eastern", "America/New_York")]
    [InlineData("mountain time", "America/Denver")]
    [InlineData("PT", "America/Los_Angeles")]
    [InlineData("America/Chicago", "America/Chicago")]
    [InlineData("Mountain Standard Time", "America/Denver")]
    public void Time_zones_are_found_by_everyday_names(string text, string iana) =>
        Assert.Equal(iana, BusinessTimeZone.IanaId(BusinessTimeZone.Find(text)!));

    [Fact]
    public void Unknown_time_zone_is_reported()
    {
        Assert.Null(BusinessTimeZone.Find("Moon/Base"));
        Assert.Contains("isn't a time zone", BusinessTimeZone.Apply("Moon/Base"));
    }

    [Fact]
    public void Appointment_made_in_another_zone_shows_that_zones_time()
    {
        var denver = BusinessTimeZone.Find("America/Denver")!;
        var ev = new CalendarEvent { StartsAt = new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero), TimeZone = "America/Denver" };
        if (BusinessTimeZone.IanaId(TimeZoneInfo.Local) == "America/Denver")
        {
            return; // Only meaningful where the app's zone isn't Mountain.
        }

        Assert.Equal("1:00 PM MT", EventTimes.Start(ev));
        Assert.Equal(new DateOnly(2026, 11, 2), EventTimes.Date(ev));
        Assert.NotNull(EventTimes.InBusinessZone(ev));
        Assert.Equal("MT", BusinessTimeZone.Abbreviation(denver, ev.StartsAt));
    }

    private sealed class ZoneGraph(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(response) });
    }

    private sealed class Token : IMailTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("t");
    }
}
