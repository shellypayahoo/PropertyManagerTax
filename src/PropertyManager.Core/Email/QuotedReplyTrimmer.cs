using System.Text.RegularExpressions;

namespace PropertyManager.Core.Email;

/// <summary>
/// Removes the earlier conversation that email programs quote below a reply, so each email's text is only what was
/// newly written. The full body is still kept separately.
/// </summary>
public static partial class QuotedReplyTrimmer
{
    public static string Trim(string body)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n');
        var kept = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (StartsQuote(lines, i))
            {
                break;
            }
            if (!line.TrimStart().StartsWith('>'))
            {
                kept.Add(line.TrimEnd());
            }
        }
        var text = string.Join('\n', kept).Trim();
        return text.Length > 0 ? text : body.Trim();
    }

    private static bool StartsQuote(string[] lines, int i)
    {
        var line = lines[i].Trim();
        if (OriginalMessage().IsMatch(line) || line.StartsWith("________________________________", StringComparison.Ordinal))
        {
            return true;
        }
        // Outlook: "From: …" followed within a few lines by "Sent:" or "Date:".
        if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase)
            && lines.Skip(i + 1).Take(4).Any(l => l.TrimStart().StartsWith("Sent:", StringComparison.OrdinalIgnoreCase) || l.TrimStart().StartsWith("Date:", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        // Gmail and Apple Mail: "On Mon, Mar 2, 2026 at 3:00 PM Jane <jane@example.com> wrote:", sometimes over two lines.
        return line.StartsWith("On ", StringComparison.Ordinal)
            && (line.EndsWith("wrote:", StringComparison.Ordinal) || (i + 1 < lines.Length && lines[i + 1].Trim().EndsWith("wrote:", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"^-{2,}\s*(Original Message|Forwarded message)\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessage();
}
