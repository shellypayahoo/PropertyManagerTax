using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Services;

public enum RentStatus { NotDue, Upcoming, Unpaid, Partial, Paid }

/// <summary>Rent due and paid under one lease for one month.</summary>
public record RentMonth(Lease Lease, DateOnly Month, decimal Due, IReadOnlyList<RentPayment> Payments, RentStatus Status)
{
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Balance => Due - Paid;
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
