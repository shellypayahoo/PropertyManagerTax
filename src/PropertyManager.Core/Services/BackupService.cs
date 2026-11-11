using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Services;

public record BackupStatus(string? Folder, DateTimeOffset? LastBackupAt, int DatabaseCopies, int Documents)
{
    public bool IsOn => Folder is not null;
}

public record BackupResult(string DatabaseCopy, long DatabaseBytes, int DocumentsCopied, int DocumentsTotal);

/// <summary>
/// Copies everything the app keeps to a backup folder (ideally one that syncs to iCloud Drive or OneDrive):
/// <list type="bullet">
/// <item><c>database/propertymanager-yyyy-MM-dd.zip</c>: a consistent copy of the database for each day, kept for
/// <see cref="BackupOptions.KeepDays"/> days.</item>
/// <item><c>documents/</c>: every stored file (receipts, leases, attachments). Only new files are copied each time.</item>
/// <item><c>documents-removed/yyyy-MM-dd/</c>: files deleted in the app since the last backup, kept the same number of days.</item>
/// </list>
/// API keys and sign-ins aren't in the app's data, so they aren't backed up.
/// </summary>
public class BackupService(
    IDbContextFactory<PropertyManagerDbContext> dbFactory, IDocumentStorage storage, IOptionsMonitor<BackupOptions> options, TimeProvider clock)
{
    private const string DatabasePrefix = "propertymanager-";
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The backup folder with "~" expanded, or null when backups are off.</summary>
    public string? Folder => Expand(options.CurrentValue.Folder);

    public static string? Expand(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }
        var path = folder.Trim();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/'));
        }
        return Path.GetFullPath(path);
    }

    /// <summary>Folders on this computer that sync to the cloud, as suggestions for where to keep backups.</summary>
    public static IReadOnlyList<(string Label, string Path)> SuggestedFolders()
    {
        const string name = "Property Manager Backups";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<(string, string)>();
        void Add(string label, string? root)
        {
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root) && found.All(f => f.Item1 != label))
            {
                found.Add((label, Path.Combine(root, name)));
            }
        }
        Add("iCloud Drive", Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs"));
        var cloudStorage = Path.Combine(home, "Library", "CloudStorage");
        if (Directory.Exists(cloudStorage))
        {
            foreach (var folder in Directory.EnumerateDirectories(cloudStorage).Order())
            {
                var provider = Path.GetFileName(folder).Split('-')[0];
                Add(provider is "OneDrive" or "Dropbox" or "GoogleDrive" ? provider.Replace("GoogleDrive", "Google Drive") : Path.GetFileName(folder), folder);
            }
        }
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Add("OneDrive", Path.Combine(home, "OneDrive"));
        Add("Dropbox", Path.Combine(home, "Dropbox"));
        return found;
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private static string DatabaseFolder(string folder) => Path.Combine(folder, "database");

    private static string CopyFor(string folder, DateOnly day) =>
        Path.Combine(DatabaseFolder(folder), $"{DatabasePrefix}{day:yyyy-MM-dd}.zip");

    /// <summary>The days there are database copies for, newest first.</summary>
    private static List<(DateOnly Day, string Path)> Copies(string folder) =>
        !Directory.Exists(DatabaseFolder(folder)) ? []
        : Directory.EnumerateFiles(DatabaseFolder(folder), $"{DatabasePrefix}*.zip")
            .Select(p => (Ok: DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(p)[DatabasePrefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day), Day: day, Path: p))
            .Where(c => c.Ok)
            .Select(c => (c.Day, c.Path))
            .OrderByDescending(c => c.Day)
            .ToList();

    public bool HasBackupForToday => Folder is { } folder && File.Exists(CopyFor(folder, Today));

    public BackupStatus Status()
    {
        if (Folder is not { } folder)
        {
            return new BackupStatus(null, null, 0, 0);
        }
        var copies = Copies(folder);
        var documents = Path.Combine(folder, "documents");
        return new BackupStatus(
            folder,
            copies.Count == 0 ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(copies[0].Path), TimeSpan.Zero),
            copies.Count,
            Directory.Exists(documents) ? Directory.EnumerateFiles(documents).Count() : 0);
    }

    /// <summary>Checks the folder can be used. Returns what's wrong, or null.</summary>
    public string? Problem(string? folder)
    {
        if (Expand(folder) is not { } path)
        {
            return null;
        }
        if (storage is LocalDocumentStorage local && (Inside(path, local.RootPath) || Inside(local.RootPath, path)))
        {
            return "Choose a folder outside the app's own documents folder.";
        }
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"The app can't write to {path}: {ex.Message}";
        }
    }

    private static bool Inside(string path, string parent) =>
        (path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes today's backup (replacing an earlier one from today) and tidies up old ones.</summary>
    public async Task<BackupResult> RunAsync(CancellationToken ct = default)
    {
        var folder = Folder ?? throw new InvalidOperationException("No backup folder is set. Choose one in Settings.");
        if (Problem(folder) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        await gate.WaitAsync(ct);
        try
        {
            var (copy, bytes) = await CopyDatabaseAsync(folder, ct);
            var (copied, total) = storage is LocalDocumentStorage local ? MirrorDocuments(local.RootPath, folder, ct) : (0, 0);
            Prune(folder);
            return new BackupResult(copy, bytes, copied, total);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A consistent copy of the live database (SQLite's online backup), zipped.</summary>
    private async Task<(string Path, long Bytes)> CopyDatabaseAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(DatabaseFolder(folder));
        var snapshot = Path.Combine(Path.GetTempPath(), $"pm-backup-{Guid.NewGuid():N}.db");
        var target = CopyFor(folder, Today);
        var partial = target + ".partial";
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var source = (SqliteConnection)db.Database.GetDbConnection();
                if (source.State != System.Data.ConnectionState.Open)
                {
                    await source.OpenAsync(ct);
                }
                await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString());
                await destination.OpenAsync(ct);
                source.BackupDatabase(destination);
            }
            await using (var zip = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(snapshot, "propertymanager.db", CompressionLevel.Optimal);
            }
            // Only a finished copy ever has the real name.
            File.Move(partial, target, overwrite: true);
            return (target, new FileInfo(target).Length);
        }
        finally
        {
            File.Delete(snapshot);
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Copies files that aren't in the backup yet. Files no longer in the app are moved to documents-removed/&lt;today&gt;
    /// rather than deleted, so a mistaken delete can still be recovered for a while.
    /// </summary>
    private (int Copied, int Total) MirrorDocuments(string source, string folder, CancellationToken ct)
    {
        var mirror = Path.Combine(folder, "documents");
        Directory.CreateDirectory(mirror);
        var names = Directory.EnumerateFiles(source).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var copied = 0;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var from = Path.Combine(source, name);
            var to = Path.Combine(mirror, name);
            if (!File.Exists(to) || new FileInfo(to).Length != new FileInfo(from).Length)
            {
                File.Copy(from, to, overwrite: true);
                copied++;
            }
        }
        var removed = Directory.EnumerateFiles(mirror).Where(p => !names.Contains(Path.GetFileName(p))).ToList();
        if (removed.Count > 0)
        {
            var bin = Path.Combine(folder, "documents-removed", Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(bin);
            foreach (var path in removed)
            {
                File.Move(path, Path.Combine(bin, Path.GetFileName(path)), overwrite: true);
            }
        }
        return (copied, names.Count);
    }

    /// <summary>Removes database copies and removed-file folders older than the keep period (the newest three copies always stay).</summary>
    private void Prune(string folder)
    {
        var oldest = Today.AddDays(-Math.Max(1, options.CurrentValue.KeepDays));
        foreach (var (day, path) in Copies(folder).Skip(3).Where(c => c.Day < oldest))
        {
            File.Delete(path);
        }
        var removed = Path.Combine(folder, "documents-removed");
        if (Directory.Exists(removed))
        {
            foreach (var bin in Directory.EnumerateDirectories(removed))
            {
                if (DateOnly.TryParseExact(Path.GetFileName(bin), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
                {
                    Directory.Delete(bin, recursive: true);
                }
            }
        }
    }
}
