namespace PropertyManager.Core.Entities;

/// <summary>A PDF, Word file, or picture attached to a synced email. The file is kept in document storage.</summary>
public class EmailAttachment
{
    public int Id { get; set; }
    public int EmailMessageId { get; set; }
    public EmailMessage? EmailMessage { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }

    /// <summary>Key the file is stored under in <see cref="Storage.IDocumentStorage"/>.</summary>
    public string StorageKey { get; set; } = "";

    /// <summary>Text read from PDFs and Word files, for the Ask page; null for pictures and unreadable files.</summary>
    public string? Text { get; set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}
