namespace PropertyManager.Core.Configuration;

/// <summary>
/// "25 min ago" style wording for when something happened (a sync, a check), which reads the same whatever time zone
/// the owner is in, unlike a clock time in the business's zone.
/// </summary>
public static class RelativeTime
{
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        return span switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min ago",
            { TotalHours: < 24 } => (int)span.TotalHours == 1 ? "1 hour ago" : $"{(int)span.TotalHours} hours ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 30 } => $"{(int)span.TotalDays} days ago",
            _ => $"on {at.ToLocalTime():MMM d, yyyy}",
        };
    }

    public static string FromNow(DateTimeOffset at, DateTimeOffset now)
    {
        var span = at - now;
        return span switch
        {
            { TotalMinutes: < 1 } => "in a moment",
            { TotalMinutes: < 60 } => $"in {Math.Max(1, (int)Math.Round(span.TotalMinutes))} min",
            _ => (int)Math.Round(span.TotalHours) == 1 ? "in 1 hour" : $"in {(int)Math.Round(span.TotalHours)} hours",
        };
    }

    /// <summary>The exact time in the business's zone with its label, e.g. "Nov 9, 2:15 PM ET", for hover text.</summary>
    public static string Exact(DateTimeOffset at) =>
        $"{at.ToLocalTime():MMM d, h:mm tt} {BusinessTimeZone.Abbreviation(TimeZoneInfo.Local, at)}";
}
