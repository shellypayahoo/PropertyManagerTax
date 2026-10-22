using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Data;
using PropertyManager.Core.Documents;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Email;

public record EmailSyncSummary(int Added, int Updated, int Removed, int Attachments, IReadOnlyList<string> Problems);

/// <summary>Copies the chosen mail folders into the app, and on later syncs only what changed.</summary>
public class EmailSyncService(
    IDbContextFactory<PropertyManagerDbContext> dbFactory, IMailSource mail, IDocumentStorage storage, EmailSignIn? signIn, IOptions<EmailOptions> options)
{
    /// <summary>Makes the given folders the synced ones. Emails from folders no longer chosen are removed from the app.</summary>
    public async Task SetFoldersAsync(IEnumerable<MailFolderInfo> folders, CancellationToken ct = default)
    {
        var chosen = folders.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.EmailFolders.ToListAsync(ct);
        foreach (var folder in existing.Where(f => chosen.All(c => c.Id != f.GraphId)))
        {
            await DeleteAttachmentFilesAsync(await db.EmailAttachments.Where(a => a.EmailMessage!.EmailFolderId == folder.Id).ToListAsync(ct), ct);
            db.EmailFolders.Remove(folder);
        }
        foreach (var folder in chosen)
        {
            if (existing.FirstOrDefault(f => f.GraphId == folder.Id) is { } found)
            {
                found.Path = folder.Path;
            }
            else
            {
                db.EmailFolders.Add(new EmailFolder { GraphId = folder.Id, Path = folder.Path });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<EmailSyncSummary> SyncAsync(CancellationToken ct = default)
    {
        var myAddresses = options.Value.MyAddresses.Select(a => a.ToLowerInvariant()).ToHashSet();
        if (signIn is not null && await signIn.GetSignedInAddressAsync() is { } me)
        {
            myAddresses.Add(me.ToLowerInvariant());
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var people = await PeopleByAddressAsync(db, ct);
        int added = 0, updated = 0, removed = 0, attachments = 0;
        var problems = new List<string>();

        foreach (var folder in await db.EmailFolders.ToListAsync(ct))
        {
            MailChanges changes;
            try
            {
                changes = await mail.GetChangesAsync(folder.GraphId, folder.DeltaLink, ct);
            }
            catch (MailDeltaExpiredException)
            {
                changes = await mail.GetChangesAsync(folder.GraphId, null, ct);
            }

            var ids = changes.Changed.Select(m => m.Id).Concat(changes.Removed).ToList();
            var known = await db.EmailMessages.Include(m => m.Attachments).Where(m => ids.Contains(m.GraphId)).ToDictionaryAsync(m => m.GraphId, ct);

            foreach (var id in changes.Removed)
            {
                // A message moved to another synced folder shows up there as changed; only delete it from this one.
                if (known.TryGetValue(id, out var gone) && gone.EmailFolderId == folder.Id)
                {
                    await DeleteAttachmentFilesAsync(gone.Attachments, ct);
                    db.EmailMessages.Remove(gone);
                    removed++;
                }
            }

            foreach (var item in changes.Changed.Where(m => !m.IsDraft))
            {
                var isNew = !known.TryGetValue(item.Id, out var email);
                email ??= new EmailMessage { GraphId = item.Id, EmailFolder = folder };
                var before = (email.Subject, email.FullText, email.EmailFolderId);
                Fill(email, item, folder, myAddresses, people);
                if (isNew)
                {
                    db.EmailMessages.Add(email);
                    known[item.Id] = email;
                    added++;
                }
                else if (before != (email.Subject, email.FullText, email.EmailFolderId))
                {
                    updated++;
                }

                if (item.HasAttachments && email.Attachments.Count == 0)
                {
                    try
                    {
                        attachments += await AddAttachmentsAsync(email, item.Id, ct);
                    }
                    catch (HttpRequestException ex)
                    {
                        problems.Add($"Attachments of \"{item.Subject}\" ({item.SentAt.ToLocalTime():MMM d, yyyy}): {ex.Message}");
                    }
                }
            }

            folder.DeltaLink = changes.DeltaLink;
            folder.LastSyncedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new EmailSyncSummary(added, updated, removed, attachments, problems);
    }

    private static void Fill(EmailMessage email, MailItem item, EmailFolder folder, HashSet<string> myAddresses, Dictionary<string, (int? TenantId, int? VendorId)> people)
    {
        email.EmailFolder = folder;
        email.ConversationId = item.ConversationId;
        email.Subject = Truncate(item.Subject, 1000);
        email.FromAddress = Truncate(item.From?.Address.ToLowerInvariant() ?? "", 320);
        email.FromName = item.From?.Name is { } name ? Truncate(name, 200) : null;
        email.To = string.Join("; ", item.To);
        email.Cc = string.Join("; ", item.Cc);
        email.SentAt = item.SentAt;
        email.FullText = item.Body.Trim();
        email.Text = QuotedReplyTrimmer.Trim(item.Body);
        email.IsFromMe = myAddresses.Contains(email.FromAddress);

        // The other person: the sender, or for emails I sent, the first tenant or vendor among the recipients.
        var others = email.IsFromMe ? item.To.Concat(item.Cc).Select(a => a.Address) : [email.FromAddress];
        var match = others.Select(a => people.GetValueOrDefault(a.ToLowerInvariant())).FirstOrDefault(p => p.TenantId is not null || p.VendorId is not null);
        email.TenantId = match.TenantId;
        email.VendorId = match.VendorId;
    }

    private async Task<int> AddAttachmentsAsync(EmailMessage email, string messageId, CancellationToken ct)
    {
        var saved = 0;
        foreach (var info in await mail.GetAttachmentsAsync(messageId, ct))
        {
            // Inline pictures are logos and signatures; other file types aren't kept.
            var name = Path.GetFileName(info.Name);
            if (info.IsInline || !LeaseDocumentService.AllowedTypes.TryGetValue(Path.GetExtension(name), out var contentType) || info.Size > LeaseDocumentService.MaxFileBytes)
            {
                continue;
            }
            var bytes = await mail.DownloadAttachmentAsync(messageId, info.Id, ct);
            var key = await storage.SaveAsync(new MemoryStream(bytes), name, ct);
            string? text = null;
            if (DocumentTextExtractor.CanRead(name))
            {
                try
                {
                    text = DocumentTextExtractor.Extract(new MemoryStream(bytes), name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Damaged or password-protected: kept, but not searchable.
                }
            }
            email.Attachments.Add(new EmailAttachment
            {
                FileName = name.Length > 255 ? name[^255..] : name,
                ContentType = contentType,
                SizeBytes = bytes.Length,
                StorageKey = key,
                Text = text,
            });
            saved++;
        }
        return saved;
    }

    /// <summary>Tenants' and vendors' email addresses.</summary>
    private static async Task<Dictionary<string, (int? TenantId, int? VendorId)>> PeopleByAddressAsync(PropertyManagerDbContext db, CancellationToken ct)
    {
        var people = new Dictionary<string, (int?, int?)>();
        foreach (var t in await db.Tenants.Where(t => t.Email != null).ToListAsync(ct))
        {
            if (ContactHandles.Normalize(t.Email) is { } address && address.Contains('@'))
            {
                people.TryAdd(address, (t.Id, null));
            }
        }
        foreach (var v in await db.Vendors.Where(v => v.Email != null).ToListAsync(ct))
        {
            if (ContactHandles.Normalize(v.Email) is { } address && address.Contains('@'))
            {
                people.TryAdd(address, (null, v.Id));
            }
        }
        return people;
    }

    private async Task DeleteAttachmentFilesAsync(IEnumerable<EmailAttachment> attachments, CancellationToken ct)
    {
        foreach (var attachment in attachments)
        {
            await storage.DeleteAsync(attachment.StorageKey, ct);
        }
    }

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
