using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class ModelChoiceTests
{
    private static AssistantOptions Settings(string? defaultProvider = null) => new()
    {
        DefaultProvider = defaultProvider,
        Providers =
        {
            ["AzureFoundry"] = new()
            {
                Type = AssistantProviderType.AzureFoundry, Model = "gpt-6.1-sol", Endpoint = "https://res.services.ai.azure.com/openai/v1/", ApiKey = "k",
                Models = ["gpt-5-mini", " gpt-4.1 ", "GPT-6.1-SOL", "", "<deployment-name>", "gpt-5-mini"],
            },
            ["Ollama"] = new() { Type = AssistantProviderType.Ollama, Model = "llama3.1" },
        },
    };

    [Fact]
    public void Each_provider_offers_its_other_models_too()
    {
        var choices = Settings().Choices();

        Assert.Equal(["AzureFoundry", "AzureFoundry/gpt-5-mini", "AzureFoundry/gpt-4.1", "Ollama"], choices.Select(c => c.Name));
        Assert.Equal(["AzureFoundry: gpt-6.1-sol", "AzureFoundry: gpt-5-mini", "AzureFoundry: gpt-4.1", "Ollama: llama3.1"], choices.Select(c => c.Label));
        var mini = choices[1];
        Assert.Equal(("gpt-5-mini", "AzureFoundry", "k"), (mini.Model, mini.ProviderName, mini.ApiKey));
        Assert.True(mini.IsConfigured);
    }

    [Fact]
    public void The_default_can_be_another_model()
    {
        Assert.Equal("gpt-5-mini", Settings("AzureFoundry/gpt-5-mini").Default()!.Model);
        Assert.Equal("gpt-6.1-sol", Settings("AzureFoundry").Default()!.Model);
        Assert.Equal("gpt-6.1-sol", Settings("Gone").Default()!.Model);
    }

    [Fact]
    public async Task Asking_another_model_uses_it_with_the_same_endpoint_and_key()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        await using (var db = dbFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
        var used = new List<AssistantProviderOptions>();
        var service = new AssistantService(new StaticOptions(Settings()),
            new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory)),
            p => { used.Add(p); return new Answering("Hi"); });

        var reply = await service.AskAsync(new AssistantConversation(), "AzureFoundry/gpt-5-mini", "Hello?");

        Assert.Equal("Hi", reply.Text);
        Assert.Equal("AzureFoundry/gpt-5-mini", reply.Provider);
        Assert.Equal(("gpt-5-mini", "https://res.services.ai.azure.com/openai/v1/"), (used.Single().Model, used.Single().Endpoint));
    }

    [Fact]
    public async Task Finds_deployments_when_the_resource_lists_them()
    {
        var http = new Handler(HttpStatusCode.OK, """
            {"data": [
              {"id": "gpt-6.1-sol", "model": "gpt-6.1", "status": "succeeded"},
              {"id": "mini", "model": "gpt-5-mini", "status": "succeeded"},
              {"id": "still-making", "model": "gpt-4.1", "status": "creating"}
            ]}
            """);

        var found = await new ModelCatalog(new HttpClient(http)).FindDeploymentsAsync(Settings().Providers["AzureFoundry"]);

        Assert.Equal(["gpt-6.1-sol", "mini"], found!.Select(d => d.Name));
        Assert.Equal("gpt-5-mini", found[1].Model);
        Assert.Equal("https://res.services.ai.azure.com/openai/deployments?api-version=2022-12-01", http.Url);
        Assert.Equal("k", http.Key);
    }

    [Fact]
    public async Task No_list_when_the_resource_doesnt_offer_one()
    {
        var catalog = new ModelCatalog(new HttpClient(new Handler(HttpStatusCode.NotFound, "{}")));

        Assert.Null(await catalog.FindDeploymentsAsync(Settings().Providers["AzureFoundry"]));
        Assert.Null(await catalog.FindDeploymentsAsync(Settings().Providers["Ollama"]));
    }

    [Fact]
    public async Task Test_says_which_model_names_work()
    {
        var foundry = Settings().Choices()[0];
        var catalog = new ModelCatalog(new HttpClient(new Handler(HttpStatusCode.OK, "{}")),
            p => p.Model == "typo" ? new Failing(new HttpRequestException("404 DeploymentNotFound")) : new Answering("OK"));

        Assert.Null(await catalog.TestAsync(foundry, "gpt-5-mini"));
        Assert.Contains("no deployment named \"typo\"", await catalog.TestAsync(foundry, "typo"));
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Key { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (Url, Key) = (request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("api-key", out var k) ? k.Single() : null);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private class Answering(string text) : IChatClient
    {
        public virtual Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class Failing(Exception error) : Answering("")
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(error);
    }

    private sealed class StaticOptions(AssistantOptions value) : IOptionsMonitor<AssistantOptions>
    {
        public AssistantOptions CurrentValue => value;
        public AssistantOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AssistantOptions, string?> listener) => null;
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
