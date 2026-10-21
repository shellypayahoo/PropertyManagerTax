using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Services;

public enum RentStatus { NotDue, Upcoming, Unpaid, Partial, Paid }

/// <summary>Rent due and paid under one lease for one month.</summary>
public record RentMonth(Lease Lease, DateOnly Month, decimal Due, IReadOnlyList<RentPayment> Payments, RentStatus Status)
{
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Balance => Due - Paid;

    /// <summary>Describes a likely data-entry mistake in this month's payments, or null when it looks fine.</summary>
    public string? Warning =>
        Due == 0m && Paid > 0m ? "No rent is due this month, but a payment is recorded for it"
        : Payments.Count > 1 && Paid > Due && Payments.GroupBy(p => p.Amount).Any(g => g.Count() > 1) ? "Paid twice?"
        : Payments.Count > 1 && Paid > Due ? $"Overpaid by {Paid - Due:C0}"
        : null;
}

/// <summary>Month-by-month rent: the full monthly rent is due for every month a lease covers, with no proration.</summary>
public static class RentSchedule
{
    /// <summary>True when the lease covers any part of the month.</summary>
    public static bool IsDue(Lease lease, DateOnly month)
    {
        var first = RentPayment.MonthOf(month);
        var last = first.AddMonths(1).AddDays(-1);
        return lease.StartDate <= last && (lease.EndDate is null || lease.EndDate >= first);
    }

    /// <summary>Rent for one month. Requires <see cref="Lease.Payments"/> to be loaded.</summary>
    public static RentMonth ForMonth(Lease lease, DateOnly month, DateOnly today)
    {
        month = RentPayment.MonthOf(month);
        var due = IsDue(lease, month) ? lease.MonthlyRent : 0m;
        var payments = lease.Payments.Where(p => p.ForMonth == month).OrderBy(p => p.Date).ToList();
        var paid = payments.Sum(p => p.Amount);

        var status = due == 0m ? RentStatus.NotDue
            : paid >= due ? RentStatus.Paid
            : month > today ? RentStatus.Upcoming
            : paid > 0m ? RentStatus.Partial
            : RentStatus.Unpaid;
        return new RentMonth(lease, month, due, payments, status);
    }

    /// <summary>All months, across the given leases, whose payments look like a mistake (e.g. rent entered twice).</summary>
    public static IReadOnlyList<RentMonth> FindProblems(IEnumerable<Lease> leases, DateOnly today) =>
        leases
            .SelectMany(l => l.Payments.Select(p => p.ForMonth).Distinct().Select(m => ForMonth(l, m, today)))
            .Where(r => r.Warning is not null)
            .OrderByDescending(r => r.Month)
            .ToList();

    /// <summary>
    /// Checks a payment before it is recorded and returns a warning when it looks like a double entry.
    /// Requires all of the lease's payments to be loaded. When editing, pass the payment as <paramref name="editing"/>;
    /// moving a payment to another month without changing its date or amount then skips the same-day check.
    /// </summary>
    public static string? CheckNewPayment(Lease lease, DateOnly month, DateOnly date, decimal amount, DateOnly today, RentPayment? editing = null)
    {
        month = RentPayment.MonthOf(month);
        var others = lease.Payments.Where(p => editing is null || p.Id != editing.Id).ToList();
        var dateOrAmountChanged = editing is null || editing.Date != date || editing.Amount != amount;

        var same = dateOrAmountChanged ? others.FirstOrDefault(p => p.Date == date && p.Amount == amount) : null;
        if (same is not null)
        {
            return $"A payment of {amount:C} on {date:MMM d, yyyy} is already recorded (for {same.ForMonth:MMMM yyyy}).";
        }

        var existing = ForMonth(new Lease { StartDate = lease.StartDate, EndDate = lease.EndDate, MonthlyRent = lease.MonthlyRent, Payments = others }, month, today);
        if (existing.Due == 0m)
        {
            return $"No rent is due for {month:MMMM yyyy} under this lease.";
        }
        if (existing.Paid > 0m && existing.Paid + amount > existing.Due)
        {
            var list = string.Join(", ", existing.Payments.Select(p => $"{p.Amount:C0} on {p.Date:MMM d}"));
            return $"{month:MMMM yyyy} already has {existing.Paid:C0} of {existing.Due:C0} paid ({list}). Saving this makes it {existing.Paid + amount:C0}.";
        }
        return null;
    }

    /// <summary>Every month of a year that has rent due or a payment recorded, up to the current month for the current year.</summary>
    public static IReadOnlyList<RentMonth> ForYear(Lease lease, int year, DateOnly today)
    {
        var lastMonth = year == today.Year ? today.Month : 12;
        return Enumerable.Range(1, lastMonth)
            .Select(m => ForMonth(lease, new DateOnly(year, m, 1), today))
            .Where(r => r.Status != RentStatus.NotDue || r.Payments.Count > 0)
            .ToList();
    }
}
