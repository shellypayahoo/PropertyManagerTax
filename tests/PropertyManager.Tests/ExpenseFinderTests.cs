using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class ExpenseFinderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 17, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-finder-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly LocalDocumentStorage storage;
    private readonly AssistantOptions settings = new() { DefaultProvider = "Fake", Providers = { ["Fake"] = new() { Type = AssistantProviderType.Ollama, Model = "fake" } } };
    private readonly int aceId;
    private readonly int taylorBId;
    private readonly int invoiceTextId;

    public ExpenseFinderTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        storage = new LocalDocumentStorage(root);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        taylorBId = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B").Id;
        var ace = new Vendor { Name = "Ace Plumbing", Trade = "Plumber" };
        var tenant = new Tenant { FirstName = "Jane", LastName = "Sample" };
        db.AddRange(ace, tenant);
        db.SaveChanges();
        aceId = ace.Id;

        var key = storage.SaveAsync(new MemoryStream(DocumentTextTests.Pdf(["Invoice 1042 total $180.50"])), "invoice.pdf").GetAwaiter().GetResult();
        var invoice = Text("Here's the invoice for the faucet, total is $180.50", Now.AddDays(-2), vendorId: ace.Id);
        invoice.Attachments.Add(new TextAttachment { Guid = "a1", FileName = "invoice.pdf", ContentType = "application/pdf", Status = TextAttachmentStatus.Saved, StorageKey = key, Text = "Invoice 1042 total $180.50" });
        db.TextMessages.AddRange(
            invoice,
            Text("On my way, 10 minutes", Now.AddDays(-1), vendorId: ace.Id),
            Text("Old bill $95 for the drain", Now.AddDays(-45), vendorId: ace.Id),
            Text("I paid the locksmith $60, can you take it off rent?", Now.AddDays(-1), tenantId: tenant.Id));
        db.SaveChanges();
        invoiceTextId = invoice.Id;
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TextMessage Text(string text, DateTimeOffset at, int? vendorId = null, int? tenantId = null) =>
        new() { Guid = Guid.NewGuid().ToString(), ChatIdentifier = "c", ContactHandle = "h", SentAt = at, Text = text, VendorId = vendorId, TenantId = tenantId };

    private ExpenseFinder Finder(IChatClient client) =>
        new(dbFactory, new StaticOptions(settings), new FixedTime(Now), storage, _ => client);

    private const string Found = """
        Here you go:
        ```json
        {"expenses": [{"message": 1, "date": "2026-11-05", "amount": "180.50", "description": "Kitchen faucet replacement",
          "category": "Repairs", "property": "Taylor", "unit": "B", "paidWith": "Zelle", "evidence": "total is $180.50", "receiptFile": 1}]}
        ```
        """;

    [Fact]
    public async Task Finds_charges_in_new_vendor_messages_only()
    {
        var model = new Scripted(_ => Found);

        var summary = await Finder(model).RunAsync();

        Assert.Equal((2, 1, 0), (summary.MessagesChecked, summary.Suggested, summary.VendorsFailed));
        var prompt = Assert.Single(model.Prompts);
        Assert.Contains("Vendor: Ace Plumbing (Plumber)", prompt);
        Assert.Contains("[message 1]", prompt);
        Assert.Contains("Invoice 1042 total $180.50", prompt);
        Assert.Contains("On my way", prompt);
        // Older messages are only context, never checked for charges themselves.
        var (context, checkNow) = (prompt[..prompt.IndexOf("New messages", StringComparison.Ordinal)], prompt[prompt.IndexOf("New messages", StringComparison.Ordinal)..]);
        Assert.Contains("Old bill", context);
        Assert.DoesNotContain("Old bill", checkNow);
        Assert.DoesNotContain("locksmith", prompt);

        await using var db = dbFactory.CreateDbContext();
        var s = await db.ExpenseSuggestions.SingleAsync();
        Assert.Equal((SuggestionStatus.Pending, 180.50m, new DateOnly(2026, 11, 5), "Kitchen faucet replacement", ExpenseCategory.Repairs, PaymentMethod.Zelle),
            (s.Status, s.Amount, s.Date, s.Description, s.Category, s.PaidWith!.Value));
        Assert.Equal((aceId, taylorBId, invoiceTextId), (s.VendorId!.Value, s.UnitId!.Value, s.TextMessageId!.Value));
        Assert.NotNull(s.PropertyId);
        Assert.NotNull(s.TextAttachmentId);
        Assert.Null(s.DuplicateOfExpenseId);

        // Each message is checked once.
        Assert.Equal(0, (await Finder(model).RunAsync()).MessagesChecked);
        Assert.Single(model.Prompts);
    }

    [Fact]
    public async Task Flags_a_likely_duplicate()
    {
        await using (var db = dbFactory.CreateDbContext())
        {
            db.Expenses.Add(new Expense { Date = new(2026, 11, 6), Amount = 180.50m, Description = "Zelle to Ace", Category = ExpenseCategory.Repairs });
            await db.SaveChangesAsync();
        }

        await Finder(new Scripted(_ => Found)).RunAsync();

        await using var check = dbFactory.CreateDbContext();
        Assert.NotNull((await check.ExpenseSuggestions.SingleAsync()).DuplicateOfExpenseId);
    }

    [Fact]
    public async Task A_failed_check_is_tried_again_next_time()
    {
        var summary = await Finder(new Scripted(_ => throw new HttpRequestException("429 Too Many Requests"))).RunAsync();

        Assert.Equal((0, 1), (summary.MessagesChecked, summary.VendorsFailed));
        Assert.Contains("Ace Plumbing", summary.Problem);
        await using var db = dbFactory.CreateDbContext();
        Assert.Equal(2, await db.TextMessages.CountAsync(m => !m.ExpenseChecked && m.VendorId == aceId));
    }

    [Fact]
    public async Task Nothing_runs_without_an_AI_model()
    {
        settings.Providers.Clear();
        var model = new Scripted(_ => Found);

        var summary = await Finder(model).RunAsync();

        Assert.NotNull(summary.Problem);
        Assert.Empty(model.Prompts);
    }

    [Fact]
    public async Task Approving_adds_the_expense_with_its_receipt()
    {
        await Finder(new Scripted(_ => Found)).RunAsync();
        var service = new ExpenseSuggestionService(dbFactory, new ReceiptService(dbFactory, storage, TimeProvider.System), storage);
        var pending = Assert.Single(await service.PendingAsync());
        Assert.Equal(1, await service.PendingCountAsync());
        var edited = new ExpenseSuggestion
        {
            Date = pending.Date, Amount = 185m, Description = "Kitchen faucet", Category = pending.Category,
            PropertyId = pending.PropertyId, UnitId = pending.UnitId, VendorId = pending.VendorId, PaidWith = pending.PaidWith,
        };

        var (expense, error) = await service.ApproveAsync(pending.Id, edited);

        Assert.Null(error);
        await using var db = dbFactory.CreateDbContext();
        var saved = await db.Expenses.Include(e => e.Receipts).SingleAsync(e => e.Id == expense!.Id);
        Assert.Equal((185m, "Kitchen faucet", aceId, taylorBId), (saved.Amount, saved.Description, saved.VendorId!.Value, saved.UnitId!.Value));
        Assert.Equal("invoice.pdf", Assert.Single(saved.Receipts).FileName);
        Assert.Contains("total is $180.50", saved.Notes);
        Assert.Equal(SuggestionStatus.Approved, (await db.ExpenseSuggestions.SingleAsync()).Status);
        Assert.Empty(await service.PendingAsync());
    }

    [Fact]
    public async Task Approving_needs_a_property_and_dismissing_adds_nothing()
    {
        await Finder(new Scripted(_ => Found.Replace("\"Taylor\"", "null"))).RunAsync();
        var service = new ExpenseSuggestionService(dbFactory, new ReceiptService(dbFactory, storage, TimeProvider.System), storage);
        var pending = Assert.Single(await service.PendingAsync());
        Assert.True(pending.NeedsProperty);

        var (_, error) = await service.ApproveAsync(pending.Id, pending);
        Assert.Contains("property", error);

        await service.DismissAsync(pending.Id);
        await using var db = dbFactory.CreateDbContext();
        Assert.Equal(SuggestionStatus.Dismissed, (await db.ExpenseSuggestions.SingleAsync()).Status);
        Assert.Empty(db.Expenses.Where(e => e.Amount == 180.50m));
    }

    [Fact]
    public void Answers_without_JSON_are_reported()
    {
        Assert.Empty(ExpenseFinder.Parse("""{"expenses": []}"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ExpenseFinder.Parse("No charges here."));
    }

    private sealed class Scripted(Func<string, string> answer) : IChatClient
    {
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
            var reply = answer(prompt);
            Prompts.Add(prompt);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StaticOptions(AssistantOptions value) : IOptionsMonitor<AssistantOptions>
    {
        public AssistantOptions CurrentValue => value;
        public AssistantOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AssistantOptions, string?> listener) => null;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
