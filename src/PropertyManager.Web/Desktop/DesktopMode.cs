using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PropertyManager.Web.Desktop;

/// <summary>
/// How the app behaves when installed (the PC installer or the Mac app) rather than run from source. Installed copies
/// have a <c>desktop-app</c> marker file next to the program. Then the app keeps its data in the user's own app-data
/// folder, opens the browser when started, hands off to a copy that's already running, writes its log to a file,
/// and can start at sign-in and stay reachable from a phone (Settings → This computer).
/// </summary>
public static class DesktopMode
{
    public const int Port = 5220;
    public const string NoBrowserArgument = "--no-browser";
    private const string WaitArgument = "--wait-for-port";

    public static bool IsOn { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "desktop-app"));

    public static string LocalUrl => $"http://localhost:{Port}";

    /// <summary>
    /// Where the database, documents, and log live: %LOCALAPPDATA%\Property Manager on Windows, ~/Library/Application
    /// Support/Property Manager on a Mac. Not Documents, which OneDrive or iCloud may sync while the database is open.
    /// An earlier Windows package kept data in Documents\Property Manager; if that's there, it's still used.
    /// </summary>
    public static string DataFolder { get; } = FindDataFolder();

    private static string FindDataFolder()
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Property Manager");
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(legacy, "propertymanager.db")))
        {
            return legacy;
        }
        return Path.Combine(AppDataRoot, "Property Manager");
    }

    /// <summary>The user's app-data folder, created if it doesn't exist yet (a brand-new account may not have one).</summary>
    public static string AppDataRoot
    {
        get
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            return folder.Length > 0 ? folder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
    }

    /// <summary>Settings an installed copy starts from; the owner's own choices (Settings page) still override them.</summary>
    public static Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:PropertyManager"] = $"Data Source={Path.Combine(DataFolder, "propertymanager.db")}",
        ["DocumentStorage:LocalPath"] = Path.Combine(DataFolder, "documents"),
    };

    /// <summary>The program to run to start the app again (the installed executable).</summary>
    public static string? ProgramPath => Environment.ProcessPath;

    /// <summary>
    /// When a copy is already running, opens it in the browser and returns true, so this one can quit (double-clicking
    /// the icon again just shows the app). Started by Restart, waits for the old copy to finish closing instead.
    /// </summary>
    public static bool HandOffToRunningCopy(string[] args)
    {
        if (args.Contains(WaitArgument))
        {
            for (var i = 0; i < 60 && IsListening(); i++)
            {
                Thread.Sleep(500);
            }
            return false;
        }
        if (!IsListening())
        {
            return false;
        }
        if (!args.Contains(NoBrowserArgument))
        {
            OpenBrowser(LocalUrl);
        }
        return true;
    }

    private static bool IsListening()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", Port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser to open (e.g. a server without a desktop); the app still runs.
        }
    }

    /// <summary>Starts a new copy, which waits for this one to close, then closes this one. Used to apply settings that need a restart.</summary>
    public static void Restart(IHostApplicationLifetime lifetime)
    {
        if (ProgramPath is { } program)
        {
            Process.Start(new ProcessStartInfo(program, [WaitArgument, NoBrowserArgument]) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        lifetime.StopApplication();
    }

    // ---- keeping the computer awake while the app runs, so a phone can reach it ----

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Stops the computer going to sleep on its own while the app is running (the screen can still turn off).</summary>
    public static void KeepAwake()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                // caffeinate ends by itself when this process does.
                Process.Start("/usr/bin/caffeinate", ["-i", "-s", "-w", Environment.ProcessId.ToString()]);
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // The request lasts as long as the thread that made it, so keep one thread for it.
            new Thread(() =>
            {
                const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
                SetThreadExecutionState(Continuous | SystemRequired);
                Thread.Sleep(Timeout.Infinite);
            }) { IsBackground = true, Name = "Keep awake" }.Start();
        }
    }
}
