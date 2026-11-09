using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Calendar;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;

namespace PropertyManager.Web.Agents;

/// <summary>
/// Every few hours (Sync:HoursBetweenSyncs) gets new emails from the chosen folders and refreshes appointments from the
/// chosen calendars, then asks the <see cref="ExpenseAgent"/> to look at any new emails. The Email &amp; calendar page's
/// own buttons go through <see cref="RunExclusiveAsync"/> so a manual sync and a background one never overlap.
/// </summary>
public sealed class MailSyncAgent(
    IServiceScopeFactory scopes, IOptionsMonitor<FeatureOptions> features, IOptionsMonitor<SyncOptions> schedule, ExpenseAgent expenses,
    ILogger<MailSyncAgent> log, TimeProvider clock) : ScheduledAgent(clock, log)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public override TimeSpan Interval => TimeSpan.FromHours(Math.Clamp(schedule.CurrentValue.HoursBetweenSyncs, 1, 24));

    public void SyncNow() => Wake(full: true);

    /// <summary>Runs a sync started from the page, waiting for a background one to finish first.</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> sync, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await sync();
        }
        finally
        {
            gate.Release();
        }
    }

    protected override async Task<string?> WorkAsync(bool full, CancellationToken ct)
    {
        var on = features.CurrentValue;
        if (!on.Email && !on.Calendar)
        {
            return null;
        }
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var signIn = services.GetRequiredService<EmailSignIn>();
        if (!signIn.IsConfigured || await signIn.GetSignedInAddressAsync() is null)
        {
            return "Not signed in to Microsoft.";
        }
        await using var db = await services.GetRequiredService<IDbContextFactory<PropertyManagerDbContext>>().CreateDbContextAsync(ct);
        var notes = new List<string>();

        if (on.Email && await db.EmailFolders.AnyAsync(ct))
        {
            try
            {
                var mail = await RunExclusiveAsync(() => services.GetRequiredService<EmailSyncService>().SyncAsync(ct), ct);
                notes.Add($"{mail.Added} new email(s).");
                if (mail.Added > 0)
                {
                    expenses.CheckNow();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                log.LogWarning(ex, "Background email sync failed");
                notes.Add($"Couldn't get new emails: {ex.Message}");
            }
        }
        if (on.Calendar && await db.CalendarSources.AnyAsync(ct))
        {
            try
            {
                var calendar = await RunExclusiveAsync(() => services.GetRequiredService<CalendarSyncService>().SyncAsync(ct), ct);
                notes.Add($"{calendar.Added} new and {calendar.Updated} changed appointment(s).");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                log.LogWarning(ex, "Background calendar sync failed");
                notes.Add($"Couldn't refresh appointments: {ex.Message}");
            }
        }
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }
}
