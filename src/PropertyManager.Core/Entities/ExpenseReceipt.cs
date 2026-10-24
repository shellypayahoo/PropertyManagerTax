namespace PropertyManager.Core.Entities;

/// <summary>A receipt or invoice (photo or file) attached to an expense.</summary>
public class ExpenseReceipt
{
    public int Id { get; set; }
    public int ExpenseId { get; set; }
    public Expense? Expense { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }

    /// <summary>The original file in <see cref="Storage.IDocumentStorage"/>.</summary>
    public string StorageKey { get; set; } = "";

    /// <summary>For photos: a JPEG every browser can show (iPhone photos are HEIC), and a thumbnail.</summary>
    public string? PreviewKey { get; set; }
    public string? ThumbnailKey { get; set; }

    /// <summary>Text read from PDF and Word receipts, for search and the Ask page.</summary>
    public string? Text { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
