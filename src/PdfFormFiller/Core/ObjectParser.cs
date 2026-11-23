using System.Text;

namespace PdfFormFiller.Core;

/// <summary>Builds objects from tokens. Handles "n g R" references and stream bodies.</summary>
internal sealed class ObjectParser
{
    private readonly Lexer _lex;
    private readonly PdfDocument? _doc;

    public ObjectParser(Lexer lexer, PdfDocument? doc)
    {
        _lex = lexer; _doc = doc;
    }

    public Lexer Lexer => _lex;

    /// <summary>Direct nesting limit; keeps every recursive walk over direct objects far from the stack limit.</summary>
    public const int MaxDepth = 200;

    /// <summary>Parses one object. Returns null at EOF or on an unexpected closing token.</summary>
    public PdfObject? ParseObject(int depth = 0)
    {
        if (depth > MaxDepth) throw new PdfFormException("PDF object nesting too deep.");
        var t = _lex.Next();
        return ParseFrom(t, depth);
    }

    private PdfObject? ParseFrom(Token t, int depth)
    {
        if (depth > MaxDepth) throw new PdfFormException("PDF object nesting too deep.");
        switch (t.Kind)
        {
            case TokenKind.Eof: return null;
            case TokenKind.Number:
            {
                // lookahead for "gen R"
                var save = _lex.Position;
                var t2 = _lex.Next();
                if (t2.Kind == TokenKind.Number)
                {
                    var save2 = _lex.Position;
                    var t3 = _lex.Next();
                    if (t3.IsKeyword("R") && _doc is not null && IsInt(t.Text) && IsInt(t2.Text))
                        return new PdfReference(_doc, ToInt(Lexer.ParseNumber(t.Text)), ToInt(Lexer.ParseNumber(t2.Text)));
                    _lex.Position = save2;
                }
                _lex.Position = save;
                var v = Lexer.ParseNumber(t.Text);
                return IsInt(t.Text) && Math.Abs(v) < 9e15 ? new PdfNumber((long)v) : new PdfNumber(v);
            }
            case TokenKind.String: return new PdfString(t.Bytes!, t.Hex);
            case TokenKind.Name: return new PdfName(t.Text);
            case TokenKind.ArrayOpen:
            {
                var arr = new PdfArray();
                while (true)
                {
                    var n = _lex.Next();
                    if (n.Kind == TokenKind.ArrayClose || n.Kind == TokenKind.Eof) break;
                    if (n.Kind == TokenKind.DictClose) continue; // junk
                    if (n.IsKeyword("endobj") || n.IsKeyword("endstream")) { _lex.Position = n.Position; break; }
                    var item = ParseFrom(n, depth + 1);
                    if (item is not null) arr.Add(item);
                }
                return arr;
            }
            case TokenKind.DictOpen:
            {
                var dict = new PdfDictionary();
                while (true)
                {
                    var k = _lex.Next();
                    if (k.Kind == TokenKind.DictClose || k.Kind == TokenKind.Eof) break;
                    if (k.IsKeyword("endobj") || k.IsKeyword("stream") || k.IsKeyword("endstream")) { _lex.Position = k.Position; break; }
                    if (k.Kind != TokenKind.Name)
                    {
                        // junk value without a key: parse and drop
                        if (k.Kind is TokenKind.ArrayOpen or TokenKind.DictOpen) ParseFrom(k, depth + 1);
                        continue;
                    }
                    var vt = _lex.Next();
                    if (vt.Kind == TokenKind.DictClose) { dict[k.Text] = PdfNull.Instance; break; }
                    var val = ParseFrom(vt, depth + 1);
                    if (val is not null) dict[k.Text] = val;
                }
                // stream?
                var save = _lex.Position;
                var next = _lex.Next();
                if (next.IsKeyword("stream")) return ReadStream(dict);
                _lex.Position = save;
                return dict;
            }
            case TokenKind.Keyword:
                return t.Text switch
                {
                    "true" => PdfBool.True,
                    "false" => PdfBool.False,
                    "null" => PdfNull.Instance,
                    _ => new PdfKeyword(t.Text),
                };
            case TokenKind.ArrayClose:
            case TokenKind.DictClose:
            case TokenKind.BraceOpen:
            case TokenKind.BraceClose:
                return new PdfKeyword(t.Text);
        }
        return null;
    }

    public static int ToInt(double v) => double.IsNaN(v) ? 0 : (int)Math.Clamp(v, int.MinValue, int.MaxValue);

    private static bool IsInt(string s) => s.Length > 0 && s.All(c => char.IsDigit(c) || c == '+' || c == '-') && s.Any(char.IsDigit);

    private PdfStream ReadStream(PdfDictionary dict)
    {
        var buf = _lex.Buffer;
        var p = _lex.Position; // right after "stream"
        if (p < buf.Length && buf[p] == (byte)'\r') p++;
        if (p < buf.Length && buf[p] == (byte)'\n') p++;
        var start = p;

        long length = -1;
        var lenObj = dict["/Length"];
        if (lenObj is PdfNumber ln) length = ln.LongValue;
        else if (lenObj is PdfReference lr)
        {
            try { if (lr.Target is PdfNumber ln2) length = ln2.LongValue; } catch { length = -1; }
        }

        var valid = false;
        if (length >= 0 && start + length <= buf.Length)
        {
            // verify "endstream" follows (after optional EOL)
            var q = (int)(start + length);
            var limit = Math.Min(buf.Length, q + 4);
            while (q < limit && Lexer.IsWhitespace(buf[q])) q++;
            valid = Matches(buf, q, "endstream");
        }
        if (!valid)
        {
            var idx = _lex.IndexOf("endstream", start);
            if (idx < 0) idx = buf.Length;
            var end = idx;
            if (end > start && buf[end - 1] == (byte)'\n') end--;
            if (end > start && buf[end - 1] == (byte)'\r') end--;
            length = end - start;
        }
        var data = new byte[length];
        Array.Copy(buf, start, data, 0, length);
        var stream = new PdfStream(dict, data);
        // move past endstream
        var e = _lex.IndexOf("endstream", (int)(start + length));
        _lex.Position = e < 0 ? buf.Length : e + "endstream".Length;
        return stream;
    }

    public static bool Matches(byte[] buf, int pos, string ascii)
    {
        if (pos < 0 || pos + ascii.Length > buf.Length) return false;
        for (var i = 0; i < ascii.Length; i++) if (buf[pos + i] != (byte)ascii[i]) return false;
        return true;
    }

    /// <summary>Parses "num gen obj ... endobj" at the given offset. Returns null if the header does not match.</summary>
    public PdfObject? ParseIndirectAt(int offset, int expectedNumber, out int number, out int generation)
    {
        number = -1; generation = 0;
        _lex.Position = offset;
        var t1 = _lex.Next(); var t2 = _lex.Next(); var t3 = _lex.Next();
        if (t1.Kind != TokenKind.Number || t2.Kind != TokenKind.Number || !t3.IsKeyword("obj")) return null;
        number = ToInt(Lexer.ParseNumber(t1.Text));
        generation = ToInt(Lexer.ParseNumber(t2.Text));
        if (expectedNumber >= 0 && number != expectedNumber) return null;
        var obj = ParseObject();
        if (obj is PdfKeyword kw && kw.Text == "endobj") return PdfNull.Instance;
        return obj ?? PdfNull.Instance;
    }
}

/// <summary>Bare keyword token that is not a real object (operators in content streams, "endobj", etc.).</summary>
internal sealed class PdfKeyword : PdfObject
{
    public string Text { get; }
    public PdfKeyword(string text) => Text = text;
    public override string ToString() => Text;
}

internal static class PdfExtCore
{
    public static PdfObject? Resolve(PdfObject? o)
    {
        var guard = 0;
        while (o is PdfReference r && guard++ < 32) o = r.Target;
        return o is PdfNull ? null : o;
    }

    public static int? Int(this PdfDictionary d, string key) => Resolve(d[key]) is PdfNumber n ? n.IntValue : null;
    public static long? Long(this PdfDictionary d, string key) => Resolve(d[key]) is PdfNumber n ? n.LongValue : null;
    public static double? Real(this PdfDictionary d, string key) => Resolve(d[key]) is PdfNumber n ? n.Value : null;
    public static string? Name(this PdfDictionary d, string key) => Resolve(d[key]) is PdfName n ? n.Value : null;
    public static PdfDictionary? Dict(this PdfDictionary d, string key) => Resolve(d[key]) as PdfDictionary;
    public static PdfArray? Arr(this PdfDictionary d, string key) => Resolve(d[key]) as PdfArray;
    public static PdfStream? Stream(this PdfDictionary d, string key) => Resolve(d[key]) as PdfStream;
    public static PdfString? StrObj(this PdfDictionary d, string key) => Resolve(d[key]) as PdfString;
    public static bool? Bool(this PdfDictionary d, string key) => Resolve(d[key]) is PdfBool b ? b.Value : null;

    public static double RealAt(this PdfArray a, int i) => i < a.Count && Resolve(a[i]) is PdfNumber n ? n.Value : 0;
    public static int IntAt(this PdfArray a, int i) => i < a.Count && Resolve(a[i]) is PdfNumber n ? n.IntValue : 0;

    public static IEnumerable<PdfDictionary> Dicts(this PdfArray a)
    {
        foreach (var it in a.Items) if (Resolve(it) is PdfDictionary d) yield return d;
    }

    public static void RemoveObjectRefCore(this PdfArray a, PdfObject target)
    {
        for (var i = a.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Resolve(a[i]), target)) a.RemoveAt(i);
    }
}
