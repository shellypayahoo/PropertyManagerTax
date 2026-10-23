namespace PropertyManager.Core.Entities;

/// <summary>One appointment (or one occurrence of a recurring appointment) from a synced calendar.</summary>
public class CalendarEvent
{
    public int Id { get; set; }
    public int CalendarSourceId { get; set; }
    public CalendarSource? CalendarSource { get; set; }

    /// <summary>The event's Microsoft Graph id; each occurrence of a recurring event has its own.</summary>
    public string GraphId { get; set; } = "";

    public string Subject { get; set; } = "";
    public string? Location { get; set; }

    /// <summary>The first part of the event's notes.</summary>
    public string? Preview { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>All-day events are stored at midnight UTC of their date; use <see cref="StartDate"/>.</summary>
    public bool IsAllDay { get; set; }

    public DateOnly StartDate => IsAllDay ? DateOnly.FromDateTime(StartsAt.UtcDateTime) : DateOnly.FromDateTime(StartsAt.LocalDateTime);

    public string Text => $"{Subject} {Location} {Preview}";
}
