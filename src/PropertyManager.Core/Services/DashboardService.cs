using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;

namespace PropertyManager.Core.Services;

public record DashboardSummary(
    int PropertyCount,
    int UnitCount,
    int OccupiedUnitCount,
    decimal MonthlyLeaseRent,
    decimal MonthlyMarketRent,
    int OpenMaintenanceCount,
    decimal ExpensesYearToDate);

public class DashboardService(IDbContextFactory<PropertyManagerDbContext> dbFactory, TimeProvider clock)
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var yearStart = new DateOnly(today.Year, 1, 1);

        var units = await db.Units.AsNoTracking().Include(u => u.Leases).ToListAsync(ct);
        var activeLeases = units.Select(u => u.ActiveLease(today)).OfType<Entities.Lease>().ToList();

        var openMaintenance = await db.MaintenanceRequests.AsNoTracking()
            .Select(m => m.Status)
            .ToListAsync(ct);

        var ytdExpenses = await db.Expenses.AsNoTracking()
            .Where(e => e.Date >= yearStart && e.Date <= today)
            .Select(e => e.Amount)
            .ToListAsync(ct);

        return new DashboardSummary(
            PropertyCount: await db.Properties.CountAsync(ct),
            UnitCount: units.Count,
            OccupiedUnitCount: activeLeases.Count,
            MonthlyLeaseRent: activeLeases.Sum(l => l.MonthlyRent),
            MonthlyMarketRent: units.Sum(u => u.MarketRent),
            OpenMaintenanceCount: openMaintenance.Count(s => s is not (Entities.MaintenanceStatus.Completed or Entities.MaintenanceStatus.Cancelled)),
            ExpensesYearToDate: ytdExpenses.Sum());
    }
}
