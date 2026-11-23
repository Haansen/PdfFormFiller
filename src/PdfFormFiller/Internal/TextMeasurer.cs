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

    /// <summary>Width of <paramref name="text"/> at the given font size, in points.</summary>
    public static double Width(string text, double fontSize)
        => string.IsNullOrEmpty(text) ? 0 : Font.Value.Width(text) * fontSize;

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
}
