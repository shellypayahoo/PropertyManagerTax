namespace PropertyManager.Core.Import;

public static class RuleSuggestions
{
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "zelle", "payment", "from", "to", "pos", "purchase", "debit", "credit", "card", "ach", "online",
        "recurring", "checkcard", "withdrawal", "deposit", "web", "id:", "ppd", "des:",
    };

    /// <summary>Suggests the text a rule should match for a bank description, e.g. "HOME DEPOT #2501 DC" → "HOME DEPOT".</summary>
    public static string MatchTextFor(string description)
    {
        var words = description
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(NoiseWords.Contains)
            .TakeWhile(w => !w.Any(char.IsDigit) && !w.Contains('#') && !w.Contains('*') && !w.StartsWith("conf", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();
        return words.Count > 0 ? string.Join(' ', words) : description;
    }
}
