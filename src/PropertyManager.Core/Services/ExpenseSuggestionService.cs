using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Services;

/// <summary>Turns approved <see cref="ExpenseSuggestion"/>s into expenses, or sets them aside.</summary>
public class ExpenseSuggestionService(IDbContextFactory<PropertyManagerDbContext> dbFactory, ReceiptService receipts, IDocumentStorage storage)
{
    public async Task<List<ExpenseSuggestion>> PendingAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExpenseSuggestions.AsNoTracking()
            .Where(s => s.Status == SuggestionStatus.Pending)
            .Include(s => s.Vendor)
            .Include(s => s.Property)
            .Include(s => s.Unit)
            .Include(s => s.TextMessage)
            .Include(s => s.EmailMessage)
            .Include(s => s.TextAttachment)
            .Include(s => s.EmailAttachment)
            .Include(s => s.DuplicateOfExpense)
            .OrderBy(s => s.Date).ThenBy(s => s.Id)
            .ToListAsync(ct);
    }

    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExpenseSuggestions.CountAsync(s => s.Status == SuggestionStatus.Pending, ct);
    }

    /// <summary>
    /// Adds the expense, with the owner's changes in <paramref name="edited"/> (date, amount, description, category, property,
    /// unit, vendor, paid with), and saves the attached invoice as its receipt. Returns the new expense, or an error to show.
    /// </summary>
    public async Task<(Expense? Expense, string? Error)> ApproveAsync(int suggestionId, ExpenseSuggestion edited, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var suggestion = await db.ExpenseSuggestions
            .Include(s => s.TextAttachment)
            .Include(s => s.EmailAttachment)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (suggestion is null || suggestion.Status != SuggestionStatus.Pending)
        {
            return (null, "That suggestion was already handled.");
        }
        if (edited.Amount <= 0 || string.IsNullOrWhiteSpace(edited.Description))
        {
            return (null, "Give a description and an amount over zero.");
        }
        if (edited.NeedsProperty)
        {
            return (null, "Choose the property (or All properties).");
        }

        var expense = new Expense
        {
            Date = edited.Date,
            Amount = edited.Amount,
            Description = edited.Description.Trim(),
            Category = edited.Category,
            PropertyId = edited.IsShared ? null : edited.PropertyId,
            UnitId = edited.IsShared ? null : edited.UnitId,
            VendorId = edited.VendorId,
            PaidWith = edited.PaidWith,
            Notes = $"From {(suggestion.TextMessageId is not null ? "a text" : "an email")}: \"{suggestion.Evidence}\"",
        };
        db.Expenses.Add(expense);
        suggestion.Status = SuggestionStatus.Approved;
        await db.SaveChangesAsync(ct);
        suggestion.ExpenseId = expense.Id;
        await db.SaveChangesAsync(ct);

        var (fileName, key) = suggestion.TextAttachment is { StorageKey: { } textKey } t ? (t.FileName, textKey)
            : suggestion.EmailAttachment is { } e ? (e.FileName, e.StorageKey)
            : (null, null);
        if (fileName is not null && key is not null && await storage.OpenReadAsync(key, ct) is { } stream)
        {
            await using (stream)
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, ct);
                var bytes = copy.ToArray();
                await receipts.AddAsync(expense.Id, [(fileName, bytes.LongLength, () => new MemoryStream(bytes))], ct);
            }
        }
        return (expense, null);
    }

    public async Task DismissAsync(int suggestionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ExpenseSuggestions.Where(s => s.Id == suggestionId && s.Status == SuggestionStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SuggestionStatus.Dismissed), ct);
    }
}
