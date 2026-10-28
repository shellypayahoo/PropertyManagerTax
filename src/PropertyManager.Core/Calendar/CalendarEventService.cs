using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Calendar;

/// <summary>An appointment to add to Outlook, in the owner's local time. Mutable so a form can edit it.</summary>
public class AppointmentDraft
{
    public const int AllDayReminder = 15 * 60; // 9 AM the day before
    public const int MaxSubjectLength = 255;

    public string Subject { get; set; } = "";
    public DateOnly Date { get; set; }
    public bool AllDay { get; set; }
    public TimeOnly Start { get; set; } = new(10, 0);
    public int DurationMinutes { get; set; } = 60;
    public string? Location { get; set; }
    public string? Notes { get; set; }

    /// <summary>Minutes before the start to remind, or null for no reminder.</summary>
    public int? ReminderMinutes { get; set; } = 60;

    /// <summary>The maintenance request this visit is for; it's linked and marked scheduled.</summary>
    public int? MaintenanceRequestId { get; set; }

    /// <summary>The lease whose end this reminds about; it's linked to the lease.</summary>
    public int? LeaseId { get; set; }

    public string When => AllDay ? $"{Date:ddd, MMM d, yyyy} (all day)" : $"{Date:ddd, MMM d, yyyy} {Start:h:mm tt}–{Start.AddMinutes(DurationMinutes):h:mm tt}";
}

/// <summary>Adds appointments to the owner's Outlook.com calendars and keeps a copy with the synced events.</summary>
public class CalendarEventService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IMailSource mail, TimeProvider clock)
{
    /// <summary>The synced calendars, the one most recently added to first.</summary>
    public async Task<List<CalendarSource>> CalendarsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.CalendarSources.AsNoTracking().ToListAsync(ct))
            .OrderByDescending(c => c.LastAddedToAt ?? DateTimeOffset.MinValue)
            .ThenBy(c => c.Name)
            .ToList();
    }

    public AppointmentDraft Blank() => new() { Date = Today.AddDays(1) };

    /// <summary>A visit for a maintenance request: the vendor and issue, the unit's address, and phone numbers in the notes.</summary>
    public async Task<AppointmentDraft> ForMaintenanceAsync(int requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.MaintenanceRequests.AsNoTracking()
            .Include(m => m.Unit).ThenInclude(u => u!.Property)
            .Include(m => m.Unit).ThenInclude(u => u!.Leases).ThenInclude(l => l.Tenants)
            .Include(m => m.Vendor)
            .FirstOrDefaultAsync(m => m.Id == requestId, ct)
            ?? throw new InvalidOperationException("That maintenance request no longer exists.");
        var unit = request.Unit!;
        var notes = new List<string> { request.Title };
        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            notes.Add(request.Description);
        }
        if (request.Vendor is { } vendor)
        {
            notes.Add($"Vendor: {vendor.Name}{(string.IsNullOrWhiteSpace(vendor.Phone) ? "" : ", " + vendor.Phone)}");
        }
        foreach (var tenant in unit.ActiveLease(Today)?.Tenants ?? [])
        {
            notes.Add($"Tenant: {tenant.FullName}{(string.IsNullOrWhiteSpace(tenant.Phone) ? "" : ", " + tenant.Phone)}");
        }
        return new AppointmentDraft
        {
            Subject = Limit($"{(request.Vendor is { } v ? v.Name + ": " : "")}{request.Title} – {unit.DisplayName}"),
            Date = request.ScheduledFor is { } scheduled && scheduled >= Today ? scheduled : Today.AddDays(1),
            Location = unit.Property is { } p ? $"{p.FullAddress} (Unit {unit.Label})" : null,
            Notes = string.Join("\n", notes),
            MaintenanceRequestId = request.Id,
        };
    }

    /// <summary>An all-day reminder some days before a lease ends.</summary>
    public async Task<AppointmentDraft> ForLeaseEndAsync(int leaseId, int daysBefore = 60, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lease = await db.Leases.AsNoTracking()
            .Include(l => l.Tenants)
            .Include(l => l.Unit).ThenInclude(u => u!.Property)
            .FirstOrDefaultAsync(l => l.Id == leaseId, ct)
            ?? throw new InvalidOperationException("That lease no longer exists.");
        if (lease.EndDate is not { } end)
        {
            throw new InvalidOperationException("This lease is month-to-month; give it an end date first.");
        }
        return new AppointmentDraft
        {
            Subject = Limit($"Lease ends {end:MMM d, yyyy}: {lease.Unit?.DisplayName} ({lease.TenantNames})"),
            Date = end.AddDays(-daysBefore),
            AllDay = true,
            ReminderMinutes = AppointmentDraft.AllDayReminder,
            Notes = $"The lease for {lease.Unit?.DisplayName} with {lease.TenantNames} ends {end:dddd, MMMM d, yyyy}. Decide whether to renew, and send any notice in time.",
            LeaseId = lease.Id,
        };
    }

    /// <summary>Adds the appointment to the calendar, saves it with the synced events, and links it to its request or lease.</summary>
    public async Task<CalendarEvent> AddAsync(int calendarSourceId, AppointmentDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Subject))
        {
            throw new ArgumentException("Give the appointment a title.");
        }
        if (!draft.AllDay && draft.DurationMinutes is <= 0 or > 24 * 60)
        {
            throw new ArgumentException("The length must be between a minute and a day.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var calendar = await db.CalendarSources.FirstOrDefaultAsync(c => c.Id == calendarSourceId, ct)
            ?? throw new InvalidOperationException("That calendar isn't synced any more. Choose calendars on the Email & calendar page.");

        var start = draft.AllDay ? draft.Date.ToDateTime(TimeOnly.MinValue) : draft.Date.ToDateTime(draft.Start);
        var end = draft.AllDay ? start.AddDays(1) : start.AddMinutes(draft.DurationMinutes);
        var zone = clock.LocalTimeZone;
        var zoneName = WindowsZoneName(zone);
        if (zoneName is null && !draft.AllDay)
        {
            // Unknown time zone: send the times in UTC instead.
            (start, end, zoneName) = (TimeZoneInfo.ConvertTimeToUtc(start, zone), TimeZoneInfo.ConvertTimeToUtc(end, zone), "UTC");
        }
        var item = await mail.CreateEventAsync(calendar.GraphId, new NewCalendarItem(
            Limit(draft.Subject.Trim()),
            string.IsNullOrWhiteSpace(draft.Location) ? null : draft.Location.Trim(),
            string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim(),
            start, end, zoneName ?? "UTC", draft.AllDay, draft.ReminderMinutes), ct);

        var ev = await db.CalendarEvents.FirstOrDefaultAsync(e => e.GraphId == item.Id, ct);
        if (ev is null)
        {
            ev = new CalendarEvent { GraphId = item.Id };
            db.CalendarEvents.Add(ev);
        }
        ev.CalendarSourceId = calendar.Id;
        ev.Subject = Truncate(item.Subject, 500);
        ev.Location = item.Location is null ? null : Truncate(item.Location, 500);
        ev.Preview = item.Preview is null ? null : Truncate(item.Preview, 1000);
        ev.StartsAt = item.Start;
        ev.EndsAt = item.End;
        ev.IsAllDay = item.IsAllDay;
        calendar.LastAddedToAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        if (draft.MaintenanceRequestId is { } requestId && await db.MaintenanceRequests.FindAsync([requestId], ct) is { } request)
        {
            request.CalendarEventId = ev.Id;
            request.ScheduledFor = draft.Date;
            if (request.Status == MaintenanceStatus.Open)
            {
                request.Status = MaintenanceStatus.Scheduled;
            }
        }
        if (draft.LeaseId is { } leaseId && await db.Leases.FindAsync([leaseId], ct) is { } lease)
        {
            lease.EndReminderEventId = ev.Id;
        }
        await db.SaveChangesAsync(ct);
        return ev;
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>The Windows name Microsoft Graph expects for a time zone (macOS uses IANA names such as America/New_York).</summary>
    internal static string? WindowsZoneName(TimeZoneInfo zone)
    {
        if (zone.Id == "UTC" || zone.Id == "Etc/UTC")
        {
            return "UTC";
        }
        if (zone.HasIanaId)
        {
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) ? windows : null;
        }
        return zone.Id;
    }

    private static string Limit(string text) => Truncate(text, AppointmentDraft.MaxSubjectLength);

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
