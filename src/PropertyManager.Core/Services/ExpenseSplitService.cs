using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Services;

public record ExpenseSplitPart(ExpenseCategory Category, decimal Amount);

/// <summary>
/// Splits one expense into several with different categories, e.g. a mortgage payment into interest,
/// principal, and escrow for taxes and insurance. The parts must add up to the original amount.
/// </summary>
public class ExpenseSplitService(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    public async Task SplitAsync(int expenseId, IReadOnlyList<ExpenseSplitPart> parts, CancellationToken ct = default)
    {
        var nonEmpty = parts.Where(p => p.Amount != 0m).ToList();
        if (nonEmpty.Count < 2)
        {
            throw new InvalidOperationException("Enter amounts for at least two parts.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var original = await db.Expenses.SingleAsync(e => e.Id == expenseId, ct);
        if (nonEmpty.Sum(p => p.Amount) != original.Amount)
        {
            throw new InvalidOperationException($"The parts add up to {nonEmpty.Sum(p => p.Amount):C} but the expense is {original.Amount:C}.");
        }

        // The original keeps the first part (and its link to any imported bank row); the rest are copies.
        original.Category = nonEmpty[0].Category;
        original.Amount = nonEmpty[0].Amount;
        foreach (var part in nonEmpty.Skip(1))
        {
            db.Expenses.Add(new Expense
            {
                PropertyId = original.PropertyId,
                UnitId = original.UnitId,
                VendorId = original.VendorId,
                MaintenanceRequestId = original.MaintenanceRequestId,
                Date = original.Date,
                Description = original.Description,
                PaidWith = original.PaidWith,
                Notes = original.Notes,
                Category = part.Category,
                Amount = part.Amount,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
