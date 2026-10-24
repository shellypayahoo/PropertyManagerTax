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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(file);
    }

    [Fact]
    public async Task SavedSettings_OverrideTheFile_ApplyRightAway_AndCanBeUndone()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Business:Name"] = "From file", ["Features:Email"] = "true" })
            .AddDatabaseSettings(connectionString)
            .Build();
        var services = new ServiceCollection()
            .Configure<BusinessOptions>(configuration.GetSection(BusinessOptions.Section))
            .Configure<FeatureOptions>(configuration.GetSection(FeatureOptions.Section))
            .BuildServiceProvider();
        var business = services.GetRequiredService<IOptionsMonitor<BusinessOptions>>();
        var features = services.GetRequiredService<IOptionsMonitor<FeatureOptions>>();
        var settings = new SettingsService(dbFactory, configuration);
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
    public void BeforeTheDatabaseExists_ThereAreNoSavedSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Business:Name"] = "From file" })
            .AddDatabaseSettings($"Data Source={Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "x.db")};Mode=ReadOnly")
            .Build();

        Assert.Equal("From file", configuration["Business:Name"]);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
