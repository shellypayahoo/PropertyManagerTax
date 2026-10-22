using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Documents;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Services;

public record DocumentUploadResult(int Saved, IReadOnlyList<string> Problems);

public class LeaseDocumentService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IDocumentStorage storage, TimeProvider clock)
{
    public const long MaxFileBytes = 25 * 1024 * 1024;
    public const int MaxFilesPerUpload = 20;

    // Content type is decided here from the extension, never taken from the browser.
    public static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".heic"] = "image/heic",
    };

    /// <summary>Saves uploaded files to a lease. Files that are rejected or fail are reported, not thrown.</summary>
    public async Task<DocumentUploadResult> AddAsync(
        int leaseId, IEnumerable<(string FileName, long Size, Func<Stream> Open)> files, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Leases.AnyAsync(l => l.Id == leaseId, ct))
        {
            return new DocumentUploadResult(0, ["That lease no longer exists."]);
        }

        var saved = 0;
        var problems = new List<string>();
        foreach (var (fileName, size, open) in files.Take(MaxFilesPerUpload))
        {
            var name = Path.GetFileName(fileName);
            if (!AllowedTypes.TryGetValue(Path.GetExtension(name), out var contentType))
            {
                problems.Add($"{name}: only PDF, Word, JPG, PNG, and HEIC files can be uploaded.");
                continue;
            }
            if (size == 0)
            {
                problems.Add($"{name}: the file is empty.");
                continue;
            }
            if (size > MaxFileBytes)
            {
                problems.Add($"{name}: larger than {MaxFileBytes / 1024 / 1024} MB.");
                continue;
            }

            string? key = null;
            try
            {
                await using (var stream = open())
                {
                    key = await storage.SaveAsync(stream, name, ct);
                }

                var document = new LeaseDocument
                {
                    LeaseId = leaseId,
                    FileName = name.Length > 255 ? name[^255..] : name,
                    ContentType = contentType,
                    SizeBytes = size,
                    StorageKey = key,
                    UploadedAt = clock.GetUtcNow(),
                };
                await ReadTextAsync(document, ct);
                db.LeaseDocuments.Add(document);
                await db.SaveChangesAsync(ct);
                saved++;
            }
            catch (Exception ex) when (ex is IOException or DbUpdateException or OperationCanceledException or InvalidDataException)
            {
                db.ChangeTracker.Clear();
                if (key is not null)
                {
                    await storage.DeleteAsync(key, CancellationToken.None);
                }
                problems.Add($"{name}: upload failed ({ex.Message}). Please try again.");
            }
        }

        return new DocumentUploadResult(saved, problems);
    }

    /// <summary>Reads the text of documents uploaded before text reading existed. Returns how many were read.</summary>
    public async Task<int> ReadMissingTextAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var documents = await db.LeaseDocuments.Where(d => !d.TextExtracted).ToListAsync(ct);
        foreach (var document in documents)
        {
            await ReadTextAsync(document, ct);
        }
        await db.SaveChangesAsync(ct);
        return documents.Count;
    }

    /// <summary>Fills in <see cref="LeaseDocument.Text"/> from the stored file. A file that can't be read just gets no text.</summary>
    private async Task ReadTextAsync(LeaseDocument document, CancellationToken ct)
    {
        document.TextExtracted = true;
        if (!DocumentTextExtractor.CanRead(document.FileName))
        {
            return;
        }
        try
        {
            await using var stream = await storage.OpenReadAsync(document.StorageKey, ct);
            if (stream is null)
            {
                return;
            }
            // PdfPig and ZipArchive need a seekable stream.
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            copy.Position = 0;
            document.Text = DocumentTextExtractor.Extract(copy, document.FileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Damaged, password-protected, or unusual files are kept; they just can't be searched.
            document.Text = null;
        }
    }
}
