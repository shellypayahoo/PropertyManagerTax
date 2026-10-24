using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Documents;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Messaging;

public record ContactScanSummary(int Contacts, int NewContacts, int Included);

public record TextImportSummary(int ChatsMatched, int MessagesAdded, int ContactsIncluded, int AttachmentsSaved = 0, int AttachmentsNotOnMac = 0, int AttachmentsTooLarge = 0);

/// <summary>
/// Brings text conversations from the Mac's Messages database into the app, only for contacts the user has chosen.
/// <see cref="ScanContactsAsync"/> lists everyone in Messages (names from Contacts, no message text);
/// <see cref="ImportAsync"/> copies the conversations of included contacts. Both are safe to run repeatedly.
/// </summary>
public class TextMessageImportService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IDocumentStorage storage, IImageConverter? images = null)
{
    public const long DefaultMaxAttachmentBytes = 50L * 1024 * 1024;
    public const int PreviewPixels = 1600;
    public const int ThumbnailPixels = 360;

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

    /// <summary>
    /// Copies conversations that include at least one included contact, with their photos and files (up to
    /// <paramref name="maxAttachmentBytes"/> each). Messages already imported are skipped, but their attachments are
    /// filled in if they weren't copied before, including files Messages has since downloaded to the Mac.
    /// </summary>
    public async Task<TextImportSummary> ImportAsync(string messagesDatabasePath, long maxAttachmentBytes = DefaultMaxAttachmentBytes, CancellationToken ct = default)
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

        var existing = await db.TextMessages.AsNoTracking().ToDictionaryAsync(m => m.Guid, ct);
        var cutoffs = await MessageRetentionService.CutoffsAsync(db, ct);
        var rows = reader.ReadMessages(matched.Keys);
        var withFiles = new Dictionary<long, TextMessage>();
        var added = 0;
        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.Guid, out var known))
            {
                if (row.AttachmentCount > 0)
                {
                    withFiles[row.RowId] = known;
                }
                continue;
            }

            var (chat, contacts) = matched[row.ChatId];
            // Incoming: file under the sender if included; otherwise (sent by you, or by someone else in a group) the chat's first included contact.
            var sender = ContactHandles.Normalize(row.SenderHandle);
            var handle = !row.IsFromMe && sender is not null && included.ContainsKey(sender) ? sender : contacts[0];
            var contact = included[handle];
            if (MessageRetentionService.IsAfterCutoff(cutoffs, contact.TenantId, contact.VendorId, row.SentAt))
            {
                // After this former tenant's or vendor's keep-until date.
                continue;
            }

            var message = new TextMessage
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
            };
            db.TextMessages.Add(message);
            existing[row.Guid] = message;
            if (row.AttachmentCount > 0)
            {
                withFiles[row.RowId] = message;
            }
            added++;
        }
        await db.SaveChangesAsync(ct);

        var (saved, notOnMac, tooLarge) = await ImportAttachmentsAsync(db, reader.ReadAttachments(withFiles.Keys), withFiles, maxAttachmentBytes, ct);
        return new TextImportSummary(matched.Count, added, included.Count, saved, notOnMac, tooLarge);
    }

    private async Task<(int Saved, int NotOnMac, int TooLarge)> ImportAttachmentsAsync(
        PropertyManagerDbContext db, IReadOnlyList<MessagesAttachment> found, Dictionary<long, TextMessage> messages, long maxBytes, CancellationToken ct)
    {
        var guids = found.Select(a => a.Guid).ToList();
        var known = await db.TextAttachments.Where(a => guids.Contains(a.Guid)).ToDictionaryAsync(a => a.Guid, ct);
        int saved = 0, notOnMac = 0, tooLarge = 0;
        foreach (var source in found)
        {
            if (known.TryGetValue(source.Guid, out var attachment) && attachment.Status != TextAttachmentStatus.NotOnMac)
            {
                continue;
            }
            if (attachment is null)
            {
                attachment = new TextAttachment
                {
                    TextMessageId = messages[source.MessageRowId].Id,
                    Guid = source.Guid,
                    FileName = Path.GetFileName(source.FileName) is { Length: > 0 } name ? (name.Length > 255 ? name[^255..] : name) : "attachment",
                    ContentType = ContentTypeOf(source),
                    SizeBytes = source.TotalBytes,
                };
                db.TextAttachments.Add(attachment);
                known[source.Guid] = attachment;
            }

            var size = source.Path is not null && File.Exists(source.Path) ? new FileInfo(source.Path).Length : (long?)null;
            if (size is null)
            {
                attachment.Status = TextAttachmentStatus.NotOnMac;
                notOnMac++;
            }
            else if (size > maxBytes)
            {
                attachment.Status = TextAttachmentStatus.TooLarge;
                attachment.SizeBytes = size.Value;
                tooLarge++;
            }
            else
            {
                await SaveAsync(attachment, source.Path!, ct);
                saved++;
            }
            // Save as we go so a long first import keeps what it has copied if it's stopped.
            await db.SaveChangesAsync(ct);
        }
        return (saved, notOnMac, tooLarge);
    }

    private async Task SaveAsync(TextAttachment attachment, string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        attachment.SizeBytes = bytes.Length;
        attachment.StorageKey = await storage.SaveAsync(new MemoryStream(bytes), attachment.FileName, ct);
        attachment.Status = TextAttachmentStatus.Saved;

        if (attachment.IsImage)
        {
            // A JPEG for viewing and for the assistant, and a thumbnail. Without a converter, browser-friendly
            // formats are shown as they are.
            var preview = images is null ? null : await images.ToJpegAsync(path, PreviewPixels, ct);
            var thumbnail = images is null ? null : await images.ToJpegAsync(path, ThumbnailPixels, ct);
            if (preview is not null)
            {
                attachment.PreviewKey = await storage.SaveAsync(new MemoryStream(preview), Path.ChangeExtension(attachment.FileName, ".jpg"), ct);
            }
            if (thumbnail is not null)
            {
                attachment.ThumbnailKey = await storage.SaveAsync(new MemoryStream(thumbnail), Path.ChangeExtension(attachment.FileName, ".jpg"), ct);
            }
        }
        else if (DocumentTextExtractor.CanRead(attachment.FileName))
        {
            try
            {
                attachment.Text = DocumentTextExtractor.Extract(new MemoryStream(bytes), attachment.FileName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Damaged or password-protected: kept, but not searchable.
            }
        }
    }

    private static readonly Dictionary<string, string> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".heic"] = "image/heic", [".heif"] = "image/heif", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".mov"] = "video/quicktime", [".mp4"] = "video/mp4", [".m4v"] = "video/mp4",
        [".pdf"] = "application/pdf", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword", [".caf"] = "audio/x-caf", [".m4a"] = "audio/mp4", [".vcf"] = "text/vcard",
    };

    private static string ContentTypeOf(MessagesAttachment a) =>
        a.MimeType is { Length: > 0 } mime && mime.Contains('/') ? mime
        : TypesByExtension.GetValueOrDefault(Path.GetExtension(a.FileName)) ?? "application/octet-stream";

    /// <summary>Includes or excludes a contact. Excluding removes that contact's imported messages from the app.</summary>
    public async Task SetIncludedAsync(int contactId, bool included, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.MessageContacts.SingleAsync(c => c.Id == contactId, ct);
        contact.Included = included;
        await db.SaveChangesAsync(ct);
        if (!included)
        {
            var files = await db.TextAttachments.Where(a => a.TextMessage!.ContactHandle == contact.Handle)
                .Select(a => new[] { a.StorageKey, a.PreviewKey, a.ThumbnailKey }).ToListAsync(ct);
            foreach (var key in files.SelectMany(f => f).OfType<string>())
            {
                await storage.DeleteAsync(key, ct);
            }
            // Attachments are deleted with their messages (cascade).
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
