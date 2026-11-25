using PdfFormFiller.Core;

namespace PdfFormFiller.Internal;

/// <summary>
/// Measures Helvetica text widths using the embedded Liberation Sans font (metric-compatible with Helvetica/Arial).
/// Only used for layout; the PDF itself references the standard non-embedded Helvetica font.
/// </summary>
internal static class TextMeasurer
{
    private static readonly Lazy<TrueTypeMetrics> Font = new(() =>
    {
        using var s = typeof(TextMeasurer).Assembly.GetManifestResourceStream("PdfFormFiller.Fonts.LiberationSans-Regular.ttf")
                      ?? throw new PdfFormException("Embedded font resource missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new TrueTypeMetrics(ms.ToArray());
    });

    public static TrueTypeMetrics Metrics => Font.Value;

    private static readonly Lazy<TrueTypeFont> Face = new(() => new TrueTypeFont(Font.Value.Data));

    /// <summary>Liberation Sans Regular for the embedding engine (subsetting); built once and shared.</summary>
    public static TrueTypeFont FallbackFace => Face.Value;

    private static readonly Lazy<TrueTypeMetrics?> BoldFont = new(() =>
    {
        using var s = typeof(TextMeasurer).Assembly.GetManifestResourceStream("PdfFormFiller.Fonts.LiberationSans-Bold.ttf");
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new TrueTypeMetrics(ms.ToArray());
    });

    /// <summary>Liberation Sans Regular (the same instance as <see cref="Metrics"/>).</summary>
    public static TrueTypeMetrics Regular => Font.Value;

    /// <summary>True when the Bold font is embedded in this build of the assembly.</summary>
    public static bool BoldAvailable => BoldFont.Value is not null;

    /// <summary>Liberation Sans Bold. Throws <see cref="PdfFormException"/> when the resource is not embedded (check <see cref="BoldAvailable"/>).</summary>
    public static TrueTypeMetrics Bold => BoldFont.Value
        ?? throw new PdfFormException("The embedded Liberation Sans Bold font is missing from this build.");

    /// <summary>Width of <paramref name="text"/> at the given font size, in points.</summary>
    public static double Width(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var em = Font.Value.Width(text);
        // Liberation Sans 2.x made the middle dot wider than Helvetica (and than Liberation 1.x); keep the Helvetica width
        // this layout code has always assumed, so existing forms measure exactly as before.
        var dots = 0;
        foreach (var ch in text) if (ch == '\u00B7') dots++;
        if (dots > 0) em += dots * (HelveticaMiddleDot - Font.Value.Advance('\u00B7'));
        return em * fontSize;
    }

    private const double HelveticaMiddleDot = 0.278;

    public const double Ascent = 0.905;
    public const double Descent = 0.212;
    public const double CapHeight = 0.716;
    public const double LineHeight = 1.15;

    /// <summary>Greedy word wrap. Breaks very long words by character.</summary>
    public static List<string> Wrap(string text, double fontSize, double maxWidth)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (para.Length == 0) { lines.Add(string.Empty); continue; }
            var words = para.Split(' ');
            var cur = string.Empty;
            foreach (var w in words)
            {
                var candidate = cur.Length == 0 ? w : cur + " " + w;
                if (Width(candidate, fontSize) <= maxWidth || cur.Length == 0 && Width(w, fontSize) <= maxWidth)
                {
                    cur = candidate;
                    continue;
                }
                if (cur.Length > 0) { lines.Add(cur); cur = string.Empty; }
                if (Width(w, fontSize) <= maxWidth) { cur = w; continue; }
                var piece = string.Empty;
                foreach (var ch in w)
                {
                    if (Width(piece + ch, fontSize) > maxWidth && piece.Length > 0) { lines.Add(piece); piece = string.Empty; }
                    piece += ch;
                }
                cur = piece;
            }
            lines.Add(cur);
        }
        return lines;
    }

    // ================================================================== table text (font-parameterised)
    //
    // Rules shared by Width, Wrap, Truncate and MeasureColumn:
    //  * line breaks are \n, \r\n, \r, U+0085, U+2028, U+2029; a tab counts as one space; other control characters are dropped;
    //  * text is handled in grapheme clusters (surrogate pairs, combining marks, ZWJ and flag sequences are never split);
    //  * a cluster is as wide as its first character: combining marks add nothing, and anything the font cannot draw
    //    (emoji, CJK, ...) counts as one '?', which is how it is drawn;
    //  * break opportunities: after a run of spaces (and U+200B), after '-', U+2010, U+2013, U+2014 and '/' when a letter or
    //    digit sits on each side, and never at no-break spaces (U+00A0, U+2007, U+202F) or U+2011.

    private const double Eps = 1e-9;

    /// <summary>Width in points of the widest line of <paramref name="text"/> at the given size.</summary>
    public static double Width(TrueTypeMetrics font, string text, double fontSize)
        => MeasureColumn(font, text, fontSize).Natural;

    /// <summary>
    /// Word wrap for table cells. Lines break at spaces, after hyphens and slashes, and inside a word that is wider than
    /// <paramref name="maxWidth"/> (between clusters). Explicit line breaks are kept, so "a\n\nb" gives "a", "", "b". Spaces
    /// that end a line are dropped, leading spaces of a paragraph are kept. Empty input gives one empty line. A
    /// <paramref name="maxWidth"/> that is zero, negative, NaN or infinite disables soft wrapping. Linear in the text length.
    /// </summary>
    public static List<string> Wrap(TrueTypeMetrics font, string text, double fontSize, double maxWidth)
    {
        ArgumentNullException.ThrowIfNull(font);
        var lines = new List<string>();
        var t = Normalize(text ?? string.Empty, keepBreaks: true);
        var limit = double.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : double.PositiveInfinity;
        var pos = 0;
        while (true)
        {
            var nl = t.IndexOf('\n', pos);
            var end = nl < 0 ? t.Length : nl;
            WrapParagraph(font, t, pos, end, fontSize, limit, lines);
            if (nl < 0) break;
            pos = nl + 1;
        }
        return lines;
    }

    /// <summary>
    /// Fits <paramref name="text"/> on one line of at most <paramref name="maxWidth"/> by cutting it between clusters and
    /// appending <paramref name="ellipsis"/>. Line breaks and tabs count as spaces. Text that already fits is returned
    /// (normalised) without ellipsis; spaces before the ellipsis are dropped; when not even the ellipsis fits the result is
    /// empty. Linear in the length of the kept part.
    /// </summary>
    public static string Truncate(TrueTypeMetrics font, string text, double fontSize, double maxWidth, string ellipsis = "\u2026")
    {
        ArgumentNullException.ThrowIfNull(font);
        var t = Normalize(text ?? string.Empty, keepBreaks: false);
        if (!(maxWidth > 0)) return t.Length == 0 ? t : string.Empty;
        var ell = Normalize(ellipsis ?? string.Empty, keepBreaks: false);
        var ellW = Measure(font, ell, fontSize);
        double acc = 0;
        var keep = -1;      // end of the longest prefix that leaves room for the ellipsis
        var pos = 0;
        while (pos < t.Length)
        {
            var c = NextCluster(font, t, pos, t.Length, fontSize);
            if (acc + c.Width + ellW <= maxWidth + Eps) keep = pos + c.Length;
            acc += c.Width;
            pos += c.Length;
            if (acc > maxWidth + Eps)
            {
                if (keep < 0) return ellW <= maxWidth + Eps ? ell : string.Empty;
                return t[..keep].TrimEnd(' ') + ell;
            }
        }
        return t;
    }

    /// <summary>
    /// Natural width (widest line, spaces included, trailing spaces not) and minimum width (widest unbreakable word, the
    /// width at which Wrap never has to cut inside a word) of a cell text, for sizing columns. One linear pass.
    /// </summary>
    public static (double Natural, double Minimum) MeasureColumn(TrueTypeMetrics font, string text, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(font);
        var t = Normalize(text ?? string.Empty, keepBreaks: true);
        double natural = 0, minimum = 0;
        var pos = 0;
        var segs = new List<Segment>();
        while (true)
        {
            var nl = t.IndexOf('\n', pos);
            var end = nl < 0 ? t.Length : nl;
            segs.Clear();
            Segments(font, t, pos, end, fontSize, segs);
            double line = 0;
            for (var i = 0; i < segs.Count; i++)
            {
                minimum = Math.Max(minimum, segs[i].ContentWidth);
                line += segs[i].ContentWidth + (i < segs.Count - 1 ? segs[i].SpaceWidth : 0);
            }
            natural = Math.Max(natural, line);
            if (nl < 0) break;
            pos = nl + 1;
        }
        return (natural, minimum);
    }

    private static double Measure(TrueTypeMetrics font, string s, double fontSize)
    {
        double w = 0;
        for (var p = 0; p < s.Length;) { var c = NextCluster(font, s, p, s.Length, fontSize); w += c.Width; p += c.Length; }
        return w;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Unbreakable stretch: [Start, ContentEnd) is drawn, [ContentEnd, End) are the spaces after it.</summary>
    private readonly record struct Segment(int Start, int ContentEnd, int End, double ContentWidth, double SpaceWidth);

    private enum Kind : byte { Normal, Space, Hyphen, Slash }

    private readonly record struct Cluster(int Length, double Width, Kind Kind, bool Alnum);

    /// <summary>Line breaks to '\n' (or to a space), tab to space, other control characters removed. Returns the input when nothing changes.</summary>
    private static string Normalize(string s, bool keepBreaks)
    {
        var dirty = false;
        foreach (var ch in s)
            if (ch < ' ' && ch != ' ' || (ch >= '\u007f' && ch <= '\u009f') || ch is '\u2028' or '\u2029') { dirty = true; break; }
        if (!dirty) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
            if (ch is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029') sb.Append(keepBreaks ? '\n' : ' ');
            else if (ch == '\t') sb.Append(' ');
            else if (char.IsControl(ch)) continue;
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static Cluster NextCluster(TrueTypeMetrics font, string s, int pos, int end, double fontSize)
    {
        var c = s[pos];
        int len;
        if (c < 0x300 && (pos + 1 >= end || s[pos + 1] < 0x300)) len = 1;
        else len = Math.Max(1, System.Globalization.StringInfo.GetNextTextElementLength(s.AsSpan(pos, end - pos)));

        var kind = Kind.Normal;
        double em;
        var alnum = false;
        if (char.IsHighSurrogate(c) && len >= 2 && char.IsLowSurrogate(s[pos + 1]))
        {
            em = font.Advance('?'); // outside the font's BMP-only character map
            alnum = char.IsLetterOrDigit(s, pos);
        }
        else if (c is ' ' or '\u1680' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006'
                 or '\u2008' or '\u2009' or '\u200A' or '\u205F' or '\u3000')
        {
            kind = Kind.Space; em = font.Advance(' ');
        }
        else if (c == '\u200B') { kind = Kind.Space; em = 0; }
        else if (c is '\u00A0' or '\u2007' or '\u202F') em = font.Advance(' ');
        else if (c is '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD') em = 0;
        else
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark) em = 0;
            else em = font.Advance(c);
            alnum = char.IsLetterOrDigit(c);
            if (c is '-' or '\u2010' or '\u2013' or '\u2014') kind = Kind.Hyphen;
            else if (c == '/') kind = Kind.Slash;
        }
        return new Cluster(len, em * fontSize, kind, alnum);
    }

    /// <summary>Splits one paragraph into unbreakable segments. A paragraph's leading spaces are folded into its first word.</summary>
    private static void Segments(TrueTypeMetrics font, string t, int start, int end, double fontSize, List<Segment> into)
    {
        var pos = start;
        var segStart = start;
        double cw = 0, sw = 0;
        var contentEnd = start;
        var inSpaces = false;
        var prevAlnum = false;
        var carry = false; // leading spaces waiting to join the first word
        while (pos < end)
        {
            var c = NextCluster(font, t, pos, end, fontSize);
            var next = pos + c.Length;
            if (c.Kind == Kind.Space)
            {
                if (!inSpaces) { inSpaces = true; contentEnd = pos; }
                sw += c.Width;
                pos = next;
                prevAlnum = false;
                continue;
            }
            if (inSpaces)
            {
                // a word starts: close the previous segment (or remember leading spaces)
                if (contentEnd == segStart && into.Count == 0 && cw == 0)
                {
                    carry = true; // keep segStart so the indentation joins the first word
                    cw = sw; sw = 0;
                }
                else
                {
                    into.Add(new Segment(segStart, contentEnd, pos, cw, sw));
                    segStart = pos; cw = 0;
                    sw = 0;
                }
                inSpaces = false;
            }
            cw += c.Width;
            contentEnd = next;
            var breakAfter = false;
            if (c.Kind is Kind.Hyphen or Kind.Slash && prevAlnum && next < end)
            {
                var n = NextCluster(font, t, next, end, fontSize);
                breakAfter = n.Alnum;
            }
            prevAlnum = c.Alnum;
            pos = next;
            if (breakAfter)
            {
                into.Add(new Segment(segStart, contentEnd, pos, cw, 0));
                segStart = pos; cw = 0; sw = 0; carry = false;
            }
        }
        _ = carry;
        if (inSpaces) into.Add(new Segment(segStart, contentEnd, end, cw, sw));
        else if (pos > segStart || into.Count == 0) into.Add(new Segment(segStart, contentEnd, end, cw, 0));
    }

    private static void WrapParagraph(TrueTypeMetrics font, string t, int start, int end, double fontSize, double limit, List<string> lines)
    {
        var segs = new List<Segment>();
        Segments(font, t, start, end, fontSize, segs);
        var open = false;
        int lineStart = 0, lineEnd = 0;
        double curW = 0, pendW = 0;
        foreach (var seg in segs)
        {
            if (open && curW + pendW + seg.ContentWidth <= limit + Eps)
            {
                curW += pendW + seg.ContentWidth; lineEnd = seg.ContentEnd; pendW = seg.SpaceWidth;
                continue;
            }
            if (open) lines.Add(t.Substring(lineStart, lineEnd - lineStart));
            open = true;
            if (seg.ContentWidth <= limit + Eps)
            {
                lineStart = seg.Start; lineEnd = seg.ContentEnd; curW = seg.ContentWidth; pendW = seg.SpaceWidth;
                continue;
            }
            // wider than a whole line: cut between clusters
            var ls = seg.Start;
            double acc = 0;
            for (var p = seg.Start; p < seg.ContentEnd;)
            {
                var c = NextCluster(font, t, p, seg.ContentEnd, fontSize);
                if (acc > 0 && acc + c.Width > limit + Eps)
                {
                    lines.Add(t.Substring(ls, p - ls));
                    ls = p; acc = 0;
                }
                acc += c.Width;
                p += c.Length;
            }
            lineStart = ls; lineEnd = seg.ContentEnd; curW = acc; pendW = seg.SpaceWidth;
        }
        lines.Add(open ? t.Substring(lineStart, lineEnd - lineStart) : string.Empty);
    }
}
