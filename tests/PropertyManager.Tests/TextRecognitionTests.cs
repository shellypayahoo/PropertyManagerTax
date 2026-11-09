using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Documents;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class TextRecognitionTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-ocr-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly LocalDocumentStorage storage;
    private readonly FakeRecognizer recognizer = new("[Page 1]\nForm 1065 U.S. Return of Partnership Income\nEIN 12-3456789");

    public TextRecognitionTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        storage = new LocalDocumentStorage(root);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly byte[] ScannedPdf = DocumentTextTests.Pdf([]);

    [Fact]
    public async Task Scanned_PDFs_and_photos_are_read_by_recognition()
    {
        Assert.Contains("Form 1065", await DocumentReader.ReadAsync(ScannedPdf, "return.pdf", recognizer));
        Assert.Contains("Form 1065", await DocumentReader.ReadAsync([1, 2, 3], "receipt.HEIC", recognizer));
        Assert.Equal(["return.pdf", "receipt.HEIC"], recognizer.Files);
    }

    [Fact]
    public async Task PDFs_with_text_are_read_directly()
    {
        var text = await DocumentReader.ReadAsync(DocumentTextTests.Pdf(["Lease ends June 30"]), "lease.pdf", recognizer);

        Assert.Contains("Lease ends June 30", text);
        Assert.Empty(recognizer.Files);
    }

    [Fact]
    public async Task No_recognizer_or_nothing_found_means_no_text()
    {
        Assert.Null(await DocumentReader.ReadAsync(ScannedPdf, "return.pdf", recognizer: null));
        Assert.Null(await DocumentReader.ReadAsync(ScannedPdf, "return.pdf", new FakeRecognizer("[Page 1]\n  ")));
        Assert.False(DocumentReader.HasWords("[Page 1]\n[Page 2]\n"));
        // On computers other than a Mac there's no built-in recognition.
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Null(await new MacTextRecognizer().ReadAsync(ScannedPdf, "return.pdf"));
        }
    }

    [Fact]
    public async Task Uploaded_scan_gets_its_text_and_an_old_one_can_be_read_again()
    {
        var withoutRecognition = new BusinessDocumentService(dbFactory, storage, TimeProvider.System);
        await withoutRecognition.AddAsync(new(BusinessDocumentKind.TaxReturn, "Tax 2025", null, 2025, true),
            [("return.pdf", ScannedPdf.LongLength, () => new MemoryStream(ScannedPdf))]);
        int id;
        await using (var db = dbFactory.CreateDbContext())
        {
            var old = db.BusinessDocuments.Single();
            Assert.False(old.HasText);
            id = old.Id;
        }

        var service = new BusinessDocumentService(dbFactory, storage, TimeProvider.System, recognizer: recognizer);
        Assert.True(await service.ReadTextAsync(id) > 0);
        await service.AddAsync(new(BusinessDocumentKind.TaxForm, "1099", null, 2025, true),
            [("1099.jpg", 3, () => new MemoryStream([1, 2, 3]))]);

        await using var check = dbFactory.CreateDbContext();
        Assert.All(check.BusinessDocuments, d => Assert.Contains("Form 1065", d.Text));
    }

    [Fact]
    public async Task Scanned_lease_file_can_be_read_again()
    {
        int leaseId;
        await using (var db = dbFactory.CreateDbContext())
        {
            var lease = new Lease { UnitId = db.Units.First().Id, StartDate = new(2026, 1, 1), MonthlyRent = 1000m };
            db.Leases.Add(lease);
            await db.SaveChangesAsync();
            leaseId = lease.Id;
        }
        await new LeaseDocumentService(dbFactory, storage, TimeProvider.System)
            .AddAsync(leaseId, [("signed.pdf", ScannedPdf.LongLength, () => new MemoryStream(ScannedPdf))]);
        int documentId;
        await using (var db = dbFactory.CreateDbContext())
        {
            documentId = db.LeaseDocuments.Single().Id;
        }

        var characters = await new LeaseDocumentService(dbFactory, storage, TimeProvider.System, recognizer).ReadTextAgainAsync(documentId);

        Assert.True(characters > 0);
        await using var check = dbFactory.CreateDbContext();
        Assert.Contains("Form 1065", check.LeaseDocuments.Single().Text);
    }

    private sealed class FakeRecognizer(string text) : ITextRecognizer
    {
        public List<string> Files { get; } = [];

        public Task<string?> ReadAsync(byte[] content, string fileName, CancellationToken ct = default)
        {
            Files.Add(fileName);
            return Task.FromResult<string?>(text);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
