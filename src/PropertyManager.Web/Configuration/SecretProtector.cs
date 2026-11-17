using Microsoft.AspNetCore.DataProtection;

namespace PropertyManager.Web.Configuration;

/// <summary>
/// Encrypts secrets (API keys) typed into the app before they're saved in the database. The encryption key stays on
/// this computer, outside the database and the backup folder, so a backup in iCloud or OneDrive never holds a usable
/// API key. On a new computer, or after restoring a backup, the keys simply have to be entered again.
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "enc:v1:";
    private readonly IDataProtector protector;

    public SecretProtector(string? keysFolder = null)
    {
        var folder = new DirectoryInfo(keysFolder ?? DefaultKeysFolder);
        var provider = DataProtectionProvider.Create(folder, options =>
        {
            options.SetApplicationName("PropertyManager");
            if (OperatingSystem.IsWindows())
            {
                // Windows also locks the key file to this user account.
                options.ProtectKeysWithDpapi();
            }
        });
        protector = provider.CreateProtector("AppSettings.Secrets");
    }

    /// <summary>The app's own folder for this user: ~/Library/Application Support on a Mac, %LOCALAPPDATA% on Windows.</summary>
    public static string DefaultKeysFolder => Path.Combine(Desktop.DesktopMode.AppDataRoot, "PropertyManager", "keys");

    /// <summary>Whether a setting holds a secret, by its name: anything ending in ApiKey.</summary>
    public static bool IsSecret(string settingKey) => settingKey.EndsWith(":ApiKey", StringComparison.OrdinalIgnoreCase);

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public string Protect(string value) => Prefix + protector.Protect(value);

    /// <summary>The original value, or null when it can't be read on this computer (restored backup, different user).</summary>
    public string? Unprotect(string value)
    {
        if (!IsProtected(value))
        {
            return value;
        }
        try
        {
            return protector.Unprotect(value[Prefix.Length..]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
