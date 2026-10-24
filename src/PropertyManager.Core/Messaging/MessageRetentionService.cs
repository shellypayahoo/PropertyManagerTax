using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Messaging;

public record RetentionResult(int TextsRemoved, int EmailsRemoved);

/// <summary>
/// "Keep texts and emails until" for former tenants and vendors: imports and email syncs skip anything with them after
/// the date, and setting the date removes what's already in the app after it.
/// </summary>
public class MessageRetentionService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IDocumentStorage storage)
{
    /// <summary>The last moment kept: the end of the day, in this computer's time zone.</summary>
    public static DateTimeOffset Cutoff(DateOnly keepUntil)
    {
        var nextMidnight = keepUntil.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(nextMidnight, TimeZoneInfo.Local.GetUtcOffset(nextMidnight)).AddTicks(-1);
    }

    /// <summary>
    /// For a tenant with no current or future lease, the last lease's end plus a month (to cover the deposit and
    /// move-out messages); otherwise null. Requires <see cref="Tenant.Leases"/>.
    /// </summary>
    public static DateOnly? Suggest(Tenant tenant, DateOnly today) =>
        tenant.Leases.Count > 0 && tenant.Leases.All(l => l.EndDate is { } end && end < today)
            ? tenant.Leases.Max(l => l.EndDate!.Value).AddMonths(1)
            : null;

    /// <summary>Cutoffs by tenant and by vendor, for imports and syncs.</summary>
    public static async Task<(Dictionary<int, DateTimeOffset> Tenants, Dictionary<int, DateTimeOffset> Vendors)> CutoffsAsync(PropertyManagerDbContext db, CancellationToken ct)
    {
        var tenants = await db.Tenants.Where(t => t.KeepMessagesUntil != null).Select(t => new { t.Id, t.KeepMessagesUntil }).ToListAsync(ct);
        var vendors = await db.Vendors.Where(v => v.KeepMessagesUntil != null).Select(v => new { v.Id, v.KeepMessagesUntil }).ToListAsync(ct);
        return (tenants.ToDictionary(t => t.Id, t => Cutoff(t.KeepMessagesUntil!.Value)), vendors.ToDictionary(v => v.Id, v => Cutoff(v.KeepMessagesUntil!.Value)));
    }

    /// <summary>True when a message with this tenant/vendor at this time is after their keep-until date.</summary>
    public static bool IsAfterCutoff((Dictionary<int, DateTimeOffset> Tenants, Dictionary<int, DateTimeOffset> Vendors) cutoffs, int? tenantId, int? vendorId, DateTimeOffset sentAt) =>
        (tenantId is { } t && cutoffs.Tenants.TryGetValue(t, out var tc) && sentAt > tc)
        || (vendorId is { } v && cutoffs.Vendors.TryGetValue(v, out var vc) && sentAt > vc);

    /// <summary>
    /// Sets or clears the date for a tenant or vendor and removes their texts and emails after it (with attachment
    /// files). When the date moves later or is cleared, the next email sync fetches the folders again so emails
    /// skipped earlier come back; texts come back on the next import anyway.
    /// </summary>
    public async Task<RetentionResult> SetKeepUntilAsync(int? tenantId, int? vendorId, DateOnly? keepUntil, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        DateOnly? before;
        if (tenantId is not null)
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
            before = tenant.KeepMessagesUntil;
            tenant.KeepMessagesUntil = keepUntil;
        }
        else
        {
            var vendor = await db.Vendors.SingleAsync(v => v.Id == vendorId, ct);
            before = vendor.KeepMessagesUntil;
            vendor.KeepMessagesUntil = keepUntil;
        }
        if (before is not null && (keepUntil is null || keepUntil > before))
        {
            await db.EmailFolders.ExecuteUpdateAsync(u => u.SetProperty(f => f.DeltaLink, (string?)null), ct);
        }
        await db.SaveChangesAsync(ct);
        if (keepUntil is null)
        {
            return new RetentionResult(0, 0);
        }

        var cutoff = Cutoff(keepUntil.Value);
        var texts = await db.TextMessages.Include(m => m.Attachments)
            .Where(m => (tenantId != null ? m.TenantId == tenantId : m.VendorId == vendorId) && m.SentAt > cutoff).ToListAsync(ct);
        var emails = await db.EmailMessages.Include(m => m.Attachments)
            .Where(m => (tenantId != null ? m.TenantId == tenantId : m.VendorId == vendorId) && m.SentAt > cutoff).ToListAsync(ct);

        foreach (var key in texts.SelectMany(m => m.Attachments).SelectMany(a => new[] { a.StorageKey, a.PreviewKey, a.ThumbnailKey })
                     .Concat(emails.SelectMany(m => m.Attachments).Select(a => a.StorageKey)).OfType<string>())
        {
            await storage.DeleteAsync(key, ct);
        }
        db.TextMessages.RemoveRange(texts);
        db.EmailMessages.RemoveRange(emails);
        await db.SaveChangesAsync(ct);
        return new RetentionResult(texts.Count, emails.Count);
    }
}
