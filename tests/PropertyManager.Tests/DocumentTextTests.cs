using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Documents;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PropertyManager.Tests;

public sealed class DocumentTextTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-doctext-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly LocalDocumentStorage storage;
    private readonly LeaseDocumentService service;
    private readonly AssistantTools tools;
    private readonly int leaseId;

    public DocumentTextTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var unit = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B");
        var lease = new Lease { Unit = unit, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }], StartDate = new(2025, 1, 1), MonthlyRent = 1000m };
        db.Leases.Add(lease);
        db.SaveChanges();
        leaseId = lease.Id;
        storage = new LocalDocumentStorage(root);
        service = new LeaseDocumentService(dbFactory, storage, TimeProvider.System);
        tools = new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory));
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Pdf(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.Letter);
            for (var i = 0; i < lines.Length; i++)
            {
                page.AddText(lines[i], 12, new PdfPoint(50, 700 - i * 20), font);
            }
        }
        return builder.Build();
    }

    private static byte[] Docx(string bodyXml)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
            writer.Write($"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{bodyXml}</w:body></w:document>""");
        }
        return output.ToArray();
    }

    [Fact]
    public void Pdf_TextIsReadWithPageMarkers()
    {
        var text = DocumentTextExtractor.Extract(new MemoryStream(Pdf(["RESIDENTIAL LEASE", "Rent is $1,000 per month."], ["12. PETS", "No pets without written consent."])), "lease.pdf");

        Assert.Equal("[Page 1]\nRESIDENTIAL LEASE\nRent is $1,000 per month.\n[Page 2]\n12. PETS\nNo pets without written consent.\n", text!.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Docx_ParagraphsTabsAndSplitRunsAreRead()
    {
        var docx = Docx("""<w:p><w:r><w:t>Late</w:t></w:r><w:r><w:t xml:space="preserve"> fee:</w:t><w:tab/><w:t>$50</w:t></w:r></w:p><w:p/><w:p><w:r><w:t>Due on the 5th</w:t></w:r></w:p>""");

        var text = DocumentTextExtractor.Extract(new MemoryStream(docx), "Addendum.DOCX");

        Assert.Equal("Late fee:\t$50\n\nDue on the 5th\n", text!.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ImagesHaveNoText() =>
        Assert.Null(DocumentTextExtractor.Extract(new MemoryStream(new byte[10]), "photo.jpg"));

    [Fact]
    public async Task Upload_SavesText_AndDamagedFilesAreStillKept()
    {
        var pdf = Pdf(["Pets are not allowed."]);
        var result = await service.AddAsync(leaseId,
        [
            ("lease.pdf", pdf.Length, () => new MemoryStream(pdf)),
            ("broken.pdf", 100, () => new MemoryStream(new byte[100])),
        ]);

        Assert.Equal(2, result.Saved);
        await using var db = dbFactory.CreateDbContext();
        var documents = await db.LeaseDocuments.OrderBy(d => d.Id).ToListAsync();
        Assert.All(documents, d => Assert.True(d.TextExtracted));
        Assert.Contains("Pets are not allowed.", documents[0].Text);
        Assert.False(documents[1].HasText);
    }

    [Fact]
    public async Task ReadMissingText_FillsInEarlierUploads()
    {
        var key = await storage.SaveAsync(new MemoryStream(Pdf(["Smoking is prohibited."])), "old.pdf");
        await using (var db = dbFactory.CreateDbContext())
        {
            db.LeaseDocuments.Add(new LeaseDocument { LeaseId = leaseId, FileName = "old.pdf", StorageKey = key, SizeBytes = 1, UploadedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await service.ReadMissingTextAsync());
        Assert.Equal(0, await service.ReadMissingTextAsync());
        await using var check = dbFactory.CreateDbContext();
        Assert.Contains("Smoking is prohibited.", (await check.LeaseDocuments.SingleAsync()).Text);
    }

    [Fact]
    public async Task AssistantTools_ListSearchAndReadDocuments()
    {
        var pdf = Pdf(["RESIDENTIAL LEASE"], ["12. PETS", "Tenant may keep one dog with written consent."]);
        await service.AddAsync(leaseId, [("Signed lease.pdf", pdf.Length, () => new MemoryStream(pdf)), ("photo.jpg", 10, () => new MemoryStream(new byte[10]))]);

        var list = await tools.ListDocumentsAsync();
        var found = await tools.SearchDocumentsAsync("DOG consent", lease: "taylor");
        var id = (await dbFactory.CreateDbContext().LeaseDocuments.FirstAsync(d => d.FileName == "Signed lease.pdf")).Id;
        var read = await tools.ReadDocumentAsync("L" + id);

        Assert.Contains("Signed lease.pdf", list);
        Assert.Contains("2 page(s)", list);
        Assert.Contains("photo.jpg", list);
        Assert.Contains("no readable text", list);
        Assert.Contains("Jane Sample", list);
        Assert.Contains("1 document(s)", found);
        Assert.Contains("[Page 2, character", found);
        Assert.Contains("Document L", found);
        Assert.Contains("one dog with written consent", found);
        Assert.Equal("No documents contain all of those words.", await tools.SearchDocumentsAsync("cat"));
        Assert.Contains("RESIDENTIAL LEASE", read);
        Assert.Contains("(end of document)", read);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
