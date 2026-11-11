using PropertyManager.Core.Services;

namespace PropertyManager.Web.Agents;

/// <summary>
/// Makes the daily backup: looks every hour, and backs up when there's no backup for today yet (so soon after
/// midnight, or as soon as the app is running that day). <see cref="BackupNow"/> makes one right away.
/// </summary>
public sealed class BackupAgent(BackupService backups, ILogger<BackupAgent> log, TimeProvider clock) : ScheduledAgent(clock, log)
{
    public override TimeSpan Interval => TimeSpan.FromHours(1);

    public void BackupNow()
    {
        manual = true;
        Wake(full: true);
    }

    /// <summary>Whether the last attempt failed (the reason is in <see cref="ScheduledAgent.LastResult"/>).</summary>
    public bool LastFailed => LastResult?.StartsWith("Failed", StringComparison.Ordinal) == true;

    protected override async Task<string?> WorkAsync(bool full, CancellationToken ct)
    {
        // The hourly look only backs up once a day; "Back up now" always does.
        if (backups.Folder is null || (!IsManual(full) && backups.HasBackupForToday))
        {
            return null;
        }
        var result = await backups.RunAsync(ct);
        return Describe(result);
    }

    // A scheduled run and a manual one both arrive as "full"; a manual one was asked for through BackupNow.
    private bool manual;

    private bool IsManual(bool full)
    {
        var was = manual;
        manual = false;
        return full && was;
    }

    public static string Describe(BackupResult result) =>
        $"Backed up the database ({(result.DatabaseBytes < 1024 * 1024 ? $"{Math.Max(1, result.DatabaseBytes / 1024)} KB" : $"{result.DatabaseBytes / 1024.0 / 1024:0.#} MB")}) and {result.DocumentsTotal:N0} document(s), {result.DocumentsCopied:N0} of them new.";
}
