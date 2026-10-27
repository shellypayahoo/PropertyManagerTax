using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Documents;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Services;

/// <summary>How uploaded business documents are filed. Every file in one upload is filed the same way.</summary>
public record BusinessDocumentInfo(BusinessDocumentKind Kind, string? Description, int? PropertyId, int? Year, bool KeepFromAssistant);

/// <summary>Stores business documents: LLC papers, tax returns, insurance, deeds and closing papers, and the like.</summary>
public class BusinessDocumentService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IDocumentStorage storage, TimeProvider clock, IImageConverter? images = null)
{
    public const int MaxDescriptionLength = 200;

    /// <summary>Saves uploaded files. Files that are rejected or fail are reported, not thrown.</summary>
    public async Task<DocumentUploadResult> AddAsync(
        BusinessDocumentInfo info, IEnumerable<(string FileName, long Size, Func<Stream> Open)> files, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (info.PropertyId is { } propertyId && !await db.Properties.AnyAsync(p => p.Id == propertyId, ct))
        {
            return new DocumentUploadResult(0, ["That property no longer exists."]);
        }
        if (info.Year is < 1900 or > 2200)
        {
            return new DocumentUploadResult(0, [$"{info.Year} isn't a year."]);
        }

        var saved = 0;
        var problems = new List<string>();
        foreach (var (fileName, size, open) in files.Take(LeaseDocumentService.MaxFilesPerUpload))
        {
            var name = Path.GetFileName(fileName);
            if (!LeaseDocumentService.AllowedTypes.TryGetValue(Path.GetExtension(name), out var contentType))
            {
                problems.Add($"{name}: only PDF, Word, and photo (JPG, PNG, HEIC) files can be uploaded.");
                continue;
            }
            if (size == 0 || size > LeaseDocumentService.MaxFileBytes)
            {
                problems.Add(size == 0 ? $"{name}: the file is empty." : $"{name}: larger than {LeaseDocumentService.MaxFileBytes / 1024 / 1024} MB.");
                continue;
            }

            var keys = new List<string>();
            try
            {
                byte[] bytes;
                await using (var stream = open())
                using (var copy = new MemoryStream())
                {
                    await stream.CopyToAsync(copy, ct);
                    bytes = copy.ToArray();
                }
                var document = new BusinessDocument
                {
                    Kind = info.Kind,
                    Description = Describe(info.Description, name),
                    PropertyId = info.PropertyId,
                    Year = info.Year,
                    KeepFromAssistant = info.KeepFromAssistant,
                    FileName = name.Length > 255 ? name[^255..] : name,
                    ContentType = contentType,
                    SizeBytes = bytes.Length,
                    UploadedAt = clock.GetUtcNow(),
                };
                document.StorageKey = await storage.SaveAsync(new MemoryStream(bytes), name, ct);
                keys.Add(document.StorageKey);

                if (document.IsImage)
                {
                    await AddPreviewsAsync(document, bytes, keys, ct);
                }
                else if (DocumentTextExtractor.CanRead(name))
                {
                    try
                    {
                        document.Text = DocumentTextExtractor.Extract(new MemoryStream(bytes), name);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Damaged or password-protected: kept, but not searchable.
                    }
                }

                db.BusinessDocuments.Add(document);
                await db.SaveChangesAsync(ct);
                saved++;
            }
            catch (Exception ex) when (ex is IOException or DbUpdateException or InvalidDataException)
            {
                db.ChangeTracker.Clear();
                foreach (var key in keys)
                {
                    await storage.DeleteAsync(key, CancellationToken.None);
                }
                problems.Add($"{name}: upload failed ({ex.Message}). Please try again.");
            }
        }
        return new DocumentUploadResult(saved, problems);
    }

    /// <summary>Changes how a document is filed. Returns an error to show, or null when saved.</summary>
    public async Task<string?> UpdateAsync(int id, BusinessDocumentInfo info, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.BusinessDocuments.FindAsync([id], ct) is not { } document)
        {
            return "That document no longer exists.";
        }
        if (info.Year is < 1900 or > 2200)
        {
            return $"{info.Year} isn't a year.";
        }
        document.Kind = info.Kind;
        document.Description = Describe(info.Description, document.FileName);
        document.PropertyId = info.PropertyId;
        document.Year = info.Year;
        document.KeepFromAssistant = info.KeepFromAssistant;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.BusinessDocuments.FindAsync([id], ct) is { } document)
        {
            foreach (var key in new[] { document.StorageKey, document.PreviewKey, document.ThumbnailKey }.OfType<string>())
            {
                await storage.DeleteAsync(key, ct);
            }
            db.BusinessDocuments.Remove(document);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The owner's description, or the file name without its extension when none was given.</summary>
    private static string Describe(string? description, string fileName)
    {
        var text = string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(fileName) : description.Trim();
        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] : text;
    }

    private async Task AddPreviewsAsync(BusinessDocument document, byte[] bytes, List<string> keys, CancellationToken ct)
    {
        if (images is null)
        {
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"pm-document-{Guid.NewGuid():N}{Path.GetExtension(document.FileName)}");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            if (await images.ToJpegAsync(temp, TextMessageImportService.PreviewPixels, ct) is { } preview)
            {
                document.PreviewKey = await storage.SaveAsync(new MemoryStream(preview), Path.ChangeExtension(document.FileName, ".jpg"), ct);
                keys.Add(document.PreviewKey);
            }
            if (await images.ToJpegAsync(temp, TextMessageImportService.ThumbnailPixels, ct) is { } thumbnail)
            {
                document.ThumbnailKey = await storage.SaveAsync(new MemoryStream(thumbnail), Path.ChangeExtension(document.FileName, ".jpg"), ct);
                keys.Add(document.ThumbnailKey);
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
