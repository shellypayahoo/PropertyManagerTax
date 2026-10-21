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
    IReadOnlyList<CategoryTotal> Expenses,
    decimal SharedExpenses)
{
    public decimal TotalExpenses => Expenses.Sum(e => e.Amount);
    public decimal NetIncome => RentReceived - TotalExpenses;
}

public class ReportService(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    /// <summary>
    /// Builds a Schedule E–style profit and loss statement for each property in a tax year.
    /// Income is rent actually received; scheduled rent from leases is included for comparison.
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

        var shared = AllocateSharedExpenses(properties, expenses.Where(e => e.IsShared));

        return properties.Select(p => new PropertyProfitAndLoss(
                p.Id,
                p.Name,
                year,
                payments.Where(r => r.PropertyId == p.Id).Sum(r => r.Amount),
                p.Units.SelectMany(u => u.Leases).Sum(l => ScheduledRent(l, yearStart, yearEnd)),
                expenses
                    .Where(e => e.PropertyId == p.Id)
                    .Select(e => (e.Category, e.Amount))
                    .Concat(shared.Where(s => s.PropertyId == p.Id).Select(s => (s.Category, s.Amount)))
                    .GroupBy(e => e.Category)
                    .OrderBy(g => g.Key)
                    .Select(g => new CategoryTotal(g.Key, g.Sum(e => e.Amount)))
                    .ToList(),
                shared.Where(s => s.PropertyId == p.Id).Sum(s => s.Amount)))
            .ToList();
    }

    /// <summary>
    /// Splits expenses shared by all properties across the properties in proportion to their number of units,
    /// per category. Rounding pennies go to the last property so totals match exactly.
    /// </summary>
    public static IReadOnlyList<(int PropertyId, ExpenseCategory Category, decimal Amount)> AllocateSharedExpenses(
        IReadOnlyList<Property> properties, IEnumerable<Expense> sharedExpenses)
    {
        var weights = properties.Select(p => (p.Id, Units: Math.Max(p.Units.Count, 1))).ToList();
        var totalUnits = weights.Sum(w => w.Units);
        var result = new List<(int, ExpenseCategory, decimal)>();
        if (totalUnits == 0)
        {
            return result;
        }

        foreach (var category in sharedExpenses.GroupBy(e => e.Category))
        {
            var total = category.Sum(e => e.Amount);
            var allocated = 0m;
            for (var i = 0; i < weights.Count; i++)
            {
                var amount = i == weights.Count - 1
                    ? total - allocated
                    : Math.Round(total * weights[i].Units / totalUnits, 2, MidpointRounding.AwayFromZero);
                allocated += amount;
                result.Add((weights[i].Id, category.Key, amount));
            }
        }
        return result;
    }

    /// <summary>Rent due under a lease for the months between two dates (inclusive). Each month the lease covers is due in full.</summary>
    public static decimal ScheduledRent(Lease lease, DateOnly from, DateOnly to)
    {
        var total = 0m;
        for (var month = RentPayment.MonthOf(from); month <= to; month = month.AddMonths(1))
        {
            if (RentSchedule.IsDue(lease, month))
            {
                total += lease.MonthlyRent;
            }
        }
        return total;
    }
}
