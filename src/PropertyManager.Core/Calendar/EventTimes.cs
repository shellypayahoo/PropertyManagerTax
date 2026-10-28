using PropertyManager.Core.Configuration;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Calendar;

/// <summary>
/// How an appointment's time is shown: in the business's time zone, or, for one made in another zone (a vet visit while
/// away), in that zone with its label and the business-zone time beside it.
/// </summary>
public static class EventTimes
{
    /// <summary>The zone the appointment was made in, when that's not the business's (the app's local) time zone.</summary>
    public static TimeZoneInfo? OtherZone(CalendarEvent ev) =>
        !ev.IsAllDay && BusinessTimeZone.Find(ev.TimeZone) is { } zone && BusinessTimeZone.IanaId(zone) != BusinessTimeZone.IanaId(TimeZoneInfo.Local)
            ? zone
            : null;

    /// <summary>The day it's on, in the zone it's shown in.</summary>
    public static DateOnly Date(CalendarEvent ev) =>
        OtherZone(ev) is { } zone ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ev.StartsAt, zone).DateTime) : ev.StartDate;

    /// <summary>"All day", "10:00 AM", or "1:00 PM MT" for an appointment made in another zone.</summary>
    public static string Start(CalendarEvent ev) =>
        ev.IsAllDay ? "All day"
        : OtherZone(ev) is { } zone ? $"{TimeZoneInfo.ConvertTime(ev.StartsAt, zone):h:mm tt} {BusinessTimeZone.Abbreviation(zone, ev.StartsAt)}"
        : ev.StartsAt.ToLocalTime().ToString("h:mm tt");

    /// <summary>For an appointment made in another zone, its start in the business's zone, e.g. "3:00 PM ET"; otherwise null.</summary>
    public static string? InBusinessZone(CalendarEvent ev) =>
        OtherZone(ev) is null ? null : $"{ev.StartsAt.ToLocalTime():h:mm tt} {BusinessTimeZone.Abbreviation(TimeZoneInfo.Local, ev.StartsAt)}";
}
