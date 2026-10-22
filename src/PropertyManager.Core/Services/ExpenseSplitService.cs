using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Services;

/// <summary>Where a split part is recorded: a property (null = shared by all properties) and optionally a unit.</summary>
public record ExpenseLocation(int? PropertyId, int? UnitId);

/// <summary>One part of a split. A null <see cref="Location"/> keeps the original expense's property and unit.</summary>
public record ExpenseSplitPart(ExpenseCategory Category, decimal Amount, ExpenseLocation? Location = null);

/// <summary>
/// Splits one expense into several with different categories and/or locations, e.g. a mortgage payment into
/// interest, principal, and escrow, or a cleaning bill across the units it covered. The parts must add up to
/// the original amount.
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

    /// <summary>Divides an amount evenly into <paramref name="count"/> parts; the rounding pennies go to the last part.</summary>
    public static IReadOnlyList<decimal> DivideEvenly(decimal amount, int count)
    {
        if (count < 1)
        {
            return [];
        }
        var share = Math.Round(amount / count, 2, MidpointRounding.ToZero);
        return Enumerable.Repeat(share, count - 1).Append(amount - share * (count - 1)).ToList();
    }

    /// <summary>
    /// Applies a split within an existing context (not saved). The original keeps the first part, and with it any
    /// link to an imported bank row; the other parts are added as copies with their own category and amount.
    /// </summary>
    public static IReadOnlyList<Expense> Split(PropertyManagerDbContext db, Expense original, IReadOnlyList<ExpenseSplitPart> parts)
    {
        var originalLocation = new ExpenseLocation(original.PropertyId, original.UnitId);
        original.Category = parts[0].Category;
        original.Amount = parts[0].Amount;
        Place(original, parts[0].Location ?? originalLocation);
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
            Place(copy, part.Location ?? originalLocation);
            db.Expenses.Add(copy);
            added.Add(copy);
        }
        return added;
    }

    private static void Place(Expense expense, ExpenseLocation location)
    {
        expense.PropertyId = location.PropertyId;
        expense.UnitId = location.PropertyId is null ? null : location.UnitId;
    }
}
