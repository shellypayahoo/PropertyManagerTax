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

public record CalendarInfo(string Id, string Name);

/// <summary>One calendar appointment, or one occurrence of a recurring one. Times are UTC; all-day events start at midnight UTC of their date.</summary>
public record CalendarItem(string Id, string Subject, string? Location, string? Preview, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, bool IsCancelled);

/// <summary>
/// An appointment to add. Times are wall-clock times in <paramref name="TimeZone"/> (a Windows time zone name, as
/// Microsoft Graph expects); all-day events run from midnight of the first day to midnight after the last.
/// </summary>
public record NewCalendarItem(string Subject, string? Location, string? Notes, DateTime Start, DateTime End, string TimeZone, bool IsAllDay, int? ReminderMinutes);

/// <summary>Thrown when a saved delta link is too old to use; the folder has to be synced from the start.</summary>
public class MailDeltaExpiredException(string message) : Exception(message);

/// <summary>Read access to a mailbox and its calendars, plus adding calendar events. Implemented with Microsoft Graph; tests use a fake.</summary>
public interface IMailSource
{
    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken ct = default);

    /// <summary>All changes in the folder since <paramref name="deltaLink"/>, or every message when it's null.</summary>
    Task<MailChanges> GetChangesAsync(string folderId, string? deltaLink, CancellationToken ct = default);

    Task<IReadOnlyList<MailAttachmentInfo>> GetAttachmentsAsync(string messageId, CancellationToken ct = default);

    Task<byte[]> DownloadAttachmentAsync(string messageId, string attachmentId, CancellationToken ct = default);

    Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken ct = default);

    /// <summary>Events in a calendar between two times, with recurring events expanded into occurrences.</summary>
    Task<IReadOnlyList<CalendarItem>> GetEventsAsync(string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>Adds an appointment to a calendar and returns it as stored there. The only change the app makes to the mailbox.</summary>
    Task<CalendarItem> CreateEventAsync(string calendarId, NewCalendarItem item, CancellationToken ct = default) =>
        throw new NotSupportedException("Adding calendar events isn't supported here.");
}

/// <summary>Supplies Microsoft Graph access tokens for the signed-in mailbox.</summary>
public interface IMailTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>A token that may also add calendar events.</summary>
    Task<string> GetCalendarWriteTokenAsync(CancellationToken ct = default) => GetAccessTokenAsync(ct);
}
