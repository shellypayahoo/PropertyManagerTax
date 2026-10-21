namespace PropertyManager.Core.Entities;

public class Expense
{
    public int Id { get; set; }
    /// <summary>
    /// The property the expense is for, or null for a cost shared by all properties (software, accounting, etc.).
    /// Shared expenses are split across properties on the annual report.
    /// </summary>
    public int? PropertyId { get; set; }
    public Property? Property { get; set; }

    public bool IsShared => PropertyId is null;

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

    /// <summary>How the expense was paid; useful when it was paid in cash or from a personal account.</summary>
    public PaymentMethod? PaidWith { get; set; }

    /// <summary>Free-form notes, e.g. the vendor's text message the expense came from.</summary>
    public string? Notes { get; set; }
}
