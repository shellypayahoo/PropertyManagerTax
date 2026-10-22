using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Services;

/// <summary>Creates the monthly expenses for recurring expenses, catching up on any months that were missed.</summary>
public class RecurringExpenseService(IDbContextFactory<PropertyManagerDbContext> dbFactory, TimeProvider clock)
{
    /// <summary>Adds every occurrence dated on or before today that hasn't been generated yet. Returns how many were added.</summary>
    public async Task<int> GenerateDueAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var recurring = await db.RecurringExpenses
            .Include(r => r.Targets).ThenInclude(t => t.Unit)
            .Include(r => r.Vendor)
            .Where(r => !r.IsPaused)
            .ToListAsync(ct);
        var done = (await db.RecurringExpenseRuns.Select(r => new { r.TargetId, r.Month }).ToListAsync(ct))
            .Select(r => (r.TargetId, r.Month))
            .ToHashSet();

        var added = 0;
        foreach (var r in recurring)
        {
            foreach (var target in r.Targets)
            {
                var first = RentPayment.MonthOf(target.StartMonth > r.StartMonth ? target.StartMonth : r.StartMonth);
                for (var month = first; r.OccurrenceIn(month) <= today; month = month.AddMonths(1))
                {
                    var date = r.OccurrenceIn(month);
                    if (r.EndDate is { } end && date > end)
                    {
                        break;
                    }
                    if (done.Contains((target.Id, month)))
                    {
                        continue;
                    }

                    var expense = new Expense
                    {
                        PropertyId = target.PropertyId,
                        UnitId = target.PropertyId is null ? null : target.UnitId,
                        VendorId = r.VendorId,
                        Date = date,
                        Category = r.Category,
                        Amount = r.Amount,
                        PaidWith = r.PaidWith,
                        Description = target.Unit is null ? r.Name : $"{r.Name} – Unit {target.Unit.Label}",
                        Notes = $"Recurring: {r.Name}",
                    };
                    db.Expenses.Add(expense);
                    db.RecurringExpenseRuns.Add(new RecurringExpenseRun { TargetId = target.Id, Month = month, Expense = expense });
                    done.Add((target.Id, month));
                    added++;
                }
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return added;
    }

    /// <summary>The next date this recurring expense will be added, or null if it's paused or has ended.</summary>
    public static DateOnly? NextOccurrence(RecurringExpense r, DateOnly today)
    {
        if (r.IsPaused)
        {
            return null;
        }
        var month = RentPayment.MonthOf(today > r.StartMonth ? today : r.StartMonth);
        var next = r.OccurrenceIn(month);
        if (next <= today)
        {
            next = r.OccurrenceIn(month.AddMonths(1));
        }
        return r.EndDate is { } end && next > end ? null : next;
    }
}
