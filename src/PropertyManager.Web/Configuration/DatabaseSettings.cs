using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Web.Configuration;

/// <summary>
/// Adds settings saved in the app (the AppSettings table) on top of appsettings.json, user secrets, and environment
/// variables, so what an owner sets on the Settings page wins. Before the database exists there are none.
/// </summary>
public sealed class DatabaseSettingsProvider(string connectionString) : ConfigurationProvider
{
    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Key, Value FROM AppSettings";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                data[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        catch (SqliteException)
        {
            // No database or table yet (first start, before migrations): nothing saved in the app.
        }
        Data = data;
    }
}

public sealed class DatabaseSettingsSource(string connectionString) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DatabaseSettingsProvider(connectionString);
}

public static class DatabaseSettingsExtensions
{
    public static IConfigurationBuilder AddDatabaseSettings(this IConfigurationBuilder builder, string? connectionString) =>
        string.IsNullOrWhiteSpace(connectionString) ? builder : builder.Add(new DatabaseSettingsSource(connectionString));
}

/// <summary>Saves settings changed in the app and reloads configuration so pages see them right away.</summary>
public class SettingsService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IConfiguration configuration)
{
    /// <summary>The value saved in the app for a key, or null when the key comes from appsettings.json (or its default).</summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetSavedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct);
    }

    /// <summary>Saves values by configuration key. A null value removes the saved value, going back to appsettings.json.</summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = values.Keys.ToList();
        var existing = await db.AppSettings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
        foreach (var (key, value) in values)
        {
            if (value is null)
            {
                if (existing.TryGetValue(key, out var old))
                {
                    db.AppSettings.Remove(old);
                }
            }
            else if (existing.TryGetValue(key, out var setting))
            {
                setting.Value = value;
            }
            else
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            }
        }
        await db.SaveChangesAsync(ct);
        (configuration as IConfigurationRoot)?.Reload();
    }
}
