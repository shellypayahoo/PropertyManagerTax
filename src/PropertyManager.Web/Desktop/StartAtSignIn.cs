namespace PropertyManager.Web.Desktop;

/// <summary>
/// Starts the installed app when the owner signs in to the computer, without opening the browser: on Windows through
/// the current user's Run key, on a Mac through a LaunchAgent in ~/Library/LaunchAgents.
/// </summary>
public static class StartAtSignIn
{
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValueName = "Property Manager";
    private const string MacLabel = "com.propertymanager.desktop";

    public static bool IsSupported => DesktopMode.IsOn && DesktopMode.ProgramPath is not null && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    private static string MacPlist =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", MacLabel + ".plist");

    public static bool IsOn
    {
        get
        {
            if (!IsSupported)
            {
                return false;
            }
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsRunKey);
                return key?.GetValue(WindowsValueName) is string;
            }
            return File.Exists(MacPlist);
        }
    }

    /// <summary>Turns starting at sign-in on or off. Takes effect at the next sign-in; the running app isn't affected.</summary>
    public static void Set(bool on)
    {
        if (!IsSupported)
        {
            return;
        }
        var program = DesktopMode.ProgramPath!;
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            if (on)
            {
                key.SetValue(WindowsValueName, $"\"{program}\" {DesktopMode.NoBrowserArgument}");
            }
            else
            {
                key.DeleteValue(WindowsValueName, throwOnMissingValue: false);
            }
            return;
        }
        if (!on)
        {
            File.Delete(MacPlist);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(MacPlist)!);
        File.WriteAllText(MacPlist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{MacLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{System.Security.SecurityElement.Escape(program)}</string>
                    <string>{DesktopMode.NoBrowserArgument}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """);
    }
}
