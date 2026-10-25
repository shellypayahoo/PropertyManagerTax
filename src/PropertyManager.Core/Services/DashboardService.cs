using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Services;

public record DashboardSummary(
    int PropertyCount,
    int UnitCount,
    int OccupiedUnitCount,
    decimal MonthlyLeaseRent,
    decimal MonthlyMarketRent,
    int OpenMaintenanceCount,
    int OpenLowPriorityMaintenanceCount,
    decimal ExpensesYearToDate,
    int LeasesPaidThisMonth,
    int LeasesDueThisMonth);

public class DashboardService(IDbContextFactory<PropertyManagerDbContext> dbFactory, TimeProvider clock)
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var yearStart = new DateOnly(today.Year, 1, 1);

        var thisMonth = Entities.RentPayment.MonthOf(today);
        var units = await db.Units.AsNoTracking()
            .Include(u => u.Leases).ThenInclude(l => l.Payments.Where(p => p.ForMonth == thisMonth))
            .ToListAsync(ct);
        var rentThisMonth = units.SelectMany(u => u.Leases)
            .Select(l => RentSchedule.ForMonth(l, thisMonth, today))
            .Where(r => r.Status != RentStatus.NotDue)
            .ToList();
        var activeLeases = units.Select(u => u.ActiveLease(today)).OfType<Entities.Lease>().ToList();

        var openMaintenance = (await db.MaintenanceRequests.AsNoTracking()
                .Select(m => new { m.Status, m.Priority })
                .ToListAsync(ct))
            .Where(m => m.Status is not (Entities.MaintenanceStatus.Completed or Entities.MaintenanceStatus.Cancelled))
            .ToList();

        var ytdExpenses = await db.Expenses.AsNoTracking()
            .Where(e => e.Date >= yearStart && e.Date <= today)
            .Select(e => new { e.Amount, e.Category })
            .ToListAsync(ct);

        return new DashboardSummary(
            PropertyCount: await db.Properties.CountAsync(ct),
            UnitCount: units.Count,
            OccupiedUnitCount: activeLeases.Count,
            MonthlyLeaseRent: activeLeases.Sum(l => l.MonthlyRent),
            MonthlyMarketRent: units.Sum(u => u.MarketRent),
            // Urgent and normal ones, as listed in the Dashboard's unit cards; low priority is counted separately.
            OpenMaintenanceCount: openMaintenance.Count(m => m.Priority != Entities.MaintenancePriority.Low),
            OpenLowPriorityMaintenanceCount: openMaintenance.Count(m => m.Priority == Entities.MaintenancePriority.Low),
            ExpensesYearToDate: ytdExpenses.Where(e => e.Category.IsDeductible()).Sum(e => e.Amount),
            LeasesPaidThisMonth: rentThisMonth.Count(r => r.Status == RentStatus.Paid),
            LeasesDueThisMonth: rentThisMonth.Count);
    }
}
