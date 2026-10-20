namespace PropertyManager.Core.Entities;

public class Lease
{
    public int Id { get; set; }
    public int UnitId { get; set; }
    public Unit? Unit { get; set; }
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public DateOnly StartDate { get; set; }

    /// <summary>Null means month-to-month with no scheduled end.</summary>
    public DateOnly? EndDate { get; set; }

    public decimal MonthlyRent { get; set; }
    public decimal SecurityDeposit { get; set; }

    public bool IsActiveOn(DateOnly date) =>
        StartDate <= date && (EndDate is null || EndDate >= date);
}
