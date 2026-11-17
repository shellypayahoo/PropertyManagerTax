using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Web.Configuration;

namespace PropertyManager.Tests;

/// <summary>Settings saved in the app override appsettings.json and apply without restarting.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"pm-settings-{Guid.NewGuid():N}.db");
    private readonly string connectionString;
    private readonly TestDbContextFactory dbFactory;

    public SettingsTests()
    {
        connectionString = $"Data Source={file};Pooling=False";
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connectionString).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
    }

    private readonly string keysFolder = Path.Combine(Path.GetTempPath(), $"pm-keys-{Guid.NewGuid():N}");

    private SecretProtector Secrets(string? folder = null) => new(folder ?? keysFolder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(file);
        foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), "pm-keys-*"))
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SavedSettings_OverrideTheFile_ApplyRightAway_AndCanBeUndone()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Business:Name"] = "From file", ["Features:Email"] = "true" })
            .AddDatabaseSettings(connectionString, Secrets())
            .Build();
        var services = new ServiceCollection()
            .Configure<BusinessOptions>(configuration.GetSection(BusinessOptions.Section))
            .Configure<FeatureOptions>(configuration.GetSection(FeatureOptions.Section))
            .BuildServiceProvider();
        var business = services.GetRequiredService<IOptionsMonitor<BusinessOptions>>();
        var features = services.GetRequiredService<IOptionsMonitor<FeatureOptions>>();
        var settings = new SettingsService(dbFactory, configuration, Secrets());
        Assert.Equal("From file", business.CurrentValue.Name);

        await settings.SaveAsync(new Dictionary<string, string?> { ["Business:Name"] = "Maple Rentals", ["Business:RentDueDay"] = "5", ["Features:Email"] = "false" });

        Assert.Equal(("Maple Rentals", 5), (business.CurrentValue.Name, business.CurrentValue.RentDueDay));
        Assert.False(features.CurrentValue.Email);
        Assert.Equal(3, (await settings.GetSavedAsync()).Count);

        await settings.SaveAsync(new Dictionary<string, string?> { ["Business:Name"] = null, ["Features:Email"] = null });

        Assert.Equal(("From file", 5), (business.CurrentValue.Name, business.CurrentValue.RentDueDay));
        Assert.True(features.CurrentValue.Email);
        Assert.Equal(["Business:RentDueDay"], (await settings.GetSavedAsync()).Keys);
    }

    [Fact]
    public async Task ApiKeys_AreStoredEncrypted_AndReadBackOnThisComputer()
    {
        var configuration = new ConfigurationBuilder().AddDatabaseSettings(connectionString, Secrets()).Build();
        var settings = new SettingsService(dbFactory, configuration, Secrets());

        await settings.SaveAsync(new Dictionary<string, string?>
        {
            ["Assistant:Providers:AzureFoundry:ApiKey"] = "sk-secret-123",
            ["Assistant:Providers:AzureFoundry:Model"] = "gpt-5-mini",
        });

        // The app reads the key back; the model name isn't a secret and is stored as typed.
        Assert.Equal("sk-secret-123", configuration["Assistant:Providers:AzureFoundry:ApiKey"]);
        var stored = await settings.GetSavedAsync();
        Assert.StartsWith("enc:v1:", stored["Assistant:Providers:AzureFoundry:ApiKey"]);
        Assert.DoesNotContain("sk-secret-123", stored["Assistant:Providers:AzureFoundry:ApiKey"]);
        Assert.Equal("gpt-5-mini", stored["Assistant:Providers:AzureFoundry:Model"]);
        // The database file itself never holds the key in readable form.
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain("sk-secret-123", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file)));
    }

    [Fact]
    public async Task ApiKey_FromAnotherComputer_CountsAsNotSet()
    {
        var here = new ConfigurationBuilder().AddDatabaseSettings(connectionString, Secrets()).Build();
        await new SettingsService(dbFactory, here, Secrets()).SaveAsync(new Dictionary<string, string?> { ["Assistant:Providers:Claude:ApiKey"] = "sk-secret-123" });

        // A restored backup on a new computer has the database but not this computer's encryption key.
        var elsewhere = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:Providers:Claude:Model"] = "claude" })
            .AddDatabaseSettings(connectionString, Secrets(Path.Combine(Path.GetTempPath(), $"pm-keys-{Guid.NewGuid():N}")))
            .Build();

        Assert.Null(elsewhere["Assistant:Providers:Claude:ApiKey"]);
        Assert.Equal("claude", elsewhere["Assistant:Providers:Claude:Model"]);
    }

    [Fact]
    public void BeforeTheDatabaseExists_ThereAreNoSavedSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Business:Name"] = "From file" })
            .AddDatabaseSettings($"Data Source={Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "x.db")};Mode=ReadOnly", Secrets())
            .Build();

        Assert.Equal("From file", configuration["Business:Name"]);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
