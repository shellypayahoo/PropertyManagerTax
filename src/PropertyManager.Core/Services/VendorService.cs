using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;

namespace PropertyManager.Core.Services;

/// <summary>Where a vendor is used, so deleting or merging it can say what will change.</summary>
public record VendorUsage(int Expenses, int RecurringExpenses, int MaintenanceRequests, int ImportRules, int TextMessages, int Emails)
{
    public bool IsUnused => Expenses + RecurringExpenses + MaintenanceRequests + ImportRules + TextMessages + Emails == 0;

    public override string ToString()
    {
        var parts = new List<string>();
        void Add(int count, string one, string many)
        {
            if (count > 0) parts.Add($"{count} {(count == 1 ? one : many)}");
        }
        Add(Expenses, "expense", "expenses");
        Add(RecurringExpenses, "recurring expense", "recurring expenses");
        Add(MaintenanceRequests, "maintenance request", "maintenance requests");
        Add(ImportRules, "import rule", "import rules");
        Add(TextMessages, "text message", "text messages");
        Add(Emails, "email", "emails");
        return parts.Count == 0 ? "not used anywhere" : string.Join(", ", parts);
    }
}

/// <summary>Deletes vendors and merges a duplicate vendor into the one to keep.</summary>
public class VendorService(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    public async Task<VendorUsage> GetUsageAsync(int vendorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new VendorUsage(
            await db.Expenses.CountAsync(e => e.VendorId == vendorId, ct),
            await db.RecurringExpenses.CountAsync(r => r.VendorId == vendorId, ct),
            await db.MaintenanceRequests.CountAsync(m => m.VendorId == vendorId, ct),
            await db.CategorizationRules.CountAsync(r => r.VendorId == vendorId, ct),
            await db.TextMessages.CountAsync(m => m.VendorId == vendorId, ct),
            await db.EmailMessages.CountAsync(m => m.VendorId == vendorId, ct));
    }

    /// <summary>Deletes a vendor. Anything that used it is kept, with no vendor.</summary>
    public async Task DeleteAsync(int vendorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ReassignAsync(db, vendorId, null, ct);
        await db.Vendors.Where(v => v.Id == vendorId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Moves everything that uses <paramref name="duplicateId"/> to <paramref name="keepId"/>, fills in any phone,
    /// email, trade, or notes the kept vendor is missing, and deletes the duplicate.
    /// </summary>
    public async Task MergeAsync(int duplicateId, int keepId, CancellationToken ct = default)
    {
        if (duplicateId == keepId)
        {
            throw new ArgumentException("Choose a different vendor to merge into.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var duplicate = await db.Vendors.FindAsync([duplicateId], ct) ?? throw new InvalidOperationException("That vendor no longer exists.");
        var keep = await db.Vendors.FindAsync([keepId], ct) ?? throw new InvalidOperationException("The vendor to keep no longer exists.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        keep.Trade = Fill(keep.Trade, duplicate.Trade);
        keep.Phone = Fill(keep.Phone, duplicate.Phone);
        keep.Email = Fill(keep.Email, duplicate.Email);
        keep.Notes = string.IsNullOrWhiteSpace(duplicate.Notes) || keep.Notes?.Contains(duplicate.Notes) == true
            ? keep.Notes
            : string.IsNullOrWhiteSpace(keep.Notes) ? duplicate.Notes : $"{keep.Notes}\n{duplicate.Notes}";
        await db.SaveChangesAsync(ct);

        await ReassignAsync(db, duplicateId, keepId, ct);
        await db.Vendors.Where(v => v.Id == duplicateId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string? Fill(string? current, string? other) => string.IsNullOrWhiteSpace(current) ? other : current;

    private static async Task ReassignAsync(PropertyManagerDbContext db, int fromId, int? toId, CancellationToken ct)
    {
        await db.Expenses.Where(e => e.VendorId == fromId).ExecuteUpdateAsync(s => s.SetProperty(e => e.VendorId, toId), ct);
        await db.RecurringExpenses.Where(e => e.VendorId == fromId).ExecuteUpdateAsync(s => s.SetProperty(e => e.VendorId, toId), ct);
        await db.MaintenanceRequests.Where(e => e.VendorId == fromId).ExecuteUpdateAsync(s => s.SetProperty(e => e.VendorId, toId), ct);
        await db.CategorizationRules.Where(e => e.VendorId == fromId).ExecuteUpdateAsync(s => s.SetProperty(e => e.VendorId, toId), ct);
        await db.TextMessages.Where(e => e.VendorId == fromId).ExecuteUpdateAsync(s => s.SetProperty(e => e.VendorId, toId), ct);
        await db.EmailMessages.Where(e => e.VendorId == fromId).ExecuteUpdateAsync(s => s.SetProperty(e => e.VendorId, toId), ct);
        await db.MessageContacts.Where(e => e.VendorId == fromId).ExecuteUpdateAsync(s => s.SetProperty(e => e.VendorId, toId), ct);
    }
}
