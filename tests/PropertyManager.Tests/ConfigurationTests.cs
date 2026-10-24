using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Web.Configuration;

namespace PropertyManager.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;

    public ConfigurationTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose() => connection.Dispose();

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Defaults_KeepEverythingOn()
    {
        var empty = Config();

        var features = empty.GetSection(FeatureOptions.Section).Get<FeatureOptions>() ?? new FeatureOptions();
        var business = empty.GetSection(BusinessOptions.Section).Get<BusinessOptions>() ?? new BusinessOptions();

        Assert.True(features.TextMessages && features.Email && features.Calendar && features.Assistant && features.Receipts && features.PropertyPhotos);
        Assert.Equal((1, 5, (string?)null), (business.RentDueDay, business.LateAfterDays, business.Name));
        Assert.DoesNotContain(ConfigurationCheck.Run(empty), p => p.IsError);
    }

    [Fact]
    public void Check_NamesBadSettings()
    {
        var config = Config(
            ("Business:RentDueDay", "31"),
            ("Assistant:DefaultProvider", "Missing"),
            ("Assistant:Providers:Mine:Type", "Gemini"),
            ("Assistant:Providers:Mine:Model", "x"),
            ("Setup:Properties:0:Name", "Duplex"),
            ("Setup:Properties:0:Units:0:Label", "A"),
            ("Setup:Properties:0:Units:1:Label", "a"));

        var errors = ConfigurationCheck.Run(config).Where(p => p.IsError).Select(p => p.Message).ToList();

        Assert.Contains(errors, e => e.Contains("RentDueDay is 31"));
        Assert.Contains(errors, e => e.Contains("DefaultProvider is \"Missing\""));
        Assert.Contains(errors, e => e.Contains("\"Mine\" has Type \"Gemini\""));
        Assert.Contains(errors, e => e.Contains("lists unit A more than once"));
    }

    [Fact]
    public void Check_TurnedOffFeaturesArentChecked()
    {
        var config = Config(("Features:Assistant", "false"), ("Features:Email", "false"), ("Features:Calendar", "false"),
            ("Assistant:DefaultProvider", "Missing"));

        var problems = ConfigurationCheck.Run(config);

        Assert.DoesNotContain(problems, p => p.Message.Contains("DefaultProvider") || p.Message.Contains("ClientId"));
    }

    [Fact]
    public async Task Setup_CreatesTheConfiguredPortfolioOnlyInAnEmptyDatabase()
    {
        List<PropertySetup> setup =
        [
            new() { Name = "Maple Duplex", Street = "12 Maple Ave", City = "Silver Spring", State = "MD", PostalCode = "20910",
                Units = [new() { Label = "1", MarketRent = 2100m }, new() { Label = "2", MarketRent = 2000m }] },
            new() { Name = "Oak House", Units = [new() { Label = "Main", MarketRent = 3000m }] },
        ];

        await using (var db = dbFactory.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, setup);
            await DbSeeder.SeedAsync(db, DbSeeder.Sample);
        }

        await using var check = dbFactory.CreateDbContext();
        var properties = await check.Properties.Include(p => p.Units).OrderBy(p => p.Name).ToListAsync();
        Assert.Equal(["Maple Duplex", "Oak House"], properties.Select(p => p.Name));
        Assert.Equal("12 Maple Ave, Silver Spring, MD 20910", properties[0].FullAddress);
        Assert.Equal(("Oak House", "Washington", "DC"), (properties[1].Street, properties[1].City, properties[1].State));
        Assert.Equal(4100m, properties[0].Units.Sum(u => u.MarketRent));
    }

    [Fact]
    public void Assistant_LeavesOutLookupsForFeaturesThatAreOff()
    {
        var off = new Monitor<FeatureOptions>(new FeatureOptions { TextMessages = false, Email = false });
        var tools = new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory), features: off).All().Select(t => t.Name).ToList();
        var all = new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory)).All().Select(t => t.Name).ToList();

        Assert.DoesNotContain("search_text_messages", tools);
        Assert.DoesNotContain("search_emails", tools);
        Assert.Contains("search_expenses", tools);
        Assert.Contains("search_text_messages", all);
        Assert.Contains("search_emails", all);
    }

    [Fact]
    public void Assistant_StartsOnTheDefaultProvider_AndSupportsOpenAI()
    {
        var options = new AssistantOptions
        {
            DefaultProvider = "OpenAI",
            AllowPhotos = false,
            Providers =
            {
                ["Local"] = new() { Type = AssistantProviderType.Ollama, Model = "llama3.1" },
                ["OpenAI"] = new() { Type = AssistantProviderType.OpenAI, Model = "gpt-5", ApiKey = "k" },
            },
        };
        var service = new AssistantService(new StaticOptions(options), new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory)));

        Assert.Equal("OpenAI", service.DefaultProvider?.Name);
        Assert.False(service.AllowPhotosByDefault);
        Assert.Contains("sent to OpenAI", options.Providers["OpenAI"].DataNotice);
        using var client = ChatClientFactory.Create(options.Providers["OpenAI"]);
        Assert.NotNull(client.GetService<FunctionInvokingChatClient>());
        options.DefaultProvider = null;
        Assert.Equal("Local", service.DefaultProvider?.Name);
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
