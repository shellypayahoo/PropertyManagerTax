using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;
using PropertyManager.Core.Messaging;

namespace PropertyManager.Web.Agents;

/// <summary>
/// Runs in the background while the app is running: every few hours it gets new emails and texts, then has the
/// <see cref="ExpenseFinder"/> look through new vendor messages for expenses to suggest. After an import on the Email or
/// Messages page, <see cref="CheckNow"/> runs the check right away.
/// </summary>
public sealed class ExpenseAgent(
    IServiceScopeFactory scopes, IOptionsMonitor<FeatureOptions> features, IConfiguration configuration, ILogger<ExpenseAgent> log,
    TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(3);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim wake = new(0);
    private volatile bool syncRequested;

    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastResult { get; private set; }
    public DateTimeOffset? NextRunAt { get; private set; }

    /// <summary>Raised when a check starts or finishes, so open pages can refresh.</summary>
    public event Action? Changed;

    /// <summary>Checks new vendor messages now; with <paramref name="importFirst"/>, gets new emails and texts first.</summary>
    public void CheckNow(bool importFirst = false)
    {
        syncRequested |= importFirst;
        wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = FirstRunDelay;
        var scheduled = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NextRunAt = clock.GetUtcNow() + next;
            try
            {
                // Woken early by CheckNow, or the interval passed.
                scheduled = !await wake.WaitAsync(next, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RunAsync(importFirst: scheduled || syncRequested, stoppingToken);
            syncRequested = false;
            next = Interval;
        }
    }

    private async Task RunAsync(bool importFirst, CancellationToken ct)
    {
        if (!features.CurrentValue.ExpenseSuggestions || !features.CurrentValue.Assistant)
        {
            return;
        }
        IsRunning = true;
        Changed?.Invoke();
        var notes = new List<string>();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            if (importFirst)
            {
                await ImportAsync(services, notes, ct);
            }
            var summary = await services.GetRequiredService<ExpenseFinder>().RunAsync(ct);
            notes.Add(summary.Problem is { } problem && summary.MessagesChecked == 0 && summary.VendorsFailed == 0 ? problem
                : $"Checked {summary.MessagesChecked} new vendor message(s), {summary.Suggested} suggested expense(s)."
                  + (summary.Problem is { } failure ? " " + failure : ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Expense check failed");
            notes.Add($"The check failed: {ex.Message}");
        }
        finally
        {
            LastRunAt = clock.GetUtcNow();
            LastResult = string.Join(" ", notes);
            IsRunning = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets new emails and texts the same way the Email and Messages pages do; problems are noted, not thrown.</summary>
    private async Task ImportAsync(IServiceProvider services, List<string> notes, CancellationToken ct)
    {
        var db = await services.GetRequiredService<IDbContextFactory<PropertyManagerDbContext>>().CreateDbContextAsync(ct);
        await using (db)
        {
            var on = features.CurrentValue;
            var signIn = services.GetRequiredService<EmailSignIn>();
            if (on.Email && signIn.IsConfigured && await db.EmailFolders.AnyAsync(ct) && await signIn.GetSignedInAddressAsync() is not null)
            {
                try
                {
                    await services.GetRequiredService<EmailSyncService>().SyncAsync(ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    log.LogWarning(ex, "Background email sync failed");
                    notes.Add($"Couldn't get new emails: {ex.Message}");
                }
            }
            if (on.TextMessages && OperatingSystem.IsMacOS() && await db.MessageContacts.AnyAsync(c => c.Included, ct))
            {
                try
                {
                    var path = configuration["Messages:DatabasePath"] is { Length: > 0 } p ? p : MessagesDatabaseReader.DefaultPath;
                    await services.GetRequiredService<TextMessageImportService>().ImportAsync(path, ct: ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    log.LogWarning(ex, "Background text import failed");
                    notes.Add(ex is UnauthorizedAccessException
                        ? "Couldn't read your texts: give the app Full Disk Access (see the README)."
                        : $"Couldn't import texts: {ex.Message}");
                }
            }
        }
    }
}
