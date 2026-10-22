using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

/// <summary>Runs the assistant's tools against a test database and drives it with a scripted stand-in for the AI model.</summary>
public sealed class AssistantTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly AssistantTools tools;

    public AssistantTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-US");
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();

        var unit = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B");
        var tenant = new Tenant { FirstName = "Jane", LastName = "Sample", Phone = "(202) 555-1234" };
        var lease = new Lease { Unit = unit, Tenants = [tenant], StartDate = new(2026, 1, 1), MonthlyRent = 1000m };
        lease.Payments.Add(new RentPayment { Date = new(2026, 3, 1), ForMonth = new(2026, 3, 1), Amount = 1000m });
        db.Leases.Add(lease);
        var plumber = new Vendor { Name = "Ace Plumbing", Trade = "Plumber" };
        db.Expenses.Add(new Expense { PropertyId = unit.PropertyId, Unit = unit, Vendor = plumber, Date = new(2026, 3, 5), Category = ExpenseCategory.Repairs, Amount = 180m, Description = "Kitchen sink repair" });
        db.SaveChanges();

        db.MessageContacts.Add(new MessageContact { Handle = "2025551234", Name = "Jane Sample", Included = true, TenantId = tenant.Id, MessageCount = 2 });
        db.TextMessages.AddRange(
            new TextMessage { Guid = "1", ChatIdentifier = "+12025551234", SenderHandle = "+12025551234", ContactHandle = "2025551234", TenantId = tenant.Id,
                SentAt = new DateTimeOffset(2026, 3, 2, 14, 0, 0, TimeSpan.Zero), Text = "The kitchen sink has a leak" },
            new TextMessage { Guid = "2", ChatIdentifier = "+12025551234", IsFromMe = true, ContactHandle = "2025551234", TenantId = tenant.Id,
                SentAt = new DateTimeOffset(2026, 3, 2, 15, 0, 0, TimeSpan.Zero), Text = "I'll send the plumber Thursday" });
        db.SaveChanges();

        tools = new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory), new FixedTime(new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero)));
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task SearchTextMessages_ShowsDateAndSender()
    {
        var result = await tools.SearchTextMessagesAsync("LEAK");

        Assert.Contains("1 message(s)", result);
        Assert.Contains("2026-03-02", result);
        Assert.Contains("Jane Sample: The kitchen sink has a leak", result);
        Assert.Equal("No messages found.", await tools.SearchTextMessagesAsync("roof"));
    }

    [Fact]
    public async Task GetConversation_FindsContactByNameOrPhone()
    {
        var byName = await tools.GetConversationAsync("jane");
        var byPhone = await tools.GetConversationAsync("202-555-1234");

        Assert.Contains("2 message(s)", byName);
        Assert.Contains("Me (to Jane Sample): I'll send the plumber Thursday", byName);
        Assert.Equal(byName, byPhone);
        Assert.Contains("No text contact matches", await tools.GetConversationAsync("neighbor"));
    }

    [Fact]
    public async Task SearchExpenses_FiltersByPropertyCategoryAndText()
    {
        var result = await tools.SearchExpensesAsync(property: "taylor", category: "repairs", text: "ace");

        Assert.Contains("1 expense(s)", result);
        Assert.Contains("total $180.00", result);
        Assert.Contains("vendor Ace Plumbing", result);
        Assert.StartsWith("No expenses found", await tools.SearchExpensesAsync(property: "1st"));
    }

    [Fact]
    public async Task RentStatusAndTenants_DescribeTheLease()
    {
        var rent = await tools.GetRentStatusAsync("2026-03", "2026-04");
        var people = await tools.ListPropertiesAndTenantsAsync();

        Assert.Contains("March 2026:", rent);
        Assert.Contains("(Jane Sample): due $1,000.00, paid $1,000.00, Paid [$1,000.00 on 2026-03-01 by Zelle]", rent);
        Assert.Contains("April 2026:", rent);
        Assert.Contains("Upcoming", rent);
        Assert.Contains("Jane Sample, 2026-01-01 to month-to-month, rent $1,000.00", people);
        Assert.Contains("Ace Plumbing (Plumber)", people);
    }

    [Fact]
    public async Task Ask_CallsToolsAndReportsLookups()
    {
        var model = new ScriptedChatClient(
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call1", "search_text_messages", new Dictionary<string, object?> { ["query"] = "leak" })])),
            messages =>
            {
                var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
                Assert.Contains("kitchen sink has a leak", result.Result?.ToString());
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Jane Sample reported the leak on March 2."));
            });
        var service = Service(_ => model);
        var conversation = new AssistantConversation();

        var reply = await service.AskAsync(conversation, "Fake", "When was the leak reported?");

        Assert.Equal("Jane Sample reported the leak on March 2.", reply.Text);
        Assert.False(reply.Declined);
        var lookup = Assert.Single(reply.Lookups);
        Assert.Equal(new AssistantLookup("search_text_messages", "query: leak"), lookup);
        Assert.Equal(ChatRole.System, model.Received[0].First().Role);
        Assert.False(conversation.IsEmpty);
    }

    [Fact]
    public async Task Ask_SwitchingModels_CarriesOverOnlyQuestionsAndAnswers()
    {
        var first = new ScriptedChatClient(
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call1", "list_text_contacts", new Dictionary<string, object?>())])),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "You text with Jane Sample.")));
        var second = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Yes.")));
        var service = Service(p => p.Name == "Fake" ? first : second);
        var conversation = new AssistantConversation();

        await service.AskAsync(conversation, "Fake", "Who do I text with?");
        await service.AskAsync(conversation, "Other", "Is she a tenant?");

        var sent = second.Received.Single();
        Assert.DoesNotContain(sent.SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);
        Assert.Equal(["Who do I text with?", "You text with Jane Sample.", "Is she a tenant?"], sent.Skip(1).Select(m => m.Text));
    }

    [Fact]
    public async Task Ask_DeclinedAnswer_IsReportedAndNotKept()
    {
        var model = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.ContentFilter });
        var conversation = new AssistantConversation();

        var reply = await Service(_ => model).AskAsync(conversation, "Fake", "Question");

        Assert.True(reply.Declined);
        Assert.Equal("The model declined to answer this question.", reply.Text);
        Assert.True(conversation.IsEmpty);
    }

    [Fact]
    public void Providers_NeedTheirSettingsFilledIn()
    {
        var placeholder = new AssistantProviderOptions { Type = AssistantProviderType.AzureFoundry, Model = "<deployment-name>", Endpoint = "https://x/openai/v1/", ApiKey = "k" };
        Assert.False(placeholder.IsConfigured);
        Assert.Equal(["Model"], placeholder.MissingSettings);
        Assert.True(new AssistantProviderOptions { Type = AssistantProviderType.AzureFoundry, Model = "gpt", Endpoint = "https://x/openai/v1/", ApiKey = "k" }.IsConfigured);
        Assert.False(new AssistantProviderOptions { Type = AssistantProviderType.AnthropicFoundry, Resource = "<your-resource>", ApiKey = "k" }.IsConfigured);
        Assert.True(new AssistantProviderOptions { Type = AssistantProviderType.Ollama, Model = "llama3.1" }.IsConfigured);
        Assert.Contains("sent to Anthropic", new AssistantProviderOptions { Type = AssistantProviderType.Anthropic }.DataNotice);
    }

    [Theory]
    [InlineData(AssistantProviderType.Anthropic)]
    [InlineData(AssistantProviderType.AnthropicFoundry)]
    [InlineData(AssistantProviderType.AzureFoundry)]
    [InlineData(AssistantProviderType.Ollama)]
    public void ChatClientFactory_BuildsEveryProviderType(AssistantProviderType type)
    {
        // Building a client doesn't contact the service, so dummy settings are fine.
        using var client = ChatClientFactory.Create(new AssistantProviderOptions
        {
            Type = type, Model = "model", ApiKey = "test-key", Resource = "resource", Endpoint = "https://example.invalid/openai/v1/",
        });

        Assert.NotNull(client.GetService<FunctionInvokingChatClient>());
    }

    [Theory]
    [InlineData("https://myres.openai.azure.com/openai/v1/")]
    [InlineData("https://myres.openai.azure.com")]
    [InlineData("https://myres.openai.azure.com/openai/responses?api-version=2025-04-01-preview")]
    [InlineData("https://myres.openai.azure.com/openai/deployments/gpt-5-mini/chat/completions?api-version=2025-01-01-preview")]
    public void FoundryEndpoint_AcceptsAnyUrlFromThePortal(string endpoint) =>
        Assert.Equal("https://myres.openai.azure.com/openai/v1/", ChatClientFactory.FoundryEndpoint(endpoint).ToString());

    [Fact]
    public void FoundryEndpoint_KeepsTheAiServicesHost() =>
        Assert.Equal("https://myres.services.ai.azure.com/openai/v1/",
            ChatClientFactory.FoundryEndpoint("https://myres.services.ai.azure.com/api/projects/rentals").ToString());

    [Fact]
    public async Task AzureFoundry_PostsToTheV1ChatCompletionsUrl()
    {
        var handler = new RecordingHandler();
        var client = new OpenAI.Chat.ChatClient("gpt-5-mini", new System.ClientModel.ApiKeyCredential("k"), new OpenAI.OpenAIClientOptions
        {
            Endpoint = ChatClientFactory.FoundryEndpoint("https://myres.services.ai.azure.com/api/projects/rentals"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        });

        await Assert.ThrowsAnyAsync<Exception>(() => client.CompleteChatAsync("hi"));

        Assert.Equal("https://myres.services.ai.azure.com/openai/v1/chat/completions", handler.Url?.ToString());
        Assert.Contains("\"model\":\"gpt-5-mini\"", handler.Body);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? Url { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }
    }

    [Fact]
    public async Task Ask_UnconfiguredModel_Throws()
    {
        var options = new AssistantOptions { Providers = { ["Foundry"] = new() { Type = AssistantProviderType.AzureFoundry, Model = "<deployment-name>" } } };
        var service = new AssistantService(new StaticOptions(options), tools, _ => throw new InvalidOperationException("should not be created"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AskAsync(new AssistantConversation(), "Foundry", "Hi"));
        Assert.Contains("missing its settings", error.Message);
    }

    private AssistantService Service(Func<AssistantProviderOptions, IChatClient> model)
    {
        var options = new AssistantOptions
        {
            Providers =
            {
                ["Fake"] = new() { Type = AssistantProviderType.Ollama, Model = "fake" },
                ["Other"] = new() { Type = AssistantProviderType.Ollama, Model = "other" },
            },
        };
        // Wrap the scripted model the same way real ones are, so tool calls are carried out.
        return new AssistantService(new StaticOptions(options), tools, p => new ChatClientBuilder(model(p)).UseFunctionInvocation().Build());
    }

    /// <summary>Returns scripted responses in order and records the messages it was sent.</summary>
    private sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] responses) : IChatClient
    {
        private int next;
        public List<IReadOnlyList<ChatMessage>> Received { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Received.Add(list);
            return Task.FromResult(responses[next++](list));
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
