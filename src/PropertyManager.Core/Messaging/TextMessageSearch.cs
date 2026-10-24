using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Messaging;

/// <summary>Searches imported text messages. Every word in the query must appear (case-insensitive).</summary>
public class TextMessageSearch(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    public async Task<IReadOnlyList<TextMessage>> SearchAsync(
        string? query, string? contactHandle = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var messages = db.TextMessages.AsNoTracking().Include(m => m.Tenant).Include(m => m.Vendor).Include(m => m.Attachments).AsQueryable();
        if (contactHandle is not null)
        {
            messages = messages.Where(m => m.ContactHandle == contactHandle);
        }
        if (from is { } f)
        {
            messages = messages.Where(m => m.SentAt >= f);
        }
        if (to is { } t)
        {
            messages = messages.Where(m => m.SentAt <= t);
        }
        foreach (var word in (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = $"%{word.Replace("%", "").Replace("_", "")}%";
            messages = messages.Where(m => EF.Functions.Like(m.Text, pattern));
        }

        var newest = await messages.OrderByDescending(m => m.SentAt).Take(limit).ToListAsync(ct);
        newest.Reverse();
        return newest;
    }
}
