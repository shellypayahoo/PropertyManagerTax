using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Core.Reports;

/// <summary>Cash in and out for one month. Unlike the P&L, mortgage principal and escrow deposits count as money out.</summary>
public record CashFlowMonth(DateOnly Month, decimal RentIn, decimal Expenses, decimal LoanAndEscrow, decimal Cumulative)
{
    public decimal TotalOut => Expenses + LoanAndEscrow;
    public decimal Net => RentIn - TotalOut;
}

/// <summary>One lease's rent for one month in the collection grid.</summary>
public record RentCollectionCell(DateOnly Month, decimal Due, decimal Paid, RentStatus Status, int DaysLate)
{
    public bool IsLate => DaysLate > 0;
}

public record RentCollectionRow(Lease Lease, IReadOnlyList<RentCollectionCell> Months)
{
    public decimal Due => Months.Where(m => m.Status != RentStatus.Upcoming).Sum(m => m.Due);
    public decimal Paid => Months.Sum(m => m.Paid);
    public decimal Balance => Due - Paid;
    public int LateMonths => Months.Count(m => m.IsLate);
    public int MissedMonths => Months.Count(m => m.Status == RentStatus.Unpaid && m.IsLate);
    public int PartialMonths => Months.Count(m => m.Status == RentStatus.Partial);
}

/// <summary>Spending with one vendor (or, with no vendor, expenses recorded without one).</summary>
public record VendorSpending(int? VendorId, string Name, string? Trade, IReadOnlyList<Expense> Items)
{
    public decimal Total => Items.Sum(e => e.Amount);
    public IReadOnlyList<string> Categories => Items.Select(e => e.Category.DisplayName()).Distinct().Order().ToList();
    public IReadOnlyList<string> Properties => Items.Select(e => e.IsShared ? "All properties" : e.Property?.Name ?? "").Distinct().Order().ToList();
}

/// <summary>Cash flow, rent collection, and vendor spending reports.</summary>
public class OperatingReports(IDbContextFactory<PropertyManagerDbContext> dbFactory, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>
    /// Month by month for a year: rent received (by date received) and money paid out (by date paid). For one property,
    /// expenses shared by all properties count by its share of units.
    /// </summary>
    public async Task<IReadOnlyList<CashFlowMonth>> GetCashFlowAsync(int year, int? propertyId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (start, end) = (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        var properties = await db.Properties.AsNoTracking().Include(p => p.Units).OrderBy(p => p.Name).ToListAsync(ct);
        var payments = await db.RentPayments.AsNoTracking()
            .Where(r => r.Date >= start && r.Date <= end)
            .Select(r => new { r.Date, r.Amount, r.Lease!.Unit!.PropertyId })
            .ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.Date >= start && e.Date <= end).ToListAsync(ct);

        var months = new List<CashFlowMonth>();
        var cumulative = 0m;
        for (var month = start; month <= end; month = month.AddMonths(1))
        {
            var inMonth = expenses.Where(e => e.Date.Year == month.Year && e.Date.Month == month.Month).ToList();
            var counted = inMonth
                .Where(e => propertyId is null || e.PropertyId == propertyId)
                .Select(e => (e.Category, e.Amount))
                .ToList();
            if (propertyId is not null)
            {
                counted.AddRange(ReportService.AllocateSharedExpenses(properties, inMonth.Where(e => e.IsShared))
                    .Where(a => a.PropertyId == propertyId)
                    .Select(a => (a.Category, a.Amount)));
            }

            var rent = payments.Where(p => p.Date.Year == month.Year && p.Date.Month == month.Month && (propertyId is null || p.PropertyId == propertyId)).Sum(p => p.Amount);
            var loan = counted.Where(e => e.Category is ExpenseCategory.MortgagePrincipal or ExpenseCategory.EscrowDeposit).Sum(e => e.Amount);
            var other = counted.Sum(e => e.Amount) - loan;
            cumulative += rent - other - loan;
            months.Add(new CashFlowMonth(month, rent, other, loan, cumulative));
        }
        return months;
    }

    /// <summary>
    /// Each lease that covers part of the year, month by month. Rent is due on the 1st; a month counts as late when it
    /// was paid in full after the 1st plus <paramref name="graceDays"/>, or isn't paid in full yet and that day has passed.
    /// </summary>
    public async Task<IReadOnlyList<RentCollectionRow>> GetRentCollectionAsync(int year, int graceDays = 5, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var leases = await db.Leases.AsNoTracking()
            .Include(l => l.Tenants).Include(l => l.Payments).Include(l => l.Unit).ThenInclude(u => u!.Property)
            .ToListAsync(ct);
        var today = Today;

        var rows = new List<RentCollectionRow>();
        foreach (var lease in leases.OrderBy(l => l.Unit?.Property?.Name).ThenBy(l => l.Unit?.Label).ThenBy(l => l.StartDate))
        {
            var months = Enumerable.Range(1, 12)
                .Select(m => RentSchedule.ForMonth(lease, new DateOnly(year, m, 1), today))
                .Where(r => r.Due > 0m || r.Payments.Count > 0)
                .Select(r => new RentCollectionCell(r.Month, r.Due, r.Paid, r.Status, DaysLate(r, graceDays, today)))
                .ToList();
            if (months.Count > 0)
            {
                rows.Add(new RentCollectionRow(lease, months));
            }
        }
        return rows;
    }

    private static int DaysLate(RentMonth r, int graceDays, DateOnly today)
    {
        if (r.Due == 0m)
        {
            return 0;
        }
        var lateAfter = r.Month.AddDays(graceDays);
        // The day the month became paid in full, or today if it still isn't.
        DateOnly? paidInFull = null;
        var running = 0m;
        foreach (var p in r.Payments.OrderBy(p => p.Date))
        {
            running += p.Amount;
            if (running >= r.Due)
            {
                paidInFull = p.Date;
                break;
            }
        }
        var settled = paidInFull ?? today;
        return settled > lateAfter ? settled.DayNumber - r.Month.DayNumber : 0;
    }

    /// <summary>Spending per vendor between two dates, largest first; expenses without a vendor are grouped last.</summary>
    public async Task<IReadOnlyList<VendorSpending>> GetVendorSpendingAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expenses = await db.Expenses.AsNoTracking()
            .Include(e => e.Vendor).Include(e => e.Property).Include(e => e.Unit)
            .Where(e => e.Date >= from && e.Date <= to)
            .ToListAsync(ct);

        var byVendor = expenses
            .Where(e => e.Vendor is not null)
            .GroupBy(e => e.VendorId)
            .Select(g => new VendorSpending(g.Key, g.First().Vendor!.Name, g.First().Vendor!.Trade, g.OrderBy(e => e.Date).ToList()))
            .OrderByDescending(v => v.Total)
            .ToList();
        var none = expenses.Where(e => e.Vendor is null).OrderBy(e => e.Date).ToList();
        if (none.Count > 0)
        {
            byVendor.Add(new VendorSpending(null, "No vendor recorded", null, none));
        }
        return byVendor;
    }
}
