using System.Globalization;

namespace PropertyManager.Core.Import;

public static class ValueParsers
{
    private static readonly string[] DateFormats =
        ["M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "MM/dd/yy", "yyyy-MM-dd", "yyyy/MM/dd", "M-d-yyyy", "MMM d, yyyy", "d-MMM-yyyy"];

    public static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Some banks append a time ("10/14/2026 00:00:00"); only the date matters.
        var text = value.Trim().Split(' ', 2)[0];
        return DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Parses bank amounts such as "-1,234.56", "$1,234.56", "(45.00)", or "45.00 CR". Blank parses as null.</summary>
    public static bool TryParseAmount(string? value, out decimal? amount)
    {
        amount = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        var negative = false;
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            negative = true;
            text = text[1..^1];
        }
        if (text.EndsWith(" DR", StringComparison.OrdinalIgnoreCase))
        {
            negative = true;
            text = text[..^3];
        }
        else if (text.EndsWith(" CR", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^3];
        }

        text = text.Replace("$", "").Replace(",", "").Trim();
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        amount = negative ? -Math.Abs(parsed) : parsed;
        return true;
    }
}
