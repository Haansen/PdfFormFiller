using System.Globalization;

namespace PdfFormFiller.Core;

/// <summary>An RGB colour.</summary>
internal readonly record struct PdfColor(byte R, byte G, byte B)
{
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor White = new(255, 255, 255);

    /// <summary>Parses "#RRGGBB" or "#RGB" (the # is optional).</summary>
    /// <exception cref="FormatException">Anything else.</exception>
    public static PdfColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.StartsWith('#') ? text[1..] : text;
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 || !s.All(char.IsAsciiHexDigit)) throw new FormatException($"'{text}' is not a colour; write it as #RRGGBB.");
        var v = int.Parse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return new PdfColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>How a shape is painted.</summary>
internal enum PaintMode { Fill, Stroke, FillAndStroke }

/// <summary>Kind of content that is not part of the document's meaning.</summary>
internal enum ArtifactKind
{
    /// <summary>Running headers and footers, page numbers, repeated table headers.</summary>
    Pagination,
    /// <summary>Rules, borders and backgrounds.</summary>
    Layout,
    /// <summary>Full-page backgrounds such as watermarks.</summary>
    Background,
}

/// <summary>
/// Writes the content of one page. Coordinates are in points with the origin at the bottom left of the page and y upwards.
/// Colours, line width and font are only written when they change, so setting them before every shape costs nothing.
/// Numbers are written with three decimals regardless of culture; a number that is not finite throws
/// <see cref="ArgumentOutOfRangeException"/>.
/// <para>
/// <see cref="SaveState"/>/<see cref="RestoreState"/> and the Begin.../<see cref="EndMarked"/> pairs must balance before the
/// page is closed; otherwise <see cref="InvalidOperationException"/> is thrown. In a document created without tagging the marked content methods do
/// nothing (apart from ActualText), so the same code can produce both kinds.
/// </para>
/// Not thread-safe. Memory: the uncompressed content of a page while it is open, the compressed content afterwards.
/// </summary>
internal sealed class ContentWriter
{
    private enum Mark { Tagged, Artifact, Span, Ignored }

    private record struct State(PdfColor Fill, PdfColor Stroke, double LineWidth, PdfFont? Font, double FontSize, (double On, double Off) Dash);

    private readonly PdfPageBuilder _page;
    private readonly ByteBuffer _out = new(8192);
    private readonly Stack<State> _saved = new();
    private readonly Stack<Mark> _marks = new();
    private bool _structural; // inside tagged content or an artifact
    private State _state = new(PdfColor.Black, PdfColor.Black, 1, null, 0, (0, 0)); // what a page starts with
    private bool _closed;

    internal ContentWriter(PdfPageBuilder page) => _page = page;

    private ByteBuffer Out => _closed ? throw new InvalidOperationException("The page is closed. Use PdfPageBuilder.Content to add more to it.") : _out;

    // ------------------------------------------------------------------ graphics state

    /// <summary>Saves colours, line width, dash, clip and transformation (q).</summary>
    public void SaveState() { Out.Ascii("q\n"); _saved.Push(_state); }

    /// <summary>Restores what the matching <see cref="SaveState"/> saved (Q).</summary>
    public void RestoreState()
    {
        if (_saved.Count == 0) throw new InvalidOperationException("RestoreState without SaveState.");
        Out.Ascii("Q\n");
        _state = _saved.Pop();
    }

    public void SetFillColor(PdfColor c)
    {
        if (_state.Fill == c) return;
        Color(c, "rg\n");
        _state.Fill = c;
    }

    public void SetStrokeColor(PdfColor c)
    {
        if (_state.Stroke == c) return;
        Color(c, "RG\n");
        _state.Stroke = c;
    }

    private void Color(PdfColor c, string op) => Out.Num(c.R / 255.0).Num(c.G / 255.0).Num(c.B / 255.0).Ascii(op);

    public void SetLineWidth(double width)
    {
        if (_state.LineWidth == width) return;
        Out.Num(width).Ascii("w\n");
        _state.LineWidth = width;
    }

    /// <summary>Dashed lines; <c>SetDash(0, 0)</c> goes back to solid.</summary>
    public void SetDash(double on, double off)
    {
        if (_state.Dash == (on, off)) return;
        Out.Byte((byte)'[');
        if (on > 0) Out.Num(on).Num(off);
        Out.Ascii("] 0 d\n");
        _state.Dash = (on, off);
    }

    /// <summary>Concatenates a transformation matrix (cm). Use inside SaveState/RestoreState.</summary>
    public void Transform(double a, double b, double c, double d, double e, double f)
        => Out.Num(a).Num(b).Num(c).Num(d).Num(e).Num(f).Ascii("cm\n");

    // ------------------------------------------------------------------ shapes

    public void Rectangle(double x, double y, double width, double height, PaintMode mode = PaintMode.Fill)
    {
        Out.Num(x).Num(y).Num(width).Num(height).Ascii("re\n");
        Paint(mode);
    }

    /// <summary>Rectangle with rounded corners; the radius is limited to half the shorter side.</summary>
    public void RoundedRectangle(double x, double y, double width, double height, double radius, PaintMode mode = PaintMode.Fill)
    {
        RoundedPath(x, y, width, height, radius);
        Paint(mode);
    }

    private void RoundedPath(double x, double y, double w, double h, double r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(Math.Abs(w), Math.Abs(h)) / 2));
        if (r < 0.001) { Out.Num(x).Num(y).Num(w).Num(h).Ascii("re\n"); return; }
        var k = r * 0.4477152498; // distance of the control points from the corner, for a quarter circle
        double x1 = x + w, y1 = y + h;
        Out.Num(x + r).Num(y).Ascii("m\n").Num(x1 - r).Num(y).Ascii("l\n");
        Out.Num(x1 - k).Num(y).Num(x1).Num(y + k).Num(x1).Num(y + r).Ascii("c\n").Num(x1).Num(y1 - r).Ascii("l\n");
        Out.Num(x1).Num(y1 - k).Num(x1 - k).Num(y1).Num(x1 - r).Num(y1).Ascii("c\n").Num(x + r).Num(y1).Ascii("l\n");
        Out.Num(x + k).Num(y1).Num(x).Num(y1 - k).Num(x).Num(y1 - r).Ascii("c\n").Num(x).Num(y + r).Ascii("l\n");
        Out.Num(x).Num(y + k).Num(x + k).Num(y).Num(x + r).Num(y).Ascii("c\nh\n");
    }

    private void Paint(PaintMode mode) => Out.Ascii(mode switch { PaintMode.Fill => "f\n", PaintMode.Stroke => "S\n", _ => "B\n" });

    public void Line(double x1, double y1, double x2, double y2)
        => Out.Num(x1).Num(y1).Ascii("m ").Num(x2).Num(y2).Ascii("l S\n");

    /// <summary>Limits drawing to a rectangle until the surrounding state is restored.</summary>
    public void ClipRectangle(double x, double y, double width, double height)
    {
        NeedSaved();
        Out.Num(x).Num(y).Num(width).Num(height).Ascii("re W n\n");
    }

    /// <summary>Limits drawing to a rounded rectangle until the surrounding state is restored.</summary>
    public void ClipRoundedRectangle(double x, double y, double width, double height, double radius)
    {
        NeedSaved();
        RoundedPath(x, y, width, height, radius);
        Out.Ascii("W n\n");
    }

    private void NeedSaved()
    {
        if (_saved.Count == 0) throw new InvalidOperationException("Clip inside SaveState/RestoreState; a clip cannot be undone otherwise.");
    }

    /// <summary>Draws an image into a rectangle (lower left corner, size in points). The aspect ratio is up to the caller.</summary>
    public void DrawImage(PdfImage image, double x, double y, double width, double height)
    {
        Out.Ascii("q ").Num(width).Ascii("0 0 ").Num(height).Num(x).Num(y).Ascii("cm ").Ascii(image.ResourceName).Ascii(" Do Q\n");
        _page.Use(image);
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// Draws one line of text with its baseline starting at (x, y), in the fill colour. Returns the width in points.
    /// A TAB is drawn as a space and extracts as U+0009.
    /// </summary>
    /// <exception cref="PdfFormException">A character has no glyph (the message names it); nothing has been drawn then.</exception>
    public double ShowText(PdfFont font, double size, double x, double y, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return 0;
        font.RequireGlyphs(text);
        BeginText(font, size, x, y);
        font.WriteHex(_out, text);
        _out.Ascii("> Tj ET\n");
        return font.Width(text, size);
    }

    /// <summary>Draws glyphs by code, see <see cref="PdfFont.Encode"/> and <see cref="PdfFont.TabCode"/>.</summary>
    public void ShowCodes(PdfFont font, double size, double x, double y, ReadOnlySpan<ushort> codes)
    {
        if (codes.Length == 0) return;
        BeginText(font, size, x, y);
        foreach (var c in codes) _out.Hex4(c);
        _out.Ascii("> Tj ET\n");
    }

    private void BeginText(PdfFont font, double size, double x, double y)
    {
        Out.Ascii("BT ");
        if (!ReferenceEquals(_state.Font, font) || _state.FontSize != size)
        {
            Out.Ascii(font.ResourceName).Byte((byte)' ').Num(size).Ascii("Tf ");
            _state.Font = font;
            _state.FontSize = size;
            _page.Use(font);
        }
        Out.Num(x).Num(y).Ascii("Td <");
    }

    // ------------------------------------------------------------------ marked content

    /// <summary>
    /// Starts content that belongs to a structure element. May be called any number of times for the same element, also on
    /// different pages. Tagged content cannot contain other tagged content or artifacts.
    /// </summary>
    public void BeginTagged(StructId element)
    {
        if (!_page.Builder.Tagged) { Ignore(); return; }
        if (_structural) throw new InvalidOperationException("Tagged content cannot be nested. Call EndMarked first.");
        var mcid = _page.Builder.Structure.AddContent(element, _page);
        Out.Ascii(StructureBuilder.Names[(int)_page.Builder.Structure.TypeOf(element)]).Ascii(" <</MCID ").Int(mcid).Ascii(">> BDC\n");
        _marks.Push(Mark.Tagged);
        _structural = true;
    }

    /// <summary>Starts content that assistive technology shall ignore.</summary>
    public void BeginArtifact(ArtifactKind kind = ArtifactKind.Layout)
    {
        if (!_page.Builder.Tagged) { Ignore(); return; }
        if (_structural) throw new InvalidOperationException("An artifact cannot be nested in other marked content. Call EndMarked first.");
        Out.Ascii("/Artifact <</Type /").Ascii(kind.ToString()).Ascii(">> BDC\n");
        _marks.Push(Mark.Artifact);
        _structural = true;
    }

    /// <summary>
    /// Starts content that text extraction shall read as <paramref name="actualText"/> instead of what is drawn. May be nested
    /// in tagged content. Readers support it unevenly; for a TAB, <see cref="PdfFont.TabCode"/> is the alternative.
    /// </summary>
    public void BeginActualText(string actualText)
    {
        ArgumentNullException.ThrowIfNull(actualText);
        Out.Ascii("/Span <</ActualText ").TextString(actualText).Ascii(">> BDC\n");
        _marks.Push(Mark.Span);
    }

    private void Ignore() { _ = Out; _marks.Push(Mark.Ignored); }

    /// <summary>Ends the innermost tagged content, artifact or ActualText.</summary>
    public void EndMarked()
    {
        if (_marks.Count == 0) throw new InvalidOperationException("EndMarked without a matching Begin.");
        var mark = _marks.Pop();
        if (mark != Mark.Ignored) Out.Ascii("EMC\n");
        if (mark is Mark.Tagged or Mark.Artifact) _structural = false;
    }

    internal ReadOnlySpan<byte> Finish()
    {
        if (_saved.Count > 0) throw new InvalidOperationException("A SaveState has no RestoreState on this page.");
        if (_marks.Count > 0) throw new InvalidOperationException("Marked content has not been ended on this page.");
        _closed = true;
        return _out.Written;
    }
}
