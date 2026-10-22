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
    decimal SharedExpenses,
    IReadOnlyList<CategoryTotal> NotDeductible)
{
    public decimal TotalExpenses => Expenses.Sum(e => e.Amount);
    public decimal NetIncome => RentReceived - TotalExpenses;
}

/// <summary>One item behind a number on the annual report.</summary>
public record ReportDetailLine(DateOnly Date, string Where, string Description, string? Party, decimal Amount, bool Shared);

/// <summary>
/// The items behind one report cell. For a single property, <see cref="SharedShare"/> is that property's portion of
/// expenses shared by all properties; the shared items themselves are listed (with <see cref="ReportDetailLine.Shared"/>)
/// at their full amounts for reference. <see cref="Total"/> equals the number on the report.
/// </summary>
public record ReportDetail(IReadOnlyList<ReportDetailLine> Lines, decimal SharedShare, decimal Total);

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

        return properties.Select(p =>
            {
                var all = expenses
                    .Where(e => e.PropertyId == p.Id)
                    .Select(e => (e.Category, e.Amount))
                    .Concat(shared.Where(s => s.PropertyId == p.Id).Select(s => (s.Category, s.Amount)))
                    .ToList();
                return new PropertyProfitAndLoss(
                    p.Id,
                    p.Name,
                    year,
                    payments.Where(r => r.PropertyId == p.Id).Sum(r => r.Amount),
                    p.Units.SelectMany(u => u.Leases).Sum(l => ScheduledRent(l, yearStart, yearEnd)),
                    all
                        .Where(e => e.Category.IsDeductible())
                        .GroupBy(e => e.Category)
                        .OrderBy(g => g.Key)
                        .Select(g => new CategoryTotal(g.Key, g.Sum(e => e.Amount)))
                        .ToList(),
                    shared.Where(s => s.PropertyId == p.Id && s.Category.IsDeductible()).Sum(s => s.Amount),
                    all
                        .Where(e => !e.Category.IsDeductible())
                        .GroupBy(e => e.Category)
                        .OrderBy(g => g.Key)
                        .Select(g => new CategoryTotal(g.Key, g.Sum(e => e.Amount)))
                        .ToList());
            })
            .ToList();
    }

    /// <summary>The expenses in a category (or, when <paramref name="category"/> is null, the rent received) behind a report cell.</summary>
    public async Task<ReportDetail> GetDetailAsync(int year, ExpenseCategory? category, int? propertyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);

        if (category is null)
        {
            var payments = (await db.RentPayments.AsNoTracking()
                    .Include(r => r.Lease!).ThenInclude(l => l.Tenants)
                    .Include(r => r.Lease!).ThenInclude(l => l.Unit!).ThenInclude(u => u.Property)
                    .Where(r => r.Date >= yearStart && r.Date <= yearEnd)
                    .ToListAsync(ct))
                .Where(r => propertyId is null || r.Lease!.Unit!.PropertyId == propertyId)
                .OrderBy(r => r.Date)
                .Select(r => new ReportDetailLine(
                    r.Date,
                    r.Lease!.Unit!.DisplayName,
                    $"{r.Lease.TenantNames} – rent for {r.ForMonth:MMMM yyyy}",
                    r.Method.ToString(),
                    r.Amount,
                    false))
                .ToList();
            return new ReportDetail(payments, 0m, payments.Sum(p => p.Amount));
        }

        var expenses = await db.Expenses.AsNoTracking()
            .Include(e => e.Property).Include(e => e.Unit).Include(e => e.Vendor)
            .Where(e => e.Date >= yearStart && e.Date <= yearEnd && e.Category == category)
            .ToListAsync(ct);

        static ReportDetailLine Line(Expense e) => new(
            e.Date,
            e.IsShared ? "All properties" : e.Unit is null ? e.Property!.Name : $"{e.Property!.Name} – Unit {e.Unit.Label}",
            e.Description,
            e.Vendor?.Name,
            e.Amount,
            e.IsShared);

        if (propertyId is null)
        {
            var all = expenses.OrderBy(e => e.Date).Select(Line).ToList();
            return new ReportDetail(all, 0m, all.Sum(l => l.Amount));
        }

        var properties = await db.Properties.AsNoTracking().Include(p => p.Units).OrderBy(p => p.Name).ToListAsync(ct);
        var share = AllocateSharedExpenses(properties, expenses.Where(e => e.IsShared))
            .Where(a => a.PropertyId == propertyId)
            .Sum(a => a.Amount);
        var lines = expenses
            .Where(e => e.PropertyId == propertyId || e.IsShared)
            .OrderBy(e => e.IsShared).ThenBy(e => e.Date)
            .Select(Line)
            .ToList();
        return new ReportDetail(lines, share, lines.Where(l => !l.Shared).Sum(l => l.Amount) + share);
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
