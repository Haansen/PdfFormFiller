using System.Text;

namespace PdfFormFiller.Internal;

/// <summary>Encodes text to WinAnsiEncoding bytes and produces PDF literal strings.</summary>
internal static class WinAnsi
{
    private static readonly Dictionary<char, byte> Specials = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85,
        ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A,
        ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92,
        ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C,
        ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    public static bool CanEncode(char c) => c < 0x80 || (c >= 0xA0 && c <= 0xFF) || Specials.ContainsKey(c);

    public static byte Encode(char c)
    {
        if (c < 0x80 || (c >= 0xA0 && c <= 0xFF)) return (byte)c;
        return Specials.TryGetValue(c, out var b) ? b : (byte)'?';
    }

    /// <summary>Replaces characters that WinAnsi cannot represent with '?', so measuring and drawing agree.</summary>
    public static string Sanitize(string s)
    {
        if (s.All(CanEncode)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(CanEncode(c) ? c : '?');
        return sb.ToString();
    }

    /// <summary>Returns a PDF literal string "(...)" with escaping.</summary>
    public static string Literal(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('(');
        foreach (var c in s)
        {
            var b = Encode(c);
            switch (b)
            {
                case (byte)'(': sb.Append("\\("); break;
                case (byte)')': sb.Append("\\)"); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                default:
                    if (b < 0x20 || b >= 0x7F) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    else sb.Append((char)b);
                    break;
            }
        }
        sb.Append(')');
        return sb.ToString();
    }
}
