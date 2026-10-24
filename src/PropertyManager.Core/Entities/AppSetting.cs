namespace PropertyManager.Core.Entities;

/// <summary>
/// A setting changed in the app (Settings page or first-run setup), stored by its configuration key, e.g.
/// "Business:Name" or "Features:Email". It overrides appsettings.json; deleting it goes back to the file's value.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}
