using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Services;

public record CategoryTotal(ExpenseCategory Category, decimal Amount);

public record PropertyProfitAndLoss(
    int PropertyId,
    string PropertyName,
    int Year,
    decimal RentReceived,
    decimal ScheduledRent,
    IReadOnlyList<CategoryTotal> Expenses)
{
    public decimal TotalExpenses => Expenses.Sum(e => e.Amount);
    public decimal NetIncome => RentReceived - TotalExpenses;
}

public class ReportService(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    /// <summary>
    /// Builds a Schedule E–style profit and loss statement for each property in a tax year.
    /// Income is rent actually received; scheduled rent from leases (prorated by day) is included for comparison.
    /// </summary>
    public async Task<IReadOnlyList<PropertyProfitAndLoss>> GetAnnualProfitAndLossAsync(int year, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);

        var properties = await db.Properties
            .AsNoTracking()
            .Include(p => p.Units).ThenInclude(u => u.Leases)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        // SQLite stores decimals as TEXT and cannot SUM them, so aggregate in memory.
        var expenses = await db.Expenses
            .AsNoTracking()
            .Where(e => e.Date >= yearStart && e.Date <= yearEnd)
            .ToListAsync(ct);

        var payments = await db.RentPayments
            .AsNoTracking()
            .Where(r => r.Date >= yearStart && r.Date <= yearEnd)
            .Select(r => new { r.Amount, r.Lease!.Unit!.PropertyId })
            .ToListAsync(ct);

        return properties.Select(p => new PropertyProfitAndLoss(
                p.Id,
                p.Name,
                year,
                payments.Where(r => r.PropertyId == p.Id).Sum(r => r.Amount),
                p.Units.SelectMany(u => u.Leases).Sum(l => ScheduledRent(l, yearStart, yearEnd)),
                expenses
                    .Where(e => e.PropertyId == p.Id)
                    .GroupBy(e => e.Category)
                    .OrderBy(g => g.Key)
                    .Select(g => new CategoryTotal(g.Key, g.Sum(e => e.Amount)))
                    .ToList()))
            .ToList();
    }

    /// <summary>Rent scheduled under a lease between two dates (inclusive), prorated by day within each month.</summary>
    public static decimal ScheduledRent(Lease lease, DateOnly from, DateOnly to)
    {
        var start = lease.StartDate > from ? lease.StartDate : from;
        var end = lease.EndDate is { } e && e < to ? e : to;
        if (end < start)
        {
            return 0m;
        }

        decimal total = 0m;
        var month = new DateOnly(start.Year, start.Month, 1);
        while (month <= end)
        {
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
            var monthEnd = month.AddDays(daysInMonth - 1);
            var overlapStart = start > month ? start : month;
            var overlapEnd = end < monthEnd ? end : monthEnd;
            var days = overlapEnd.DayNumber - overlapStart.DayNumber + 1;

            total += days == daysInMonth
                ? lease.MonthlyRent
                : Math.Round(lease.MonthlyRent * days / daysInMonth, 2, MidpointRounding.AwayFromZero);

            month = month.AddMonths(1);
        }

        return total;
    }
}
