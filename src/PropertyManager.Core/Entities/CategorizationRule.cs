namespace PropertyManager.Core.Entities;

/// <summary>
/// Pre-fills how an imported transaction is classified when its description contains <see cref="MatchText"/>.
/// When several rules match, the one with the longest match text wins.
/// </summary>
public class CategorizationRule
{
    public int Id { get; set; }
    public string MatchText { get; set; } = "";
    public TransactionKind Kind { get; set; }

    public int? PropertyId { get; set; }
    public Property? Property { get; set; }
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public ExpenseCategory? Category { get; set; }
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    /// <summary>For rent payments: the tenant whose lease the payment is applied to.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public bool Matches(string description) =>
        description.Contains(MatchText, StringComparison.OrdinalIgnoreCase);
}
