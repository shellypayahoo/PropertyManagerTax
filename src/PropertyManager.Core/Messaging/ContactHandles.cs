namespace PropertyManager.Core.Messaging;

/// <summary>Normalizes phone numbers and emails so a Messages handle ("+12025551234") matches a stored "(202) 555-1234".</summary>
public static class ContactHandles
{
    public static string? Normalize(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return null;
        }

        var value = handle.Trim();
        if (value.Contains('@'))
        {
            return value.ToLowerInvariant();
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        // US numbers: compare the last 10 digits so "+1 202…" and "202…" match.
        if (digits.Length == 11 && digits[0] == '1')
        {
            digits = digits[1..];
        }
        return digits.Length >= 7 ? digits : null;
    }
}
