using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class BusinessDocumentTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-business-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly BusinessDocumentService service;
    private readonly AssistantTools tools;
    private readonly AssistantActions actions;
    private readonly AssistantConversation conversation = new();
    private readonly int taylorId;

    public BusinessDocumentTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        taylorId = db.Properties.Single(p => p.Name.Contains("Taylor")).Id;

        var storage = new LocalDocumentStorage(root);
        service = new BusinessDocumentService(dbFactory, storage, TimeProvider.System);
        tools = new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory), storage: storage);
        actions = new AssistantActions(dbFactory, new ReceiptService(dbFactory, storage, TimeProvider.System),
            new LeaseDocumentService(dbFactory, storage, TimeProvider.System), businessDocuments: service) { Conversation = conversation };
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<(string, long, Func<Stream>)> Files(string name, byte[] bytes) => [(name, bytes.LongLength, () => new MemoryStream(bytes))];

    [Fact]
    public async Task Upload_files_the_document_and_reads_its_text()
    {
        var pdf = DocumentTextTests.Pdf(["Settlement statement. Cash to close $41,250.00"]);
        var result = await service.AddAsync(new(BusinessDocumentKind.DeedAndClosing, "  Closing ", taylorId, 2019, false), Files("settlement.pdf", pdf));
        var unnamed = await service.AddAsync(new(BusinessDocumentKind.LlcAndFormation, " ", null, null, false), Files("Articles of Organization.pdf", pdf));

        Assert.Equal(1, result.Saved);
        Assert.Equal(1, unnamed.Saved);
        await using var db = dbFactory.CreateDbContext();
        var closing = db.BusinessDocuments.Single(d => d.Kind == BusinessDocumentKind.DeedAndClosing);
        Assert.Equal("Closing", closing.Description);
        Assert.Equal(taylorId, closing.PropertyId);
        Assert.Equal(2019, closing.Year);
        Assert.Contains("Cash to close", closing.Text);
        Assert.Equal("Articles of Organization", db.BusinessDocuments.Single(d => d.PropertyId == null).Description);
    }

    [Fact]
    public async Task Upload_rejects_unsupported_files()
    {
        var result = await service.AddAsync(new(BusinessDocumentKind.Other, "x", null, null, false), Files("macro.exe", [1, 2, 3]));

        Assert.Equal(0, result.Saved);
        Assert.Contains("only PDF", Assert.Single(result.Problems));
    }

    [Fact]
    public async Task Edit_and_delete()
    {
        await service.AddAsync(new(BusinessDocumentKind.Other, "Policy", null, null, false), Files("policy.pdf", DocumentTextTests.Pdf(["Policy"])));
        int id;
        string key;
        await using (var db = dbFactory.CreateDbContext())
        {
            var d = db.BusinessDocuments.Single();
            (id, key) = (d.Id, d.StorageKey);
        }

        Assert.Null(await service.UpdateAsync(id, new(BusinessDocumentKind.Insurance, "Insurance 2026", taylorId, 2026, true)));
        await using (var db = dbFactory.CreateDbContext())
        {
            var d = db.BusinessDocuments.Single();
            Assert.Equal((BusinessDocumentKind.Insurance, "Insurance 2026", taylorId, 2026, true), (d.Kind, d.Description, d.PropertyId, d.Year, d.KeepFromAssistant));
        }

        await service.DeleteAsync(id);
        await using (var db = dbFactory.CreateDbContext())
        {
            Assert.Empty(db.BusinessDocuments);
        }
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Ask_finds_business_documents_but_never_private_ones()
    {
        await service.AddAsync(new(BusinessDocumentKind.Insurance, "Taylor insurance 2026", taylorId, 2026, false),
            Files("policy.pdf", DocumentTextTests.Pdf(["Liberty Mutual policy renews March 1 2027"])));
        await service.AddAsync(new(BusinessDocumentKind.TaxReturn, "Tax 2025", null, 2025, true),
            Files("return.pdf", DocumentTextTests.Pdf(["Form 1065 EIN 12-3456789 renews"])));

        var list = await tools.ListDocumentsAsync();
        Assert.Contains("Insurance, \"Taylor insurance 2026\"", list);
        Assert.DoesNotContain("Tax 2025", list);

        var found = await tools.SearchDocumentsAsync("renews", "insurance");
        Assert.Contains("policy.pdf", found);
        Assert.DoesNotContain("return.pdf", await tools.SearchDocumentsAsync("renews"));
        Assert.DoesNotContain("12-3456789", await tools.ReadDocumentAsync("B2"));
    }

    [Fact]
    public async Task Ask_can_propose_saving_an_attached_file()
    {
        conversation.Files.Add(await UploadedFile.PrepareAsync("2025 return.pdf", DocumentTextTests.Pdf(["Form 1065"])));

        var answer = await actions.ProposeSaveDocumentAsync("2025 return.pdf", "tax return", "Tax 2025", year: 2025);

        Assert.Contains("Proposed change 1", answer);
        var change = Assert.Single(conversation.Proposals);
        Assert.Contains(change.Details, d => d.StartsWith("Kept out of Ask"));
        await using (var db = dbFactory.CreateDbContext())
        {
            Assert.Empty(db.BusinessDocuments);
        }

        Assert.Contains("Saved", await change.Apply(CancellationToken.None));
        await using (var db = dbFactory.CreateDbContext())
        {
            var d = db.BusinessDocuments.Single();
            Assert.Equal((BusinessDocumentKind.TaxReturn, "Tax 2025", (int?)null, (int?)2025, true), (d.Kind, d.Description, d.PropertyId, d.Year, d.KeepFromAssistant));
        }
    }

    [Fact]
    public async Task Proposal_files_closing_papers_under_the_property()
    {
        conversation.Files.Add(await UploadedFile.PrepareAsync("hud1.pdf", DocumentTextTests.Pdf(["Settlement"])));

        await actions.ProposeSaveDocumentAsync("hud1.pdf", "closing", "Closing", property: "Taylor");
        await Assert.Single(conversation.Proposals).Apply(CancellationToken.None);

        await using var db = dbFactory.CreateDbContext();
        var d = db.BusinessDocuments.Single();
        Assert.Equal((BusinessDocumentKind.DeedAndClosing, taylorId, false), (d.Kind, d.PropertyId!.Value, d.KeepFromAssistant));
    }

    [Theory]
    [InlineData("LLC", BusinessDocumentKind.LlcAndFormation)]
    [InlineData("Operating agreement", BusinessDocumentKind.LlcAndFormation)]
    [InlineData("1099", BusinessDocumentKind.TaxForm)]
    [InlineData("Tax return", BusinessDocumentKind.TaxReturn)]
    [InlineData("insurance policy", BusinessDocumentKind.Insurance)]
    [InlineData("Deed/Closing", BusinessDocumentKind.DeedAndClosing)]
    [InlineData("Mortgage/Loan", BusinessDocumentKind.MortgageAndLoan)]
    [InlineData("Bank statement", BusinessDocumentKind.BankStatement)]
    [InlineData("TaxReturn", BusinessDocumentKind.TaxReturn)]
    public void Document_types_are_understood(string text, BusinessDocumentKind expected) =>
        Assert.Equal(expected, AssistantActions.ResolveDocumentKind(text));

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
