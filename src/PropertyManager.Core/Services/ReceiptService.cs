using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Documents;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Services;

/// <summary>Attaches receipts (photos, PDFs, Word files) to expenses.</summary>
public class ReceiptService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IDocumentStorage storage, TimeProvider clock, IImageConverter? images = null)
{
    /// <summary>Saves uploaded receipts to an expense. Files that are rejected or fail are reported, not thrown.</summary>
    public async Task<DocumentUploadResult> AddAsync(int expenseId, IEnumerable<(string FileName, long Size, Func<Stream> Open)> files, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Expenses.AnyAsync(e => e.Id == expenseId, ct))
        {
            return new DocumentUploadResult(0, ["That expense no longer exists."]);
        }

        var saved = 0;
        var problems = new List<string>();
        foreach (var (fileName, size, open) in files.Take(LeaseDocumentService.MaxFilesPerUpload))
        {
            var name = Path.GetFileName(fileName);
            if (!LeaseDocumentService.AllowedTypes.TryGetValue(Path.GetExtension(name), out var contentType))
            {
                problems.Add($"{name}: only photos (JPG, PNG, HEIC), PDF, and Word files can be attached.");
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
                var receipt = new ExpenseReceipt
                {
                    ExpenseId = expenseId,
                    FileName = name.Length > 255 ? name[^255..] : name,
                    ContentType = contentType,
                    SizeBytes = bytes.Length,
                    UploadedAt = clock.GetUtcNow(),
                };
                receipt.StorageKey = await storage.SaveAsync(new MemoryStream(bytes), name, ct);
                keys.Add(receipt.StorageKey);

                if (receipt.IsImage)
                {
                    await AddPreviewsAsync(receipt, bytes, keys, ct);
                }
                else if (DocumentTextExtractor.CanRead(name))
                {
                    try
                    {
                        receipt.Text = DocumentTextExtractor.Extract(new MemoryStream(bytes), name);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Damaged or password-protected: kept, but not searchable.
                    }
                }

                db.ExpenseReceipts.Add(receipt);
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

    private async Task AddPreviewsAsync(ExpenseReceipt receipt, byte[] bytes, List<string> keys, CancellationToken ct)
    {
        if (images is null)
        {
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"pm-receipt-{Guid.NewGuid():N}{Path.GetExtension(receipt.FileName)}");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            if (await images.ToJpegAsync(temp, TextMessageImportService.PreviewPixels, ct) is { } preview)
            {
                receipt.PreviewKey = await storage.SaveAsync(new MemoryStream(preview), Path.ChangeExtension(receipt.FileName, ".jpg"), ct);
                keys.Add(receipt.PreviewKey);
            }
            if (await images.ToJpegAsync(temp, TextMessageImportService.ThumbnailPixels, ct) is { } thumbnail)
            {
                receipt.ThumbnailKey = await storage.SaveAsync(new MemoryStream(thumbnail), Path.ChangeExtension(receipt.FileName, ".jpg"), ct);
                keys.Add(receipt.ThumbnailKey);
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task DeleteAsync(int receiptId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ExpenseReceipts.FindAsync([receiptId], ct) is { } receipt)
        {
            await DeleteFilesAsync([receipt], ct);
            db.ExpenseReceipts.Remove(receipt);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Deletes the files of an expense's receipts; call before deleting the expense (its receipt rows go with it).</summary>
    public async Task DeleteFilesForExpenseAsync(int expenseId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await DeleteFilesAsync(await db.ExpenseReceipts.Where(r => r.ExpenseId == expenseId).ToListAsync(ct), ct);
    }

    private async Task DeleteFilesAsync(IEnumerable<ExpenseReceipt> receipts, CancellationToken ct)
    {
        foreach (var key in receipts.SelectMany(r => new[] { r.StorageKey, r.PreviewKey, r.ThumbnailKey }).OfType<string>())
        {
            await storage.DeleteAsync(key, ct);
        }
    }
}
