namespace PropertyManager.Core.Entities;

/// <summary>An Outlook.com calendar whose events are copied into the app.</summary>
public class CalendarSource
{
    public int Id { get; set; }

    /// <summary>The calendar's Microsoft Graph id.</summary>
    public string GraphId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateTimeOffset? LastSyncedAt { get; set; }

    public List<CalendarEvent> Events { get; set; } = [];
}
