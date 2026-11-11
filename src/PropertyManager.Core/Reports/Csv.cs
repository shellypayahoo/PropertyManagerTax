using System.Globalization;
using System.Text;

namespace PropertyManager.Core.Reports;

/// <summary>Writes comma-separated files that open cleanly in Excel and Numbers.</summary>
public static class Csv
{
    public static string Line(params object?[] values) => string.Join(",", values.Select(Field));

    private static string Field(object? value)
    {
        var text = value switch
        {
            null => "",
            decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "Yes" : "No",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        // A leading =, +, -, or @ would be run as a formula by a spreadsheet; a quote in front makes it plain text.
        if (value is string && text.Length > 0 && text[0] is '=' or '+' or '-' or '@')
        {
            text = "'" + text;
        }
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    /// <summary>The file's bytes, with the marker Excel needs to read accents and symbols correctly.</summary>
    public static byte[] File(IEnumerable<string> lines) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n")];
}
