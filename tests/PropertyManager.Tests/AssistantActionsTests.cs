using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

/// <summary>The assistant only proposes changes; they happen when the owner applies them.</summary>
public sealed class AssistantActionsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-actions-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly AssistantActions actions;
    private readonly AssistantConversation conversation = new();
    private readonly AssistantService service;

    public AssistantActionsTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var unit = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B");
        var lease = new Lease { Unit = unit, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }], StartDate = new(2026, 1, 1), MonthlyRent = 1000m };
        lease.Payments.Add(new RentPayment { Date = new(2026, 10, 1), ForMonth = new(2026, 10, 1), Amount = 1000m });
        db.Leases.Add(lease);
        db.Vendors.Add(new Vendor { Name = "Ace Plumbing" });
        db.SaveChanges();

        var storage = new LocalDocumentStorage(root);
        var clock = new FixedTime(Now);
        actions = new AssistantActions(dbFactory, new ReceiptService(dbFactory, storage, clock), new LeaseDocumentService(dbFactory, storage, clock), clock) { Conversation = conversation };
        var options = new AssistantOptions { Providers = { ["Fake"] = new() { Type = AssistantProviderType.Ollama, Model = "fake" } } };
        service = new AssistantService(new StaticOptions(options), new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory)), null, actions);
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddExpense_WithReceipt_HappensOnlyWhenApplied()
    {
        conversation.Files.Add(await UploadedFile.PrepareAsync("invoice.pdf", DocumentTextTests.Pdf(["Invoice 1042: faucet $180.50"])));

        var answer = await actions.ProposeAddExpenseAsync("2026-10-20", "Taylor", 180.50m, "Kitchen faucet", "repairs", unit: "B",
            vendor: "Bob's Hardware", paidWith: "credit card", attachFileName: "invoice.pdf");

        Assert.StartsWith("Proposed change 1:", answer);
        var change = Assert.Single(conversation.Proposals);
        Assert.Contains("Where: 1354 Taylor St NW – Unit B", change.Details);
        Assert.Contains("Vendor: Bob's Hardware (new vendor)", change.Details);
        Assert.Contains("Paid with: Card", change.Details);
        await using (var db = dbFactory.CreateDbContext())
        {
            Assert.Equal(0, await db.Expenses.CountAsync());
        }

        var result = await service.ApplyAsync(conversation, change);

        Assert.Equal(ProposalState.Applied, change.State);
        Assert.Contains("Saved invoice.pdf as its receipt", result);
        await using var check = dbFactory.CreateDbContext();
        var expense = await check.Expenses.Include(e => e.Vendor).Include(e => e.Receipts).Include(e => e.Unit).SingleAsync();
        Assert.Equal((180.50m, ExpenseCategory.Repairs, "B", "Bob's Hardware", PaymentMethod.Card), (expense.Amount, expense.Category, expense.Unit!.Label, expense.Vendor!.Name, expense.PaidWith));
        Assert.Contains("faucet $180.50", Assert.Single(expense.Receipts).Text);
        Assert.Contains(conversation.History, m => m.Text.Contains("applied by the owner"));
        Assert.Equal(result, await service.ApplyAsync(conversation, change));
        Assert.Equal(1, await check.Expenses.CountAsync());
    }

    [Fact]
    public async Task BadInput_IsExplainedSoTheModelCanFixIt()
    {
        Assert.Contains("No property matches \"Maple\"", await actions.ProposeAddExpenseAsync("2026-10-20", "Maple", 10m, "x", "Repairs"));
        Assert.Contains("isn't a category", await actions.ProposeAddExpenseAsync("2026-10-20", "Taylor", 10m, "x", "Snacks"));
        Assert.Contains("has no unit \"C\"", await actions.ProposeAddExpenseAsync("2026-10-20", "Taylor", 10m, "x", "Repairs", unit: "C"));
        Assert.Contains("isn't a date", await actions.ProposeAddExpenseAsync("next week", "Taylor", 10m, "x", "Repairs"));
        Assert.Contains("No files are attached", await actions.ProposeAddExpenseAsync("2026-10-20", "shared", 10m, "x", "Repairs", attachFileName: "nope.pdf"));
        Assert.Empty(conversation.Proposals);
    }

    [Fact]
    public async Task RentPayment_FindsTheLease_WarnsAboutDoubleEntry_AndRecordsIt()
    {
        await actions.ProposeRecordRentPaymentAsync("Jane", 1000m, "2026-10-01", "2026-10", "zelle");
        await actions.ProposeRecordRentPaymentAsync("Taylor B", 1000m, "2026-11-01", "2026-11");

        var (october, november) = (conversation.Proposals[0], conversation.Proposals[1]);
        Assert.Contains(october.Details, d => d.StartsWith("⚠"));
        Assert.DoesNotContain(november.Details, d => d.StartsWith("⚠"));
        Assert.Equal("Record rent: $1,000.00 from Jane Sample for November 2026", november.Summary);

        service.Dismiss(conversation, october);
        await service.ApplyAsync(conversation, november);

        await using var db = dbFactory.CreateDbContext();
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)], await db.RentPayments.OrderBy(p => p.ForMonth).Select(p => p.ForMonth).ToListAsync());
        Assert.Equal(ProposalState.Dismissed, october.State);
    }

    [Fact]
    public async Task Maintenance_AddAndComplete()
    {
        Assert.StartsWith("Proposed", await actions.ProposeAddMaintenanceRequestAsync("Taylor B", "Kitchen sink leak", "high", vendor: "ace"));
        await service.ApplyAsync(conversation, conversation.Proposals[0]);
        int id;
        await using (var db = dbFactory.CreateDbContext())
        {
            var request = await db.MaintenanceRequests.Include(m => m.Vendor).SingleAsync();
            Assert.Equal((MaintenancePriority.High, "Ace Plumbing", new DateOnly(2026, 10, 27)), (request.Priority, request.Vendor!.Name, request.ReportedOn));
            id = request.Id;
        }

        Assert.StartsWith("Proposed", await actions.ProposeUpdateMaintenanceRequestAsync(id, completedOn: "2026-10-28"));
        Assert.Contains("Status: Open → Completed", conversation.Proposals[1].Details);
        await service.ApplyAsync(conversation, conversation.Proposals[1]);

        await using var check = dbFactory.CreateDbContext();
        var done = await check.MaintenanceRequests.SingleAsync();
        Assert.Equal((MaintenanceStatus.Completed, (DateOnly?)new DateOnly(2026, 10, 28)), (done.Status, done.CompletedOn));
    }

    [Fact]
    public async Task AttachedFiles_GoWithTheQuestion_AndProposalsComeBackWithTheReply()
    {
        var photo = await UploadedFile.PrepareAsync("leak.png", [0x89, 0x50, 0x4E, 0x47]);
        var csv = await UploadedFile.PrepareAsync("bills.csv", "date,amount\n2026-10-01,71.14\n"u8.ToArray());
        IReadOnlyList<ChatMessage>? sent = null;
        var model = new Scripted(
            messages =>
            {
                sent = messages;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "propose_add_vendor", new Dictionary<string, object?> { ["name"] = "Cool Air HVAC", ["trade"] = "HVAC" })]));
            },
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "I've proposed adding Cool Air HVAC; click Apply to add it.")));
        var options = new AssistantOptions { Providers = { ["Fake"] = new() { Type = AssistantProviderType.Ollama, Model = "fake" } } };
        var withModel = new AssistantService(new StaticOptions(options), new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory)),
            _ => new ChatClientBuilder(model).UseFunctionInvocation().Build(), actions);

        var reply = await withModel.AskAsync(conversation, "Fake", "Add this vendor", files: [photo, csv]);

        var question = sent!.Last(m => m.Role == ChatRole.User);
        Assert.Equal("image/png", question.Contents.OfType<DataContent>().Single().MediaType);
        Assert.Contains(question.Contents.OfType<TextContent>(), t => t.Text.Contains("2026-10-01,71.14"));
        Assert.Equal("Add vendor: Cool Air HVAC", Assert.Single(reply.Changes).Summary);
        Assert.Contains("Add this vendor\n[Attached: leak.png, bills.csv.", conversation.History.First().Text);
        Assert.Equal(2, conversation.Files.Count);
        Assert.Contains("71.14", await actions.ReadUploadedFileAsync("bills"));
    }

    [Fact]
    public async Task UnsupportedFiles_AreRefused() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => UploadedFile.PrepareAsync("tool.exe", [1, 2]));

    private sealed class Scripted(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] responses) : IChatClient
    {
        private int next;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(responses[Math.Min(next++, responses.Length - 1)](messages.ToList()));

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
