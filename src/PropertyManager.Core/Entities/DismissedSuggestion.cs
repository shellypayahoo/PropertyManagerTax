namespace PropertyManager.Core.Entities;

/// <summary>A calendar appointment the owner said isn't the visit for a maintenance request ("Not this").</summary>
public class DismissedSuggestion
{
    public int MaintenanceRequestId { get; set; }
    public MaintenanceRequest? MaintenanceRequest { get; set; }

    public int CalendarEventId { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }
}
