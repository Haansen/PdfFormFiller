using System.Globalization;
using System.Text;
using PdfFormFiller.Core;

namespace PdfFormFiller.Reports.Internal;

/// <summary>A font with its fallbacks, as used in one document: cleaning, measuring, breaking and drawing text.</summary>
internal sealed class TextFace
{
    private static readonly Lazy<TrueTypeFont> LiberationRegular = new(() => Embedded("LiberationSans-Regular.ttf"));
    private static readonly Lazy<TrueTypeFont> LiberationBold = new(() => Embedded("LiberationSans-Bold.ttf"));

    /// <summary>Longer cell texts are cut. Far beyond anything readable; keeps one value from producing tens of thousands of pages.</summary>
    public const int MaxLength = 1_000_000;

    private const double Eps = 1e-3;

    private readonly PdfFont[] _fonts;
    private readonly PdfFont _first;
    private readonly float[] _em = new float[0x10000]; // advance per BMP character; NaN = not looked up, -1 = no glyph
    private readonly float _space;
    private readonly int _minusGlyph;

    private TextFace(params PdfFont[] fonts)
    {
        _fonts = fonts;
        _first = fonts[0];
        Array.Fill(_em, float.NaN);
        _space = Math.Max(0, Em(' '));
        _minusGlyph = _first.Face.GlyphIndex('\u2212');
        CapHeight = _first.Face.CapHeightEm is > 0.3 and < 1.2 and var h ? h : 0.72;
        // fonts with unusual metrics get more room between lines, so that nothing is clipped or touches
        LineHeight = Math.Clamp(_first.Face.AscentEm + _first.Face.DescentEm, 1.15, 1.7) + 0.08;
    }

    public double CapHeight { get; }
    public double LineHeight { get; }
    public double SpaceWidth => _space;

    public static (TextFace Regular, TextFace Bold) Create(PdfBuilder pdf, ReportBranding branding)
    {
        var regular = pdf.AddFont(LiberationRegular.Value);
        var bold = pdf.AddFont(LiberationBold.Value);
        if (branding.Regular is not { } own) return (new TextFace(regular, bold), new TextFace(bold, regular));
        var ownRegular = pdf.AddFont(Load(own));
        var ownBold = branding.Bold is { } b ? pdf.AddFont(Load(b)) : ownRegular;
        return (new TextFace(ownRegular, regular), new TextFace(ownBold, bold, regular));
    }

    private static TrueTypeFont Load(byte[] data) => new(data);

    private static TrueTypeFont Embedded(string name)
    {
        using var s = typeof(PdfFormDocument).Assembly.GetManifestResourceStream("PdfFormFiller.Fonts." + name)
                      ?? throw new PdfFormException("The embedded font " + name + " is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new TrueTypeFont(ms.ToArray());
    }

    // ------------------------------------------------------------------ measuring

    private float Em(int codePoint)
    {
        if (codePoint < 0x10000)
        {
            var e = _em[codePoint];
            return float.IsNaN(e) ? _em[codePoint] = Find(codePoint) : e;
        }
        return Find(codePoint);
    }

    private float Find(int codePoint)
    {
        foreach (var f in _fonts)
            if (f.HasGlyph(codePoint)) return (float)f.Face.AdvanceUnits(f.Face.GlyphIndex(codePoint is '\t' or 0xA0 or 0x202F && f.Face.GlyphIndex(codePoint) == 0 ? ' ' : codePoint)) / f.Face.UnitsPerEm;
        return -1;
    }

    private static int CodePoint(string s, int i, out int length)
    {
        length = 1;
        if (!char.IsHighSurrogate(s[i]) || i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return s[i];
        length = 2;
        return char.ConvertToUtf32(s[i], s[i + 1]);
    }

    /// <summary>Width of cleaned text.</summary>
    public double Width(string text, double size) => Width(text, 0, text.Length) * size;

    private double Width(string s, int start, int end)
    {
        double w = 0;
        for (var i = start; i < end;)
        {
            w += Math.Max(0, Em(CodePoint(s, i, out var n)));
            i += n;
        }
        return w;
    }

    /// <summary>Widest line and widest unbreakable word of cleaned text, at size 1.</summary>
    public (double Natural, double Minimum) Measure(string s)
    {
        double natural = 0, minimum = 0, line = 0, word = 0;
        var prev = '\0';
        var end = Math.Min(s.Length, 4000); // enough to know that the column wants all the width it can get
        for (var i = 0; i < end;)
        {
            var c = s[i];
            if (c == '\n')
            {
                natural = Math.Max(natural, line);
                minimum = Math.Max(minimum, word);
                line = word = 0;
                prev = c;
                i++;
                continue;
            }
            var w = Math.Max(0, Em(CodePoint(s, i, out var n)));
            line += w;
            if (c == ' ') { minimum = Math.Max(minimum, word); word = 0; }
            else
            {
                word += w;
                if (BreakAfter(s, i, end, prev)) { minimum = Math.Max(minimum, word); word = 0; }
            }
            prev = c;
            i += n;
        }
        return (Math.Max(natural, line), Math.Max(minimum, word));
    }

    private static bool BreakAfter(string s, int i, int end, char prev)
        => s[i] is '-' or '/' or '\u2010' or '\u2013' or '\u2014' or '_' or '@' && char.IsLetterOrDigit(prev) && i + 1 < end && char.IsLetterOrDigit(s[i + 1]);

    // ------------------------------------------------------------------ cleaning

    /// <summary>
    /// Text that can be drawn. Line breaks become \n (or a space), tabs spaces; invisible control and direction characters are
    /// dropped; what no font can draw becomes '?' (one per visible character, so an emoji sequence is one).
    /// </summary>
    public string Clean(string? text, bool keepBreaks = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length > MaxLength) text = text[..MaxLength] + "\u2026";
        var simple = true;
        foreach (var c in text)
            if (c < ' ' || c >= 0x7F && (c < 0xA0 || c == 0xAD || c >= 0x300) || Em(c) < 0) { simple = false; break; }
        if (simple) return text;

        var sb = new StringBuilder(text.Length);
        var composed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            if (c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029') { sb.Append(keepBreaks ? '\n' : ' '); continue; }
            if (c == '\t') { sb.Append(' '); continue; }
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[++i]); composed = true; }
                else sb.Append('?');
                continue;
            }
            var category = c < 0x80 ? UnicodeCategory.OtherLetter : char.GetUnicodeCategory(c);
            if (char.IsControl(c) || c is >= '\uFFF0' and <= '\uFFFB' or '\uFFFE' or '\uFFFF') continue;
            if (category == UnicodeCategory.Format && c != '\u200D') continue; // joiners are needed to see what belongs together
            if (category == UnicodeCategory.SpaceSeparator && Em(c) < 0) { sb.Append(' '); continue; }
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark || c == '\u200D') composed = true;
            sb.Append(c);
        }
        var s = sb.ToString();
        if (composed)
        {
            try { s = s.Normalize(NormalizationForm.FormC); }
            catch (Exception e) when (e is ArgumentException or PlatformNotSupportedException) { }
        }

        sb.Clear();
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '\n') { sb.Append('\n'); i++; continue; }
            var first = CodePoint(s, i, out var n);
            var end = i + n;
            if (composed && first >= 0x80 || composed && end < s.Length && s[end] >= 0x300) end = i + Math.Max(n, StringInfo.GetNextTextElementLength(s, i));
            if (Em(first) >= 0)
            {
                sb.Append(s, i, n);
                // marks that the fonts lack are left out; the letter below them says more than a question mark
                for (var k = i + n; k < end; k += n)
                    if (Em(CodePoint(s, k, out n)) >= 0 && s[k] != '\u200D') sb.Append(s, k, n);
            }
            else if (first != '\u200D') sb.Append('?');
            i = end;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ breaking

    /// <summary>Lines of cleaned text, broken at spaces, after hyphens and slashes, and inside words that are too long.</summary>
    public List<string> Wrap(string text, double size, double width)
    {
        var lines = new List<string>(1);
        if (text.IndexOf('\n') < 0 && (text.Length * size * 0.1 < width || text.Length < 40) && Width(text, size) <= width + Eps)
        {
            lines.Add(text);
            return lines;
        }
        var max = width / size + Eps;
        var start = 0;
        while (true)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl;
            WrapParagraph(text, start, end, max, lines);
            if (nl < 0) return lines;
            start = nl + 1;
        }
    }

    private void WrapParagraph(string s, int start, int end, double max, List<string> lines)
    {
        int line = start, brk = -1;
        double w = 0, atBreak = 0;
        var text = false; // the line has something other than spaces
        var prev = '\0';
        for (var i = start; i < end;)
        {
            var c = s[i];
            var cw = (double)Math.Max(0, Em(CodePoint(s, i, out var n)));
            // marks stay with their letter
            while (i + n < end && s[i + n] >= 0x300 && char.GetUnicodeCategory(s[i + n]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
                cw += Math.Max(0, Em(s[i + n++]));
            if (c == ' ')
            {
                w += cw;
                i++;
                if (text) (brk, atBreak) = (i, w);
                prev = c;
                continue;
            }
            if (w + cw > max && i > line)
            {
                if (brk > line) { Emit(line, brk); line = brk; w -= atBreak; }
                else { Emit(line, i); line = i; w = 0; }
                brk = -1;
                text = line < i;
                continue;
            }
            w += cw;
            text = true;
            if (BreakAfter(s, i, end, prev)) (brk, atBreak) = (i + n, w);
            prev = c;
            i += n;
        }
        Emit(line, end);

        void Emit(int from, int to)
        {
            while (to > from && s[to - 1] == ' ') to--;
            lines.Add(from == 0 && to == s.Length ? s : s[from..to]);
        }
    }

    /// <summary>One line of cleaned text, cut with an ellipsis if needed.</summary>
    public string Truncate(string text, double size, double width)
    {
        text = text.Replace('\n', ' ');
        if (Width(text, size) <= width + Eps) return text;
        var max = width / size - Math.Max(0, Em('\u2026')) + Eps;
        double w = 0;
        var i = 0;
        while (i < text.Length)
        {
            var cw = Math.Max(0, Em(CodePoint(text, i, out var n)));
            if (w + cw > max) break;
            w += cw;
            i += n;
        }
        return Em('\u2026') * size > width ? "" : text[..i].TrimEnd(' ') + "\u2026";
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// Draws one line of cleaned text. <paramref name="tabs"/> TABs are drawn (as blanks) just before it, in the same text object:
    /// PDFium ignores text objects of white space only, so that is what makes pasted cells end up in columns.
    /// With <paramref name="number"/>, a leading minus sign is copied as a hyphen, which is what spreadsheets understand.
    /// </summary>
    public void Draw(ContentWriter c, double size, double x, double y, string text, int tabs = 0, bool number = false)
    {
        if (text.Length == 0) return;
        if (number && text[0] == '\u2212' && _minusGlyph != 0 && tabs < 128)
        {
            Span<ushort> codes = stackalloc ushort[tabs + 1];
            if (tabs > 0) codes[..tabs].Fill(_first.TabCode);
            codes[tabs] = _first.Encode(_minusGlyph, "-");
            c.ShowCodes(_first, size, x - tabs * _space * size, y, codes);
            x += Em('\u2212') * size;
            text = text[1..];
            tabs = 0;
            if (text.Length == 0) return;
        }
        if (tabs > 0)
        {
            text = new string('\t', tabs) + text;
            x -= tabs * _space * size;
        }
        if (_first.CanDraw(text)) { c.ShowText(_first, size, x, y, text); return; }
        foreach (var run in PdfFont.Split(text, _fonts)) x += c.ShowText(run.Font, size, x, y, run.Font.ReplaceMissing(run.Text));
    }

    /// <summary>Draws nothing visible, but is copied and read as <paramref name="text"/> (which must not be empty).</summary>
    public void DrawBlank(ContentWriter c, double size, double x, double y, string text, int tabs)
    {
        var glyph = _first.Face.GlyphIndex(' ');
        if (glyph == 0) { Draw(c, size, x, y, text, tabs); return; } // a font without a space: the text becomes visible
        Span<ushort> codes = stackalloc ushort[Math.Min(tabs, 128) + 1];
        codes[..^1].Fill(_first.TabCode);
        codes[^1] = _first.Encode(glyph, text);
        c.ShowCodes(_first, size, x - (codes.Length - 1) * _space * size, y, codes);
    }
}
