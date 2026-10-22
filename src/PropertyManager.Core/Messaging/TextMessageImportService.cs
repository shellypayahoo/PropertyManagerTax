using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Messaging;

public record ContactScanSummary(int Contacts, int NewContacts, int Included);

public record TextImportSummary(int ChatsMatched, int MessagesAdded, int ContactsIncluded);

/// <summary>
/// Brings text conversations from the Mac's Messages database into the app, only for contacts the user has chosen.
/// <see cref="ScanContactsAsync"/> lists everyone in Messages (names from Contacts, no message text);
/// <see cref="ImportAsync"/> copies the conversations of included contacts. Both are safe to run repeatedly.
/// </summary>
public class TextMessageImportService(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    /// <summary>
    /// Refreshes the contact list from Messages. New contacts are included automatically when their phone or email
    /// belongs to a tenant or vendor; existing choices are kept.
    /// </summary>
    public async Task<ContactScanSummary> ScanContactsAsync(string messagesDatabasePath, string? addressBookFolder, CancellationToken ct = default)
    {
        IReadOnlyList<MessagesHandleStats> stats;
        using (var reader = MessagesDatabaseReader.Open(messagesDatabasePath))
        {
            stats = reader.ReadHandleStats();
        }
        var names = addressBookFolder is null ? new Dictionary<string, string>() : AddressBookReader.ReadNames(addressBookFolder);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var people = await KnownPeopleAsync(db, ct);
        var contacts = await db.MessageContacts.ToDictionaryAsync(c => c.Handle, ct);

        var added = 0;
        foreach (var group in stats.GroupBy(s => ContactHandles.Normalize(s.Handle)).Where(g => g.Key is not null))
        {
            var handle = group.Key!;
            if (!contacts.TryGetValue(handle, out var contact))
            {
                people.TryGetValue(handle, out var who);
                contact = new MessageContact { Handle = handle, TenantId = who.TenantId, VendorId = who.VendorId, Included = who != default };
                db.MessageContacts.Add(contact);
                contacts[handle] = contact;
                added++;
            }
            else if (contact.TenantId is null && contact.VendorId is null && people.TryGetValue(handle, out var who))
            {
                // Someone added as a tenant/vendor since the last scan.
                contact.TenantId = who.TenantId;
                contact.VendorId = who.VendorId;
            }

            contact.Name = names.GetValueOrDefault(handle) ?? contact.Name;
            contact.MessageCount = group.Sum(s => s.MessageCount);
            contact.LastMessageAt = group.Max(s => s.LastMessageAt);
        }

        await db.SaveChangesAsync(ct);
        return new ContactScanSummary(contacts.Count, added, contacts.Values.Count(c => c.Included));
    }

    /// <summary>Copies conversations that include at least one included contact. Messages already imported are skipped.</summary>
    public async Task<TextImportSummary> ImportAsync(string messagesDatabasePath, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var included = await db.MessageContacts.AsNoTracking().Where(c => c.Included).ToDictionaryAsync(c => c.Handle, ct);
        if (included.Count == 0)
        {
            return new TextImportSummary(0, 0, 0);
        }

        using var reader = MessagesDatabaseReader.Open(messagesDatabasePath);
        var matched = reader.ReadChats()
            .Select(c => (Chat: c, Contacts: c.Participants.Select(ContactHandles.Normalize).OfType<string>().Where(included.ContainsKey).Distinct().ToList()))
            .Where(x => x.Contacts.Count > 0)
            .ToDictionary(x => x.Chat.Id);

        var existing = (await db.TextMessages.Select(m => m.Guid).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var row in reader.ReadMessages(matched.Keys))
        {
            if (!existing.Add(row.Guid))
            {
                continue;
            }

            var (chat, contacts) = matched[row.ChatId];
            // Incoming: file under the sender if included; otherwise (sent by you, or by someone else in a group) the chat's first included contact.
            var sender = ContactHandles.Normalize(row.SenderHandle);
            var handle = !row.IsFromMe && sender is not null && included.ContainsKey(sender) ? sender : contacts[0];
            var contact = included[handle];

            db.TextMessages.Add(new TextMessage
            {
                Guid = row.Guid,
                ChatIdentifier = chat.Identifier,
                IsGroupChat = chat.IsGroup,
                SenderHandle = row.IsFromMe ? null : row.SenderHandle,
                IsFromMe = row.IsFromMe,
                SentAt = row.SentAt,
                Text = row.Text,
                AttachmentCount = row.AttachmentCount,
                ContactHandle = handle,
                TenantId = contact.TenantId,
                VendorId = contact.VendorId,
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        return new TextImportSummary(matched.Count, added, included.Count);
    }

    /// <summary>Includes or excludes a contact. Excluding removes that contact's imported messages from the app.</summary>
    public async Task SetIncludedAsync(int contactId, bool included, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.MessageContacts.SingleAsync(c => c.Id == contactId, ct);
        contact.Included = included;
        await db.SaveChangesAsync(ct);
        if (!included)
        {
            await db.TextMessages.Where(m => m.ContactHandle == contact.Handle).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>Links a contact to a tenant or vendor (or neither), updating its imported messages.</summary>
    public async Task LinkAsync(int contactId, int? tenantId, int? vendorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.MessageContacts.SingleAsync(c => c.Id == contactId, ct);
        contact.TenantId = tenantId;
        contact.VendorId = tenantId is null ? vendorId : null;
        await db.SaveChangesAsync(ct);
        await db.TextMessages.Where(m => m.ContactHandle == contact.Handle)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.TenantId, contact.TenantId).SetProperty(m => m.VendorId, contact.VendorId), ct);
    }

    private static async Task<Dictionary<string, (int? TenantId, int? VendorId)>> KnownPeopleAsync(PropertyManagerDbContext db, CancellationToken ct)
    {
        var people = new Dictionary<string, (int? TenantId, int? VendorId)>();
        foreach (var t in await db.Tenants.AsNoTracking().ToListAsync(ct))
        {
            foreach (var h in new[] { t.Phone, t.Email }.Select(ContactHandles.Normalize).OfType<string>())
            {
                people.TryAdd(h, (t.Id, null));
            }
        }
        foreach (var v in await db.Vendors.AsNoTracking().ToListAsync(ct))
        {
            foreach (var h in new[] { v.Phone, v.Email }.Select(ContactHandles.Normalize).OfType<string>())
            {
                people.TryAdd(h, (null, v.Id));
            }
        }
        return people;
    }
}
