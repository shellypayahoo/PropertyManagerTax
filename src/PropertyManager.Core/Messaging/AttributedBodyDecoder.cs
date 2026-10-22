using System.Text;

namespace PropertyManager.Core.Messaging;

/// <summary>
/// Extracts the plain text from a Messages <c>attributedBody</c> value. Since macOS Ventura, many messages have no
/// <c>text</c> column and keep their text only in this archived NSAttributedString ("typedstream"). The string is
/// stored after the "NSString" class name as a '+'-tagged, length-prefixed UTF-8 run.
/// </summary>
public static class AttributedBodyDecoder
{
    private static readonly byte[] NSStringMarker = "NSString"u8.ToArray();

    public static string? Decode(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        var start = body.AsSpan().IndexOf(NSStringMarker);
        if (start < 0)
        {
            return null;
        }

        // A few header bytes follow the class name, then '+' (0x2B) introduces the string.
        var i = start + NSStringMarker.Length;
        var plus = body.AsSpan(i, Math.Min(12, body.Length - i)).IndexOf((byte)0x2B);
        if (plus < 0)
        {
            return null;
        }
        i += plus + 1;
        if (i >= body.Length)
        {
            return null;
        }

        // Length: one byte if under 0x80; 0x81 = 2-byte, 0x82 = 4-byte little-endian length follows.
        int length;
        switch (body[i])
        {
            case 0x81 when i + 2 < body.Length:
                length = body[i + 1] | (body[i + 2] << 8);
                i += 3;
                break;
            case 0x82 when i + 4 < body.Length:
                length = BitConverter.ToInt32(body, i + 1);
                i += 5;
                break;
            case < 0x80:
                length = body[i];
                i += 1;
                break;
            default:
                return null;
        }

        if (length < 0 || i + length > body.Length)
        {
            return null;
        }
        return Encoding.UTF8.GetString(body, i, length);
    }
}
