namespace PropertyManager.Core.Entities;

/// <summary>A file attached to a lease, such as the signed lease PDF or an addendum.</summary>
public class LeaseDocument
{
    public int Id { get; set; }
    public int LeaseId { get; set; }
    public Lease? Lease { get; set; }

    /// <summary>Original file name as uploaded; for display and download only.</summary>
    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }

    /// <summary>Key the file is stored under in <see cref="Storage.IDocumentStorage"/>.</summary>
    public string StorageKey { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>Whether text extraction has been tried for this file (see <see cref="Text"/>).</summary>
    public bool TextExtracted { get; set; }

    /// <summary>
    /// The file's text, read from PDFs and Word files so the Ask assistant can search it. Null or empty when the file
    /// has no text layer (photos, scanned PDFs) or couldn't be read.
    /// </summary>
    public string? Text { get; set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}
