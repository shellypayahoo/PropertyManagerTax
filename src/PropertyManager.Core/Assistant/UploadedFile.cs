using PropertyManager.Core.Documents;
using PropertyManager.Core.Messaging;

namespace PropertyManager.Core.Assistant;

/// <summary>A file attached to a question on the Ask page, kept for the rest of that conversation.</summary>
public class UploadedFile
{
    public const long MaxBytes = 20 * 1024 * 1024;
    public const int MaxTextCharacters = 40_000;

    public string Name { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public byte[] Content { get; init; } = [];

    /// <summary>Text read from PDF, Word, CSV, and text files.</summary>
    public string? Text { get; init; }

    /// <summary>For pictures: bytes and type the AI model can look at (iPhone HEIC photos converted to JPEG when possible).</summary>
    public byte[]? Image { get; init; }
    public string? ImageType { get; init; }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword", [".txt"] = "text/plain", [".csv"] = "text/csv", [".md"] = "text/markdown",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".heic"] = "image/heic", [".heif"] = "image/heif",
    };

    public static bool IsSupported(string fileName) => Types.ContainsKey(Path.GetExtension(fileName));

    /// <summary>Reads what the model needs from an uploaded file.</summary>
    /// <exception cref="InvalidDataException">The file type isn't supported or the file is empty or too large.</exception>
    public static async Task<UploadedFile> PrepareAsync(string fileName, byte[] content, IImageConverter? images = null, CancellationToken ct = default)
    {
        var name = Path.GetFileName(fileName);
        if (!Types.TryGetValue(Path.GetExtension(name), out var type))
        {
            throw new InvalidDataException($"{name}: only photos, PDF, Word, CSV, and text files can be attached.");
        }
        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new InvalidDataException(content.Length == 0 ? $"{name} is empty." : $"{name} is larger than {MaxBytes / 1024 / 1024} MB.");
        }

        if (type.StartsWith("image/", StringComparison.Ordinal))
        {
            var jpeg = await ToJpegAsync(name, content, images, ct);
            var webType = type is "image/jpeg" or "image/png" or "image/gif" or "image/webp" ? type : null;
            return new UploadedFile
            {
                Name = name, ContentType = type, Content = content,
                Image = jpeg ?? (webType is null ? null : content),
                ImageType = jpeg is not null ? "image/jpeg" : webType,
            };
        }

        string? text = type.StartsWith("text/", StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetString(content)
            : null;
        if (text is null && DocumentTextExtractor.CanRead(name))
        {
            try
            {
                text = DocumentTextExtractor.Extract(new MemoryStream(content), name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                text = null;
            }
        }
        return new UploadedFile { Name = name, ContentType = type, Content = content, Text = text };
    }

    /// <summary>What the model is told about the file in the question.</summary>
    public string Describe() =>
        IsImage ? (Image is null ? $"[Attached photo {Name}: this format can't be shown to the model]" : $"[Attached photo {Name}, shown below]")
        : string.IsNullOrWhiteSpace(Text) ? $"[Attached file {Name}: no readable text (scanned or unsupported)]"
        : Text.Length > MaxTextCharacters
            ? $"[Attached file {Name}, first {MaxTextCharacters:N0} of {Text.Length:N0} characters; read_uploaded_file reads more]\n{Text[..MaxTextCharacters]}"
            : $"[Attached file {Name}]\n{Text}";

    private static async Task<byte[]?> ToJpegAsync(string name, byte[] content, IImageConverter? images, CancellationToken ct)
    {
        if (images is null)
        {
            return null;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"pm-ask-{Guid.NewGuid():N}{Path.GetExtension(name)}");
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            return await images.ToJpegAsync(temp, TextMessageImportService.PreviewPixels, ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
