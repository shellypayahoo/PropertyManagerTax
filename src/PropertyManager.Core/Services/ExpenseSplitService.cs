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

        Split(db, original, nonEmpty);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Applies a split within an existing context (not saved). The original keeps the first part, and with it any
    /// link to an imported bank row; the other parts are added as copies with their own category and amount.
    /// </summary>
    public static IReadOnlyList<Expense> Split(PropertyManagerDbContext db, Expense original, IReadOnlyList<ExpenseSplitPart> parts)
    {
        original.Category = parts[0].Category;
        original.Amount = parts[0].Amount;
        var added = new List<Expense> { original };
        foreach (var part in parts.Skip(1))
        {
            var copy = new Expense
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
            };
            db.Expenses.Add(copy);
            added.Add(copy);
        }
        return added;
    }
}
