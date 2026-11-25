using System.Globalization;
using System.Text;

namespace PdfFormFiller.Core;

internal enum TokenKind { Number, String, Name, ArrayOpen, ArrayClose, DictOpen, DictClose, Keyword, BraceOpen, BraceClose, Eof }

internal readonly struct Token
{
    public TokenKind Kind { get; }
    public string Text { get; }      // keyword / name text; numbers as text
    public byte[]? Bytes { get; }    // string payload
    public bool Hex { get; }
    public int Position { get; }

    public Token(TokenKind kind, string text, int pos, byte[]? bytes = null, bool hex = false)
    {
        Kind = kind; Text = text; Position = pos; Bytes = bytes; Hex = hex;
    }

    public bool IsKeyword(string kw) => Kind == TokenKind.Keyword && Text == kw;
    public override string ToString() => $"{Kind}:{Text}";
}

/// <summary>Tokenizer for PDF syntax (ISO 32000-1 §7.2). Operates on a byte buffer with a movable position.</summary>
internal sealed class Lexer
{
    private readonly byte[] _buf;
    public int Position { get; set; }
    public byte[] Buffer => _buf;
    public int Length => _buf.Length;

    public Lexer(byte[] buffer, int position = 0)
    {
        _buf = buffer; Position = position;
    }

    public static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    public void SkipWhitespace()
    {
        while (Position < _buf.Length)
        {
            var b = _buf[Position];
            if (IsWhitespace(b)) { Position++; continue; }
            if (b == (byte)'%')
            {
                while (Position < _buf.Length && _buf[Position] != (byte)'\n' && _buf[Position] != (byte)'\r') Position++;
                continue;
            }
            break;
        }
    }

    public Token Peek()
    {
        var save = Position;
        var t = Next();
        Position = save;
        return t;
    }

    public Token Next()
    {
        again:
        SkipWhitespace();
        if (Position >= _buf.Length) return new Token(TokenKind.Eof, string.Empty, Position);
        var start = Position;
        var b = _buf[Position];
        switch (b)
        {
            case (byte)'[': Position++; return new Token(TokenKind.ArrayOpen, "[", start);
            case (byte)']': Position++; return new Token(TokenKind.ArrayClose, "]", start);
            case (byte)'{': Position++; return new Token(TokenKind.BraceOpen, "{", start);
            case (byte)'}': Position++; return new Token(TokenKind.BraceClose, "}", start);
            case (byte)'<':
                if (Position + 1 < _buf.Length && _buf[Position + 1] == (byte)'<') { Position += 2; return new Token(TokenKind.DictOpen, "<<", start); }
                return ReadHexString();
            case (byte)'>':
                if (Position + 1 < _buf.Length && _buf[Position + 1] == (byte)'>') { Position += 2; return new Token(TokenKind.DictClose, ">>", start); }
                Position++; goto again; // stray '>' – skip (loop, never recurse: garbage can contain millions of these)
            case (byte)'(':
                return ReadLiteralString();
            case (byte)'/':
                return ReadName();
            case (byte)')':
                Position++; goto again; // stray ')' 
        }
        if ((b >= (byte)'0' && b <= (byte)'9') || b == (byte)'+' || b == (byte)'-' || b == (byte)'.')
            return ReadNumber();
        // keyword
        while (Position < _buf.Length && IsRegular(_buf[Position])) Position++;
        if (Position == start) Position++;
        return new Token(TokenKind.Keyword, Encoding.ASCII.GetString(_buf, start, Position - start), start);
    }

    private Token ReadNumber()
    {
        var start = Position;
        while (Position < _buf.Length && IsRegular(_buf[Position])) Position++;
        var text = Encoding.ASCII.GetString(_buf, start, Position - start);
        return new Token(TokenKind.Number, text, start);
    }

    public static double ParseNumber(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return double.IsFinite(v) ? v : 0;
        // tolerate junk like "--5", "3.4.5", "5-"
        var sb = new StringBuilder();
        var seenDot = false; var seenDigit = false;
        foreach (var c in text)
        {
            if (char.IsDigit(c)) { sb.Append(c); seenDigit = true; }
            else if (c == '.' && !seenDot) { sb.Append(c); seenDot = true; }
            else if (c == '-' && sb.Length == 0) sb.Append(c);
            else if (seenDigit) break;
        }
        return double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) ? v : 0;
    }

    private Token ReadName()
    {
        var start = Position;
        Position++; // '/'
        var sb = new StringBuilder("/");
        while (Position < _buf.Length && IsRegular(_buf[Position]))
        {
            var c = _buf[Position++];
            if (c == (byte)'#' && Position + 1 < _buf.Length && IsHex(_buf[Position]) && IsHex(_buf[Position + 1]))
            {
                sb.Append((char)((HexVal(_buf[Position]) << 4) | HexVal(_buf[Position + 1])));
                Position += 2;
            }
            else sb.Append((char)c);
        }
        return new Token(TokenKind.Name, sb.ToString(), start);
    }

    private Token ReadLiteralString()
    {
        var start = Position;
        Position++; // '('
        var depth = 1;
        var ms = new MemoryStream();
        while (Position < _buf.Length)
        {
            var c = _buf[Position++];
            if (c == (byte)'\\')
            {
                if (Position >= _buf.Length) break;
                var e = _buf[Position++];
                switch (e)
                {
                    case (byte)'n': ms.WriteByte((byte)'\n'); break;
                    case (byte)'r': ms.WriteByte((byte)'\r'); break;
                    case (byte)'t': ms.WriteByte((byte)'\t'); break;
                    case (byte)'b': ms.WriteByte(8); break;
                    case (byte)'f': ms.WriteByte(12); break;
                    case (byte)'\r':
                        if (Position < _buf.Length && _buf[Position] == (byte)'\n') Position++;
                        break;
                    case (byte)'\n': break;
                    default:
                        if (e >= (byte)'0' && e <= (byte)'7')
                        {
                            var v = e - '0';
                            for (var i = 0; i < 2 && Position < _buf.Length && _buf[Position] >= (byte)'0' && _buf[Position] <= (byte)'7'; i++)
                                v = v * 8 + (_buf[Position++] - '0');
                            ms.WriteByte((byte)(v & 0xFF));
                        }
                        else ms.WriteByte(e);
                        break;
                }
            }
            else if (c == (byte)'(') { depth++; ms.WriteByte(c); }
            else if (c == (byte)')')
            {
                depth--;
                if (depth == 0) break;
                ms.WriteByte(c);
            }
            else ms.WriteByte(c);
        }
        return new Token(TokenKind.String, string.Empty, start, ms.ToArray());
    }

    private Token ReadHexString()
    {
        var start = Position;
        Position++; // '<'
        var ms = new MemoryStream();
        var hi = -1;
        while (Position < _buf.Length)
        {
            var c = _buf[Position++];
            if (c == (byte)'>') break;
            if (!IsHex(c)) continue;
            var v = HexVal(c);
            if (hi < 0) hi = v;
            else { ms.WriteByte((byte)((hi << 4) | v)); hi = -1; }
        }
        if (hi >= 0) ms.WriteByte((byte)(hi << 4));
        return new Token(TokenKind.String, string.Empty, start, ms.ToArray(), hex: true);
    }

    public static bool IsHex(byte c) => (c >= (byte)'0' && c <= (byte)'9') || (c >= (byte)'a' && c <= (byte)'f') || (c >= (byte)'A' && c <= (byte)'F');
    public static int HexVal(byte c) => c <= (byte)'9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>Finds the next occurrence of an ASCII pattern at or after <paramref name="from"/>; -1 if none.</summary>
    public int IndexOf(string ascii, int from)
    {
        var pat = Encoding.ASCII.GetBytes(ascii);
        var idx = _buf.AsSpan(Math.Min(from, _buf.Length)).IndexOf(pat);
        return idx < 0 ? -1 : idx + from;
    }

    public int LastIndexOf(string ascii, int before)
    {
        var pat = Encoding.ASCII.GetBytes(ascii);
        var idx = _buf.AsSpan(0, Math.Min(before, _buf.Length)).LastIndexOf(pat);
        return idx;
    }
}
