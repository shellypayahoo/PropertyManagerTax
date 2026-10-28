namespace PropertyManager.Core.Entities;

public class Lease
{
    public int Id { get; set; }
    public int UnitId { get; set; }
    public Unit? Unit { get; set; }

    /// <summary>Everyone on the lease. Co-tenants share one rent, and payments from any of them count toward it.</summary>
    public List<Tenant> Tenants { get; set; } = [];

    public DateOnly StartDate { get; set; }

    /// <summary>Null means month-to-month with no scheduled end.</summary>
    public DateOnly? EndDate { get; set; }

    public decimal MonthlyRent { get; set; }
    public decimal SecurityDeposit { get; set; }

    /// <summary>The calendar reminder added for the end of the lease, if any.</summary>
    public int? EndReminderEventId { get; set; }
    public CalendarEvent? EndReminderEvent { get; set; }

    public List<LeaseDocument> Documents { get; set; } = [];
    public List<RentPayment> Payments { get; set; } = [];

    public string TenantNames => Tenants.Count == 0 ? "(no tenant)" : string.Join(" & ", Tenants.OrderBy(t => t.LastName).ThenBy(t => t.FirstName).Select(t => t.FullName));

    public bool IsActiveOn(DateOnly date) =>
        StartDate <= date && (EndDate is null || EndDate >= date);
}
