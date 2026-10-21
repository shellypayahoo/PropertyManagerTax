using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
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

                db.LeaseDocuments.Add(new LeaseDocument
                {
                    LeaseId = leaseId,
                    FileName = name.Length > 255 ? name[^255..] : name,
                    ContentType = contentType,
                    SizeBytes = size,
                    StorageKey = key,
                    UploadedAt = clock.GetUtcNow(),
                });
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
}
