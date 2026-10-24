namespace PropertyManager.Core.Entities;

public enum TextAttachmentStatus
{
    /// <summary>The file is copied into document storage.</summary>
    Saved,

    /// <summary>Messages hasn't downloaded the file to this Mac (e.g. Optimize Mac Storage); tried again on the next import.</summary>
    NotOnMac,

    /// <summary>Larger than the size limit, so not copied.</summary>
    TooLarge,
}

/// <summary>A photo, video, or file sent in a text message.</summary>
public class TextAttachment
{
    public int Id { get; set; }
    public int TextMessageId { get; set; }
    public TextMessage? TextMessage { get; set; }

    /// <summary>The attachment's guid in the Messages database.</summary>
    public string Guid { get; set; } = "";

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public TextAttachmentStatus Status { get; set; }

    /// <summary>The original file in <see cref="Storage.IDocumentStorage"/>, when saved.</summary>
    public string? StorageKey { get; set; }

    /// <summary>For pictures: a JPEG up to 1600 pixels, which every browser can show (iPhone photos are HEIC).</summary>
    public string? PreviewKey { get; set; }

    /// <summary>For pictures: a small JPEG for the conversation view.</summary>
    public string? ThumbnailKey { get; set; }

    /// <summary>Text read from PDF and Word attachments, for the Ask page.</summary>
    public string? Text { get; set; }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public bool IsVideo => ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}
