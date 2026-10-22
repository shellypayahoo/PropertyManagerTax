namespace PropertyManager.Core.Email;

public record MailFolderInfo(string Id, string Path, int TotalCount);

public record MailAddress(string Address, string? Name)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public record MailItem(
    string Id, string? ConversationId, string Subject, MailAddress? From, IReadOnlyList<MailAddress> To, IReadOnlyList<MailAddress> Cc,
    DateTimeOffset SentAt, string Body, bool HasAttachments, bool IsDraft);

/// <summary>Messages added or changed in a folder since the last sync, ids of messages removed from it, and where to resume next time.</summary>
public record MailChanges(IReadOnlyList<MailItem> Changed, IReadOnlyList<string> Removed, string DeltaLink);

public record MailAttachmentInfo(string Id, string Name, string ContentType, long Size, bool IsInline);

/// <summary>Thrown when a saved delta link is too old to use; the folder has to be synced from the start.</summary>
public class MailDeltaExpiredException(string message) : Exception(message);

/// <summary>Read-only access to a mailbox. Implemented with Microsoft Graph; tests use a fake.</summary>
public interface IMailSource
{
    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken ct = default);

    /// <summary>All changes in the folder since <paramref name="deltaLink"/>, or every message when it's null.</summary>
    Task<MailChanges> GetChangesAsync(string folderId, string? deltaLink, CancellationToken ct = default);

    Task<IReadOnlyList<MailAttachmentInfo>> GetAttachmentsAsync(string messageId, CancellationToken ct = default);

    Task<byte[]> DownloadAttachmentAsync(string messageId, string attachmentId, CancellationToken ct = default);
}

/// <summary>Supplies Microsoft Graph access tokens for the signed-in mailbox.</summary>
public interface IMailTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}
