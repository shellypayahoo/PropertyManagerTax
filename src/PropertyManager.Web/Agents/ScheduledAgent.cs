namespace PropertyManager.Web.Agents;

/// <summary>
/// A job that runs in the background every few hours while the app is running, and right away when asked. The interval
/// comes from Settings and is looked at again every minute, so a change applies without a restart.
/// </summary>
public abstract class ScheduledAgent(TimeProvider clock, ILogger log) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim wake = new(0);
    private DateTimeOffset started;
    private volatile bool fullRunRequested;

    /// <summary>Time between runs, from Settings.</summary>
    public abstract TimeSpan Interval { get; }

    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastResult { get; private set; }

    /// <summary>When the next run is due, from the last run and the current setting.</summary>
    public DateTimeOffset NextRunAt => LastRunAt is { } last ? last + Interval : started + FirstRunDelay;

    /// <summary>Raised when a run starts or finishes, so open pages can refresh.</summary>
    public event Action? Changed;

    /// <summary>Runs now. A scheduled run is always "full"; <paramref name="full"/> asks for the same on demand.</summary>
    protected void Wake(bool full)
    {
        fullRunRequested |= full;
        wake.Release();
    }

    /// <summary>Does the work and returns a short description of what happened, or null when there was nothing to do.</summary>
    protected abstract Task<string?> WorkAsync(bool full, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        started = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            bool woken;
            try
            {
                do
                {
                    var wait = NextRunAt - clock.GetUtcNow();
                    woken = wait > TimeSpan.Zero && await wake.WaitAsync(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), stoppingToken);
                }
                while (!woken && clock.GetUtcNow() < NextRunAt);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var full = !woken || fullRunRequested;
            fullRunRequested = false;
            IsRunning = true;
            Changed?.Invoke();
            try
            {
                LastResult = await WorkAsync(full, stoppingToken) ?? LastResult;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "{Agent} failed", GetType().Name);
                LastResult = $"Failed: {ex.Message}";
            }
            finally
            {
                LastRunAt = clock.GetUtcNow();
                IsRunning = false;
                Changed?.Invoke();
            }
        }
    }
}
