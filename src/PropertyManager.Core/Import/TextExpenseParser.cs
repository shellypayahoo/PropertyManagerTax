using System.Globalization;
using System.Text.RegularExpressions;

namespace PropertyManager.Core.Import;

public record TextExpenseLine(decimal Amount, string Description);

public record TextExpenseResult(DateOnly? Date, IReadOnlyList<TextExpenseLine> Lines);

/// <summary>
/// Pulls dollar amounts and what they were for out of a vendor's text message, e.g.
/// "it's $180 for the lawn this week and $40 for the hedges".
/// </summary>
public static partial class TextExpenseParser
{
    [GeneratedRegex(@"\$\s?(?<amount>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<cents>\d{1,2}))?|(?<amount>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<cents>\d{2}))?\s?(?:dollars|bucks)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"\b(?<m>1[0-2]|0?[1-9])/(?<d>3[01]|[12]\d|0?[1-9])(?:/(?<y>\d{2}|\d{4}))?\b")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\b(?:total|altogether|all together|in all)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TotalPattern();

    public static TextExpenseResult Parse(string text, DateOnly today)
    {
        var matches = AmountPattern().Matches(text);
        var found = new List<(decimal Amount, string Description, bool LooksLikeTotal)>();

        // Texts either name the item before the amount ("Mowing $150, hedges $40") or after it
        // ("$180 for the lawn and $40 for the hedges"); decide from the first amount.
        var nameBeforeAmount = matches.Count > 0 && Before(text, matches, 0).Length > 0;

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var amount = decimal.Parse(m.Groups["amount"].Value.Replace(",", ""), CultureInfo.InvariantCulture);
            if (m.Groups["cents"].Success)
            {
                amount += decimal.Parse("0." + m.Groups["cents"].Value.PadRight(2, '0'), CultureInfo.InvariantCulture);
            }

            var (first, second) = nameBeforeAmount ? (Before(text, matches, i), After(text, matches, i)) : (After(text, matches, i), Before(text, matches, i));
            var description = first.Length > 0 ? first : second.Length > 0 ? second : Sentence(text, m);

            var context = text[Math.Max(0, m.Index - 25)..m.Index];
            found.Add((amount, Capitalize(description), TotalPattern().IsMatch(context)));
        }

        // Drop a "total" that just sums the other amounts.
        var items = found.Where(f => !f.LooksLikeTotal).ToList();
        var lines = found.Count > 1 && found.Any(f => f.LooksLikeTotal && f.Amount == items.Sum(p => p.Amount))
            ? items
            : found;

        return new TextExpenseResult(ParseDate(text, today), lines.Select(l => new TextExpenseLine(l.Amount, l.Description)).ToList());
    }

    private static readonly char[] ClauseSeparators = ['.', '!', '?', '\n', ';', ','];

    /// <summary>Words between the previous amount (or a clause break) and this amount.</summary>
    private static string Before(string text, MatchCollection matches, int i)
    {
        var start = i > 0 ? matches[i - 1].Index + matches[i - 1].Length : 0;
        var segment = text[start..matches[i].Index];
        return TrimFiller(DatePattern().Replace(segment[(segment.LastIndexOfAny(ClauseSeparators) + 1)..], " "));
    }

    /// <summary>Words after this amount, up to the next amount or a clause break.</summary>
    private static string After(string text, MatchCollection matches, int i)
    {
        var start = matches[i].Index + matches[i].Length;
        var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
        var segment = text[start..end];
        var cut = segment.IndexOfAny(ClauseSeparators);
        return TrimFiller(DatePattern().Replace(cut >= 0 ? segment[..cut] : segment, " "));
    }

    /// <summary>Fallback: the whole sentence around the amount, minus the amount and any date.</summary>
    private static string Sentence(string text, Match amount)
    {
        var start = text.LastIndexOfAny(['.', '!', '?', '\n'], Math.Max(0, amount.Index - 1)) + 1;
        var end = text.IndexOfAny(['.', '!', '?', '\n'], amount.Index + amount.Length);
        var sentence = text[start..(end < 0 ? text.Length : end)].Replace(amount.Value, " ");
        return TrimFiller(DatePattern().Replace(sentence, " ").Replace(",", " "));
    }

    private static string TrimFiller(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && IsFiller(words[0]))
        {
            words.RemoveAt(0);
        }
        while (words.Count > 0 && IsFiller(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }
        return string.Join(' ', words);
    }

    private static string Capitalize(string text) =>
        text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : "";

    private static DateOnly? ParseDate(string text, DateOnly today)
    {
        var m = DatePattern().Match(text);
        if (!m.Success)
        {
            return null;
        }

        var month = int.Parse(m.Groups["m"].Value);
        var day = int.Parse(m.Groups["d"].Value);
        var year = m.Groups["y"].Success
            ? int.Parse(m.Groups["y"].Value) is var y && y < 100 ? 2000 + y : y
            : today.Year;
        if (day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var date = new DateOnly(year, month, day);
        // "12/28" texted in January means last year.
        return !m.Groups["y"].Success && date > today.AddDays(31) ? date.AddYears(-1) : date;
    }

    private static bool IsFiller(string word) =>
        word.Trim(':', '-').ToLowerInvariant() is "hey" or "hi" or "hello" or "so" or "ok" or "okay" or "it's" or "its" or "it" or "that's"
            or "thats" or "is" or "was" or "be" or "will" or "comes" or "came" or "to" or "for" or "of" or "about" or "total"
            or "the" or "a" or "an" or "on" or "and" or "plus" or "&" or "";
}
