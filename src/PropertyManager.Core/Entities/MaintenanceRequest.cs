namespace PropertyManager.Core.Entities;

public enum MaintenancePriority { Low, Normal, High, Emergency }

public enum MaintenanceStatus { Open, Scheduled, InProgress, Completed, Cancelled }

public class MaintenanceRequest
{
    public int Id { get; set; }
    public int UnitId { get; set; }
    public Unit? Unit { get; set; }

    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public MaintenancePriority Priority { get; set; } = MaintenancePriority.Normal;
    public MaintenanceStatus Status { get; set; } = MaintenanceStatus.Open;

    public DateOnly ReportedOn { get; set; }
    public DateOnly? ScheduledFor { get; set; }
    public DateOnly? CompletedOn { get; set; }

    /// <summary>The calendar appointment this request was scheduled from, if any.</summary>
    public int? CalendarEventId { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }

    public List<Expense> Expenses { get; set; } = [];

    public bool IsOpen => Status is not (MaintenanceStatus.Completed or MaintenanceStatus.Cancelled);
}
