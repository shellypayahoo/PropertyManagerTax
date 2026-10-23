using System.Text.RegularExpressions;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Calendar;

/// <summary>A calendar appointment that may be the visit for a maintenance request, with why it was suggested.</summary>
public record ScheduleSuggestion(CalendarEvent Event, int Score, IReadOnlyList<string> Reasons);

/// <summary>Works out which property a calendar appointment is about, and which appointments fit a maintenance request.</summary>
public static partial class EventMatcher
{
    /// <summary>The property an appointment mentions by street number or street name, if exactly one matches.</summary>
    public static Property? PropertyFor(CalendarEvent ev, IEnumerable<Property> properties)
    {
        var matches = properties.Where(p => MentionsProperty(ev.Text, p)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// True when the text has the property's street number ("1354") or street name ("Taylor", "1st St").
    /// A short name like "1st" alone is too common, so it must come with the street type.
    /// </summary>
    public static bool MentionsProperty(string text, Property property)
    {
        var street = StreetParts().Match(property.Street);
        if (!street.Success)
        {
            return HasWord(text, property.Name);
        }
        var (number, name, type) = (street.Groups["number"].Value, street.Groups["name"].Value, street.Groups["type"].Value);
        return HasWord(text, number)
            || (type.Length > 0 && HasWord(text, $"{name} {type}"))
            || (name.Length >= 4 && !name.Any(char.IsDigit) && HasWord(text, name));
    }

    /// <summary>
    /// Appointments that look like the visit for a request: from a few days before it was reported to four months
    /// after, mentioning its property or vendor, and ideally words from its title. Best first.
    /// </summary>
    public static IReadOnlyList<ScheduleSuggestion> Suggest(MaintenanceRequest request, IEnumerable<CalendarEvent> events, int max = 3)
    {
        var earliest = request.ReportedOn.AddDays(-3);
        var latest = request.ReportedOn.AddDays(120);
        var titleWords = Words().Matches(request.Title).Select(m => m.Value).Where(w => w.Length >= 4 && !CommonWords.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var suggestions = new List<ScheduleSuggestion>();
        foreach (var ev in events.Where(e => e.StartDate >= earliest && e.StartDate <= latest))
        {
            var reasons = new List<string>();
            var score = 0;
            var property = request.Unit?.Property;
            if (property is not null && MentionsProperty(ev.Text, property))
            {
                score += 2;
                reasons.Add(property.Name);
            }
            if (request.Vendor is { } vendor && VendorNamed(ev.Text, vendor))
            {
                score += 2;
                reasons.Add(vendor.Name);
            }
            if (score == 0)
            {
                continue;
            }
            if (request.Unit is { } unit && HasWord(ev.Text, $"unit {unit.Label}"))
            {
                score++;
                reasons.Add($"Unit {unit.Label}");
            }
            var shared = titleWords.Where(w => HasWord(ev.Text, w)).Take(2).ToList();
            score += shared.Count;
            reasons.AddRange(shared.Select(w => $"\"{w}\""));
            if (score >= 3)
            {
                suggestions.Add(new ScheduleSuggestion(ev, score, reasons));
            }
        }
        return suggestions
            .OrderByDescending(s => s.Score)
            .ThenBy(s => Math.Abs(s.Event.StartDate.DayNumber - request.ReportedOn.DayNumber))
            .Take(max)
            .ToList();
    }

    /// <summary>The vendor's full name, or the first word of it when that's distinctive (e.g. "Ace" for "Ace Plumbing").</summary>
    private static bool VendorNamed(string text, Vendor vendor)
    {
        var first = vendor.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return HasWord(text, vendor.Name) || (first.Length >= 3 && !CommonWords.Contains(first) && HasWord(text, first));
    }

    private static bool HasWord(string text, string phrase) =>
        phrase.Length > 0 && Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape(phrase)}(?![\w])", RegexOptions.IgnoreCase);

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "from", "unit", "repair", "repairs", "fix", "check", "service", "issue", "needs", "need", "broken", "new", "replace",
    };

    [GeneratedRegex(@"^\s*(?<number>\d+)\s+(?<name>.+?)(?:\s+(?<type>St|Street|Ave|Avenue|Pl|Place|Rd|Road|Dr|Drive|Ct|Court|Ter|Terrace|Blvd|Way|Ln|Lane))?\.?(?:\s+(?:NW|NE|SW|SE))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StreetParts();

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex Words();
}
