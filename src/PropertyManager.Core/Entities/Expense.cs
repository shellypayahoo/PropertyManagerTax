namespace PropertyManager.Core.Entities;

public class Expense
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public Property? Property { get; set; }

    /// <summary>Optional; leave null for property-wide costs such as taxes or insurance.</summary>
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }

    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public int? MaintenanceRequestId { get; set; }
    public MaintenanceRequest? MaintenanceRequest { get; set; }

    public DateOnly Date { get; set; }
    public ExpenseCategory Category { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
}
