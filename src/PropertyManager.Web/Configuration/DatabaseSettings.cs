using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Web.Configuration;

/// <summary>
/// Adds settings saved in the app (the AppSettings table) on top of appsettings.json, user secrets, and environment
/// variables, so what an owner sets on the Settings page wins. Before the database exists there are none.
/// </summary>
public sealed class DatabaseSettingsProvider(string connectionString, SecretProtector secrets) : ConfigurationProvider
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
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (SecretProtector.IsProtected(value))
                {
                    // An API key saved in the app. One that can't be read here (a restored backup) counts as not set.
                    if (secrets.Unprotect(value!) is not { } secret)
                    {
                        continue;
                    }
                    value = secret;
                }
                data[reader.GetString(0)] = value;
            }
        }
        catch (SqliteException)
        {
            // No database or table yet (first start, before migrations): nothing saved in the app.
        }
        Data = data;
    }
}

/// <summary>Plugs the settings saved on the Settings page (the AppSettings table) into the app's configuration.</summary>
public sealed class DatabaseSettingsSource(string connectionString, SecretProtector secrets) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DatabaseSettingsProvider(connectionString, secrets);
}

/// <summary>Adds the database settings as the last (highest-priority) configuration source.</summary>
public static class DatabaseSettingsExtensions
{
    public static IConfigurationBuilder AddDatabaseSettings(this IConfigurationBuilder builder, string? connectionString, SecretProtector secrets) =>
        string.IsNullOrWhiteSpace(connectionString) ? builder : builder.Add(new DatabaseSettingsSource(connectionString, secrets));
}

/// <summary>Saves settings changed in the app and reloads configuration so pages see them right away.</summary>
public class SettingsService(IDbContextFactory<PropertyManagerDbContext> dbFactory, IConfiguration configuration, SecretProtector secrets)
{
    /// <summary>The value saved in the app for a key, or null when the key comes from appsettings.json (or its default).</summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetSavedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct);
    }

    /// <summary>
    /// Saves values by configuration key. A null value removes the saved value, going back to appsettings.json.
    /// Secrets (keys ending in ApiKey) are encrypted before they're stored.
    /// </summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = values.Keys.ToList();
        var existing = await db.AppSettings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
        foreach (var (key, plain) in values)
        {
            var value = plain is not null && SecretProtector.IsSecret(key) ? secrets.Protect(plain) : plain;
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
