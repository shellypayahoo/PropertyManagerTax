using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class ReceiptServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-receipts-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly ReceiptService service;
    private readonly int expenseId;

    public ReceiptServiceTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var expense = new Expense { PropertyId = db.Properties.First(p => p.Name.Contains("Taylor")).Id, Date = new(2026, 3, 9), Category = ExpenseCategory.Repairs, Amount = 101.37m, Description = "Costco faucet" };
        db.Expenses.Add(expense);
        db.SaveChanges();
        expenseId = expense.Id;
        service = new ReceiptService(dbFactory, new LocalDocumentStorage(root), TimeProvider.System, new FakeImages());
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string, long, Func<Stream>) File(string name, byte[] content) => (name, content.Length, () => new MemoryStream(content));

    private int FileCount => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public async Task Attach_SavesPhotosWithPreviews_ReadsPdfs_AndRejectsOtherFiles()
    {
        var pdf = DocumentTextTests.Pdf(["COSTCO WHOLESALE", "Kitchen faucet 101.37"]);

        var result = await service.AddAsync(expenseId, [File("IMG_5001.HEIC", [1, 2, 3]), File("invoice.pdf", pdf), File("notes.exe", [1]), File("empty.jpg", [])]);

        Assert.Equal(2, result.Saved);
        Assert.Equal(2, result.Problems.Count);
        await using var db = dbFactory.CreateDbContext();
        var receipts = await db.ExpenseReceipts.OrderBy(r => r.FileName).ToListAsync();
        Assert.Equal(("IMG_5001.HEIC", "image/heic"), (receipts[0].FileName, receipts[0].ContentType));
        Assert.NotNull(receipts[0].PreviewKey);
        Assert.NotNull(receipts[0].ThumbnailKey);
        Assert.Contains("Kitchen faucet 101.37", receipts[1].Text);
        Assert.Equal(4, FileCount);
    }

    [Fact]
    public async Task Removing_DeletesFiles_AndDeletingTheExpenseRemovesItsReceipts()
    {
        await service.AddAsync(expenseId, [File("a.jpg", [1]), File("b.jpg", [2])]);
        await using (var db = dbFactory.CreateDbContext())
        {
            await service.DeleteAsync((await db.ExpenseReceipts.FirstAsync(r => r.FileName == "a.jpg")).Id);
        }
        Assert.Equal(3, FileCount);

        await service.DeleteFilesForExpenseAsync(expenseId);
        await using (var db = dbFactory.CreateDbContext())
        {
            await db.Expenses.Where(e => e.Id == expenseId).ExecuteDeleteAsync();
            Assert.Equal(0, await db.ExpenseReceipts.CountAsync());
        }
        Assert.Equal(0, FileCount);
        Assert.Equal(["That expense no longer exists."], (await service.AddAsync(expenseId, [File("c.jpg", [1])])).Problems);
    }

    [Fact]
    public async Task Assistant_SeesReceiptsOnExpenses_AndSearchesTheirText()
    {
        await service.AddAsync(expenseId, [File("invoice.pdf", DocumentTextTests.Pdf(["Kitchen faucet 101.37"]))]);
        var tools = new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory));

        var expenses = await tools.SearchExpensesAsync(fromDate: "2026-01-01", toDate: "2026-12-31");
        var documents = await tools.SearchDocumentsAsync("faucet", lease: "costco");

        Assert.Contains("receipt: invoice.pdf (document R", expenses);
        Assert.Contains("Document R", documents);
        Assert.Contains("receipt for expense 2026-03-09 $101.37 Costco faucet", documents);
    }

    private sealed class FakeImages : IImageConverter
    {
        public Task<byte[]?> ToJpegAsync(string sourcePath, int maxPixels, CancellationToken ct = default) => Task.FromResult<byte[]?>([0xFF, 0xD8]);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
