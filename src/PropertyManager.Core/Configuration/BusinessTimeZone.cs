namespace PropertyManager.Core.Configuration;

/// <summary>
/// The business's time zone (Business:TimeZone), used for "today", for times shown in the app, and as the starting choice
/// for new appointments. It can differ from the computer's, e.g. rentals in DC managed from a Mac set to Mountain time.
/// </summary>
public static class BusinessTimeZone
{
    /// <summary>US time zones offered in the app, by IANA id.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Common =
    [
        ("America/New_York", "Eastern"),
        ("America/Chicago", "Central"),
        ("America/Denver", "Mountain"),
        ("America/Phoenix", "Arizona"),
        ("America/Los_Angeles", "Pacific"),
        ("America/Anchorage", "Alaska"),
        ("Pacific/Honolulu", "Hawaii"),
    ];

    /// <summary>Finds a time zone by IANA id, Windows name, or a plain name such as "Eastern", "MT", or "mountain time".</summary>
    public static TimeZoneInfo? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var t = text.Trim();
        var plain = t.ToLowerInvariant().Replace(" time", "").Replace(" standard", "").Replace(" daylight", "").Trim();
        var id = plain switch
        {
            "eastern" or "et" or "est" or "edt" or "east coast" or "dc" => "America/New_York",
            "central" or "ct" or "cst" or "cdt" => "America/Chicago",
            "mountain" or "mt" or "mst" or "mdt" => "America/Denver",
            "arizona" => "America/Phoenix",
            "pacific" or "pt" or "pst" or "pdt" or "west coast" => "America/Los_Angeles",
            "alaska" => "America/Anchorage",
            "hawaii" => "Pacific/Honolulu",
            "utc" or "gmt" => "UTC",
            _ => t,
        };
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>The IANA id of a zone (macOS and Linux use these; Windows names are converted), for storing.</summary>
    public static string IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

    /// <summary>
    /// Makes the business time zone the app's local time zone, so every date and time follows it. Returns a problem to
    /// report, or null. Works on macOS and Linux; Windows always uses the computer's time zone.
    /// </summary>
    public static string? Apply(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }
        if (Find(setting) is not { } zone)
        {
            return $"Business:TimeZone \"{setting}\" isn't a time zone; use one like America/New_York. Using the computer's time zone.";
        }
        if (OperatingSystem.IsWindows())
        {
            return TimeZoneInfo.Local.Id == zone.Id || IanaId(TimeZoneInfo.Local) == IanaId(zone)
                ? null
                : $"Business:TimeZone is {IanaId(zone)}, but on Windows the app uses the computer's time zone ({TimeZoneInfo.Local.DisplayName}). Change it in Windows settings.";
        }
        Environment.SetEnvironmentVariable("TZ", IanaId(zone));
        TimeZoneInfo.ClearCachedData();
        return null;
    }

    /// <summary>A short label for a zone at a moment: ET, CT, MT, PT, and so on, or the UTC offset for others.</summary>
    public static string Abbreviation(TimeZoneInfo zone, DateTimeOffset at)
    {
        var name = Common.FirstOrDefault(c => c.Id == IanaId(zone)).Name;
        if (name is "Eastern" or "Central" or "Mountain" or "Pacific")
        {
            return $"{name[0]}T";
        }
        if (name is not null)
        {
            return name == "Arizona" ? "MST" : name == "Alaska" ? "AKT" : "HT";
        }
        var offset = zone.GetUtcOffset(at);
        return offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration():h\\:mm}".Replace(":00", "");
    }
}
