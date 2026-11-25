using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>A character the font cannot draw. <paramref name="Index"/> is its position in the string (UTF-16 units).</summary>
internal readonly record struct MissingGlyph(int Index, int CodePoint);

/// <summary>A stretch of text to be drawn with one font, see <see cref="PdfFont.Split"/>.</summary>
internal readonly record struct FontRun(PdfFont Font, string Text);

/// <summary>
/// A TrueType font used in one generated document. Get one from <see cref="PdfBuilder.AddFont(TrueTypeFont)"/>. Remembers which
/// glyphs are drawn, and is embedded as a subset of exactly those (Type0, Identity-H) when the document is saved.
/// <para>
/// Every distinct pair of glyph and text gets its own code, so copied text is exact even when characters share a glyph
/// (space and no-break space, for example). A TAB is drawn as a space that extracts as U+0009. No-break spaces (U+00A0,
/// U+202F) use the space glyph when the font has none of its own. Measuring does not add glyphs to the subset; drawing does.
/// </para>
/// </summary>
internal sealed class PdfFont
{
    private const int NotLoaded = -1;

    private readonly int[] _bmpGlyph = new int[0x10000];  // NotLoaded, or glyph index (0 = missing)
    private readonly ushort[] _bmpCid = new ushort[0x10000]; // 0 = no code yet
    private readonly Dictionary<int, int> _otherGlyph = new();
    private readonly Dictionary<int, ushort> _otherCid = new();
    private readonly Dictionary<(int Glyph, string Text), ushort> _cids = new();
    private readonly List<(int Glyph, string Text)> _used = new() { (0, "") }; // index = CID, 0 = .notdef

    internal PdfFont(TrueTypeFont face, string resourceName, PdfDictionary dictionary)
    {
        Face = face;
        ResourceName = resourceName;
        Dictionary = dictionary;
        Array.Fill(_bmpGlyph, NotLoaded);
    }

    /// <summary>Metrics and style of the font.</summary>
    public TrueTypeFont Face { get; }
    /// <summary>Name in page resources, e.g. "/F1".</summary>
    internal string ResourceName { get; }
    /// <summary>The font object; filled in by <see cref="Finish"/>.</summary>
    internal PdfDictionary Dictionary { get; }

    private int Glyph(int codePoint)
    {
        if (codePoint < 0x10000)
        {
            var g = _bmpGlyph[codePoint];
            return g != NotLoaded ? g : _bmpGlyph[codePoint] = Find(codePoint);
        }
        if (!_otherGlyph.TryGetValue(codePoint, out var other)) _otherGlyph[codePoint] = other = Find(codePoint);
        return other;
    }

    private int Find(int c)
    {
        if (c == '\t') return Face.GlyphIndex(' ');
        if (c < 0x20 || c is >= 0xD800 and <= 0xDFFF) return 0;
        var g = Face.GlyphIndex(c);
        return g == 0 && c is 0xA0 or 0x202F ? Face.GlyphIndex(' ') : g;
    }

    /// <summary>Next code point of <paramref name="text"/> at <paramref name="i"/>, advancing it. An unpaired surrogate is returned as itself.</summary>
    private static int Next(string text, ref int i)
    {
        var c = text[i++];
        return char.IsHighSurrogate(c) && i < text.Length && char.IsLowSurrogate(text[i]) ? char.ConvertToUtf32(c, text[i++]) : c;
    }

    /// <summary>True when the font can draw the code point (TAB included; other control characters never).</summary>
    public bool HasGlyph(int codePoint) => codePoint is >= 0 and <= 0x10FFFF && Glyph(codePoint) != 0;

    /// <summary>True when every character can be drawn.</summary>
    public bool CanDraw(string text)
    {
        for (var i = 0; i < text.Length;) if (Glyph(Next(text, ref i)) == 0) return false;
        return true;
    }

    /// <summary>The characters that cannot be drawn, in order. Line breaks count: split the text into lines first.</summary>
    public IReadOnlyList<MissingGlyph> FindMissing(string text)
    {
        List<MissingGlyph>? list = null;
        for (var i = 0; i < text.Length;)
        {
            var at = i;
            var c = Next(text, ref i);
            if (Glyph(c) == 0) (list ??= new()).Add(new MissingGlyph(at, c));
        }
        return list ?? (IReadOnlyList<MissingGlyph>)Array.Empty<MissingGlyph>();
    }

    /// <summary>The text with every character that cannot be drawn replaced.</summary>
    public string ReplaceMissing(string text, string replacement = "?")
    {
        if (CanDraw(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var at = i;
            if (Glyph(Next(text, ref i)) == 0) sb.Append(replacement);
            else sb.Append(text, at, i - at);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits text into runs, giving each character to the first font that can draw it. Characters no font can draw stay with
    /// the first font, where <see cref="FindMissing"/> finds them.
    /// </summary>
    public static List<FontRun> Split(string text, IReadOnlyList<PdfFont> fonts)
    {
        var runs = new List<FontRun>();
        var start = 0;
        PdfFont? current = null;
        for (var i = 0; i < text.Length;)
        {
            var at = i;
            var c = Next(text, ref i);
            var font = fonts.FirstOrDefault(f => f.Glyph(c) != 0) ?? fonts[0];
            if (ReferenceEquals(font, current)) continue;
            if (current is not null) runs.Add(new FontRun(current, text[start..at]));
            current = font;
            start = at;
        }
        if (current is not null) runs.Add(new FontRun(current, text[start..]));
        return runs;
    }

    /// <summary>Width of the text in points. Characters without a glyph count as .notdef.</summary>
    public double Width(string text, double size) => Width(text.AsSpan(), size);

    /// <inheritdoc cref="Width(string, double)"/>
    public double Width(ReadOnlySpan<char> text, double size)
    {
        long units = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int c = text[i];
            if (char.IsHighSurrogate((char)c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) c = char.ConvertToUtf32((char)c, text[++i]);
            units += Face.AdvanceUnits(Glyph(c));
        }
        return units * size / Face.UnitsPerEm;
    }

    /// <summary>
    /// Code for a glyph that stands for an arbitrary text, e.g. the "fi" ligature (two characters) or a space standing for a
    /// TAB. Draw it with <see cref="ContentWriter.ShowCodes"/>. At most 65,535 distinct pairs per font.
    /// </summary>
    /// <param name="glyph">Glyph index, see <see cref="TrueTypeFont.GlyphIndex"/>.</param>
    /// <param name="text">What text extraction shall see; must not be empty.</param>
    public ushort Encode(int glyph, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (glyph < 0 || glyph >= Face.GlyphCount) throw new ArgumentOutOfRangeException(nameof(glyph));
        if (_cids.TryGetValue((glyph, text), out var cid)) return cid;
        if (_used.Count > 0xFFFF) throw new PdfFormException("A font is used for more than 65,535 different characters in one document.");
        cid = (ushort)_used.Count;
        _used.Add((glyph, text));
        _cids[(glyph, text)] = cid;
        return cid;
    }

    /// <summary>The code of the TAB: looks like a space, extracts as U+0009.</summary>
    public ushort TabCode => Encode(Glyph('\t') is var g && g != 0 ? g : throw new PdfFormException("The font has no space glyph to draw a tab with."), "\t");

    /// <summary>Throws, naming the character, when one has no glyph.</summary>
    internal void RequireGlyphs(string text)
    {
        if (CanDraw(text)) return;
        throw new PdfFormException($"The font {Face.PostScriptName} has no glyph for U+{FindMissing(text)[0].CodePoint:X4}. Use FindMissing, ReplaceMissing or Split before drawing.");
    }

    /// <summary>Writes the codes of the text, which must pass <see cref="RequireGlyphs"/>, as hexadecimal digits.</summary>
    internal void WriteHex(ByteBuffer buffer, string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var c = Next(text, ref i);
            ushort cid;
            if (c < 0x10000)
            {
                cid = _bmpCid[c];
                if (cid == 0) _bmpCid[c] = cid = Encode(Glyph(c), ((char)c).ToString());
            }
            else if (!_otherCid.TryGetValue(c, out cid)) _otherCid[c] = cid = Encode(Glyph(c), char.ConvertFromUtf32(c));
            buffer.Hex4(cid);
        }
    }

    /// <summary>Builds the font objects for what has been drawn so far.</summary>
    internal void Finish(PdfDocument doc)
    {
        var glyphs = _used.Select(u => u.Glyph).ToHashSet();
        byte[] file;
        Func<int, int> newIndex;
        var name = Face.PostScriptName;
        if (Face.MaySubset)
        {
            var (subset, map) = Face.Subset(glyphs);
            file = subset;
            newIndex = g => map[g];
            name = Tag(glyphs) + "+" + name;
        }
        else
        {
            file = Face.Data;
            newIndex = g => g;
        }

        var fontFile = new PdfStream(Array.Empty<byte>());
        fontFile.SetData(file);
        fontFile["/Length1"] = new PdfNumber(file.Length);

        double Milli(double em) => Math.Round(em * 1000);
        double MilliUnits(int units) => Math.Round(units * 1000.0 / Face.UnitsPerEm);
        var desc = new PdfDictionary();
        desc["/Type"] = new PdfName("/FontDescriptor");
        desc["/FontName"] = new PdfName(name);
        desc["/Flags"] = new PdfNumber(4 | (Face.Italic ? 64 : 0)); // symbolic: the codes are private to the subset
        desc["/FontBBox"] = new PdfArray(Face.BBox.Select(v => (PdfObject)new PdfNumber(MilliUnits(v))));
        desc["/ItalicAngle"] = new PdfNumber(Face.ItalicAngle);
        desc["/Ascent"] = new PdfNumber(Milli(Face.AscentEm));
        desc["/Descent"] = new PdfNumber(-Milli(Face.DescentEm));
        desc["/CapHeight"] = new PdfNumber(Milli(Face.CapHeightEm));
        desc["/XHeight"] = new PdfNumber(Milli(Face.XHeightEm));
        desc["/StemV"] = new PdfNumber(Math.Round(50 + Math.Pow(Face.Weight / 65.0, 2)));
        desc["/FontFile2"] = doc.RefFor(fontFile);

        var widths = new PdfArray();
        var toGid = new byte[_used.Count * 2];
        for (var cid = 0; cid < _used.Count; cid++)
        {
            widths.Add(new PdfNumber(Math.Round(Face.AdvanceUnits(_used[cid].Glyph) * 1000.0 / Face.UnitsPerEm, 3)));
            var gid = newIndex(_used[cid].Glyph);
            toGid[cid * 2] = (byte)(gid >> 8);
            toGid[cid * 2 + 1] = (byte)gid;
        }
        var map2 = new PdfStream(Array.Empty<byte>());
        map2.SetData(toGid);

        var info = new PdfDictionary();
        info["/Registry"] = new PdfString(Encoding.ASCII.GetBytes("Adobe"));
        info["/Ordering"] = new PdfString(Encoding.ASCII.GetBytes("Identity"));
        info["/Supplement"] = new PdfNumber(0);
        var cidFont = new PdfDictionary();
        cidFont["/Type"] = new PdfName("/Font");
        cidFont["/Subtype"] = new PdfName("/CIDFontType2");
        cidFont["/BaseFont"] = new PdfName(name);
        cidFont["/CIDSystemInfo"] = info;
        cidFont["/FontDescriptor"] = doc.RefFor(desc);
        cidFont["/DW"] = new PdfNumber(1000);
        cidFont["/W"] = new PdfArray(new PdfNumber(0), widths);
        cidFont["/CIDToGIDMap"] = doc.RefFor(map2);

        var toUnicode = new PdfStream(Array.Empty<byte>());
        toUnicode.SetData(ToUnicode());
        Dictionary["/Type"] = new PdfName("/Font");
        Dictionary["/Subtype"] = new PdfName("/Type0");
        Dictionary["/BaseFont"] = new PdfName(name);
        Dictionary["/Encoding"] = new PdfName("/Identity-H");
        Dictionary["/DescendantFonts"] = new PdfArray(doc.RefFor(cidFont));
        Dictionary["/ToUnicode"] = doc.RefFor(toUnicode);
    }

    /// <summary>Six letters derived from the font and the glyphs, so the same content always gets the same name.</summary>
    private string Tag(IEnumerable<int> glyphs)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Face.Hash);
        foreach (var g in glyphs.Order()) sha.AppendData(new[] { (byte)(g >> 8), (byte)g });
        return new string(sha.GetHashAndReset().Take(6).Select(b => (char)('A' + b % 26)).ToArray());
    }

    private byte[] ToUnicode()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo <</Registry (Adobe) /Ordering (UCS) /Supplement 0>> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (var first = 1; first < _used.Count; first += 100)
        {
            var n = Math.Min(100, _used.Count - first);
            sb.Append(CultureInfo.InvariantCulture, $"{n} beginbfchar\n");
            for (var cid = first; cid < first + n; cid++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<{cid:X4}> <");
                foreach (var ch in _used[cid].Text) sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
