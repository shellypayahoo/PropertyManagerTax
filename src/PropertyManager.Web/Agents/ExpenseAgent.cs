using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Messaging;

namespace PropertyManager.Web.Agents;

/// <summary>
/// Every few hours (ExpenseCheck:HoursBetweenChecks) imports new texts, then has the <see cref="ExpenseFinder"/> look
/// through new vendor texts and emails for expenses to suggest. New emails come from <see cref="MailSyncAgent"/>, which
/// calls <see cref="CheckNow"/> when they arrive, as do imports on the Email and Messages pages.
/// </summary>
public sealed class ExpenseAgent(
    IServiceScopeFactory scopes, IOptionsMonitor<FeatureOptions> features, IOptionsMonitor<ExpenseCheckOptions> schedule,
    IConfiguration configuration, ILogger<ExpenseAgent> log, TimeProvider clock) : ScheduledAgent(clock, log)
{
    public override TimeSpan Interval => TimeSpan.FromHours(Math.Clamp(schedule.CurrentValue.HoursBetweenChecks, 1, 24));

    /// <summary>Checks new vendor messages now; with <paramref name="importFirst"/>, imports new texts first.</summary>
    public void CheckNow(bool importFirst = false) => Wake(importFirst);

    protected override async Task<string?> WorkAsync(bool full, CancellationToken ct)
    {
        if (!features.CurrentValue.ExpenseSuggestions || !features.CurrentValue.Assistant)
        {
            return null;
        }
        var notes = new List<string>();
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (full)
        {
            await ImportTextsAsync(services, notes, ct);
        }
        var summary = await services.GetRequiredService<ExpenseFinder>().RunAsync(ct);
        notes.Add(summary.Problem is { } problem && summary.MessagesChecked == 0 && summary.VendorsFailed == 0 ? problem
            : $"Checked {summary.MessagesChecked} new vendor message(s), {summary.Suggested} suggested expense(s)."
              + (summary.Problem is { } failure ? " " + failure : ""));
        return string.Join(" ", notes);
    }

    /// <summary>Imports new texts the same way the Messages page does; problems are noted, not thrown.</summary>
    private async Task ImportTextsAsync(IServiceProvider services, List<string> notes, CancellationToken ct)
    {
        if (!features.CurrentValue.TextMessages || !OperatingSystem.IsMacOS())
        {
            return;
        }
        await using var db = await services.GetRequiredService<IDbContextFactory<PropertyManagerDbContext>>().CreateDbContextAsync(ct);
        if (!await db.MessageContacts.AnyAsync(c => c.Included, ct))
        {
            return;
        }
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
