using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>What the font's licence bits (OS/2 fsType) say about embedding.</summary>
internal enum FontEmbedding
{
    /// <summary>No restriction.</summary>
    Installable,
    /// <summary>May be embedded in documents that can be edited.</summary>
    Editable,
    /// <summary>May be embedded for viewing and printing.</summary>
    PreviewAndPrint,
}

/// <summary>
/// A TrueType font program (glyf outlines): metrics, character map, licence bits and subsetting. Immutable and safe to share
/// between documents and threads once constructed.
/// <para>
/// The constructor checks every table it relies on and throws <see cref="PdfFormException"/> for anything unusable: truncated
/// or inconsistent files, OpenType/CFF ("OTTO"), collections (TTC), WOFF, and fonts whose licence forbids embedding.
/// All metrics named "...Em" are fractions of the font size; multiply by the size in points.
/// </para>
/// Not supported: kerning, ligature substitution, complex scripts and right-to-left layout. Glyphs are mapped one code point at a time.
/// </summary>
internal sealed class TrueTypeFont
{
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new();
    private readonly int _hmtx, _numH, _loca, _glyf, _glyfLength;
    private readonly bool _longLoca;

    // character map: sorted segments. Delta segments map c to (c + Delta); Array segments look the glyph up in the file.
    private readonly record struct Segment(int Start, int End, int Delta, int ArrayOffset, bool Wrap);
    private readonly Segment[] _segments = Array.Empty<Segment>();
    private readonly bool _symbolic;

    /// <summary>The font file.</summary>
    public byte[] Data { get; }
    /// <summary>SHA-256 of <see cref="Data"/>.</summary>
    public byte[] Hash { get; }
    /// <summary>PostScript name, e.g. "LiberationSans-Bold"; only characters that are safe in a PDF name.</summary>
    public string PostScriptName { get; }
    /// <summary>Family name, e.g. "Liberation Sans".</summary>
    public string FamilyName { get; }
    public int UnitsPerEm { get; }
    public int GlyphCount { get; }
    public bool Bold { get; }
    public bool Italic { get; }
    /// <summary>100-900; 400 is regular, 700 bold.</summary>
    public int Weight { get; }
    public double ItalicAngle { get; }
    /// <summary>Height above the baseline, positive (hhea).</summary>
    public double AscentEm { get; }
    /// <summary>Depth below the baseline, positive (hhea).</summary>
    public double DescentEm { get; }
    public double LineGapEm { get; }
    /// <summary>Recommended distance between baselines.</summary>
    public double LineHeightEm => AscentEm + DescentEm + LineGapEm;
    public double CapHeightEm { get; }
    public double XHeightEm { get; }
    /// <summary>Centre of the underline relative to the baseline, normally negative.</summary>
    public double UnderlinePositionEm { get; }
    public double UnderlineThicknessEm { get; }
    /// <summary>Font bounding box (xMin, yMin, xMax, yMax) in font units.</summary>
    public IReadOnlyList<int> BBox { get; }
    public FontEmbedding Embedding { get; }
    /// <summary>False when the licence asks for the font to be embedded whole.</summary>
    public bool MaySubset { get; }

    public TrueTypeFont(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Hash = SHA256.HashData(data);
        if (data.Length < 12) throw Bad("the file is too short");
        switch (U32(0))
        {
            case 0x00010000 or 0x74727565: break; // 1.0, 'true'
            case 0x4F54544F: throw new PdfFormException("The font has OpenType/CFF outlines, which are not supported. Use a TrueType version (.ttf) of it.");
            case 0x74746366: throw new PdfFormException("The font file is a TrueType collection (.ttc), which is not supported. Extract the wanted font to a .ttf file.");
            case 0x774F4646 or 0x774F4632: throw new PdfFormException("The font file is a WOFF web font, which is not supported. Use a TrueType version (.ttf) of it.");
            default: throw Bad("it is not a TrueType file");
        }
        var count = U16(4);
        for (var i = 0; i < count; i++)
        {
            var rec = 12 + i * 16;
            Need(rec, 16);
            var off = U32(rec + 8);
            var len = U32(rec + 12);
            if (off > data.Length || len > data.Length - off) continue; // only fatal when the table is needed
            _tables.TryAdd(Encoding.Latin1.GetString(data, rec, 4), ((int)off, (int)len));
        }

        var head = Table("head", 54);
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm is < 16 or > 16384) throw Bad("unitsPerEm is out of range");
        BBox = new[] { (int)S16(head + 36), S16(head + 38), S16(head + 40), S16(head + 42) };
        var macStyle = U16(head + 44);
        _longLoca = S16(head + 50) switch { 0 => false, 1 => true, _ => throw Bad("the loca format is unknown") };
        GlyphCount = U16(Table("maxp", 6) + 4);
        if (GlyphCount == 0) throw Bad("it has no glyphs");

        var hhea = Table("hhea", 36);
        AscentEm = Em(S16(hhea + 4));
        DescentEm = Math.Abs(Em(S16(hhea + 6)));
        LineGapEm = Math.Max(0, Em(S16(hhea + 8)));
        _numH = U16(hhea + 34);
        if (_numH == 0 || _numH > GlyphCount) throw Bad("the number of horizontal metrics is wrong");
        _hmtx = Table("hmtx", _numH * 4);
        _loca = Table("loca", (GlyphCount + 1) * (_longLoca ? 4 : 2));
        _glyf = Table("glyf", 0);
        _glyfLength = _tables["glyf"].Length;

        Weight = (macStyle & 1) != 0 ? 700 : 400;
        Bold = (macStyle & 1) != 0;
        Italic = (macStyle & 2) != 0;
        Embedding = FontEmbedding.Installable;
        MaySubset = true;
        CapHeightEm = AscentEm * 0.78;
        XHeightEm = AscentEm * 0.57;
        if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 68)
        {
            var w = U16(os2.Offset + 4);
            Weight = Math.Clamp(w < 10 ? w * 100 : w, 100, 1000); // some old fonts count 1-9
            var fsType = U16(os2.Offset + 8);
            // the least restrictive of the usage bits applies
            Embedding = (fsType & 0xF) switch
            {
                0 => FontEmbedding.Installable,
                var bits when (bits & 8) != 0 => FontEmbedding.Editable,
                var bits when (bits & 4) != 0 => FontEmbedding.PreviewAndPrint,
                var bits when (bits & 2) != 0 => throw new PdfFormException("The font's licence does not permit embedding it in documents (restricted licence, OS/2 fsType). Use another font."),
                _ => FontEmbedding.Installable,
            };
            if ((fsType & 0x200) != 0) throw new PdfFormException("The font's licence only permits embedding bitmaps (OS/2 fsType). Use another font.");
            MaySubset = (fsType & 0x100) == 0;
            var selection = U16(os2.Offset + 62);
            Italic |= (selection & 1) != 0;
            Bold |= (selection & 32) != 0;
            if (U16(os2.Offset) >= 2 && os2.Length >= 90)
            {
                if (S16(os2.Offset + 86) > 0) XHeightEm = Em(S16(os2.Offset + 86));
                if (S16(os2.Offset + 88) > 0) CapHeightEm = Em(S16(os2.Offset + 88));
            }
        }
        UnderlinePositionEm = -0.1;
        UnderlineThicknessEm = 0.05;
        if (_tables.TryGetValue("post", out var post) && post.Length >= 12)
        {
            ItalicAngle = (int)U32(post.Offset + 4) / 65536.0;
            UnderlinePositionEm = Em(S16(post.Offset + 8));
            if (S16(post.Offset + 10) > 0) UnderlineThicknessEm = Em(S16(post.Offset + 10));
        }
        var names = ReadNames();
        FamilyName = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1) ?? "Font";
        var ps = new string((names.GetValueOrDefault(6) ?? FamilyName).Where(c => c is > ' ' and < '\x7F' && !"[](){}<>/%#".Contains(c)).Take(63).ToArray());
        PostScriptName = ps.Length > 0 ? ps : "Font";
        (_segments, _symbolic) = ReadCmap();
    }

    private static PdfFormException Bad(string why) => new($"The font file is damaged or not usable: {why}.");
    private double Em(int units) => (double)units / UnitsPerEm;

    private void Need(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Data.Length - length) throw Bad("it ends too early");
    }

    private int U16(int o) { Need(o, 2); return BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(o)); }
    private short S16(int o) { Need(o, 2); return BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(o)); }
    private uint U32(int o) { Need(o, 4); return BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(o)); }

    private int Table(string tag, int minLength)
    {
        if (!_tables.TryGetValue(tag, out var t)) throw Bad($"the {tag} table is missing or outside the file");
        if (t.Length < minLength) throw Bad($"the {tag} table is too short");
        return t.Offset;
    }

    private Dictionary<int, string> ReadNames()
    {
        var result = new Dictionary<int, string>();
        if (!_tables.TryGetValue("name", out var t) || t.Length < 6) return result;
        var count = U16(t.Offset + 2);
        var storage = U16(t.Offset + 4);
        for (var i = 0; i < count && 6 + (i + 1) * 12 <= t.Length; i++)
        {
            var rec = t.Offset + 6 + i * 12;
            var id = U16(rec + 6);
            if (id is not (1 or 6 or 16)) continue;
            var platform = U16(rec);
            int len = U16(rec + 8), off = storage + U16(rec + 10);
            if (off + len > t.Length) continue;
            var s = platform == 1 ? Encoding.Latin1.GetString(Data, t.Offset + off, len) : Encoding.BigEndianUnicode.GetString(Data, t.Offset + off, len);
            var english = platform == 3 && U16(rec + 4) == 0x409;
            if (english || !result.ContainsKey(id)) result[id] = s;
        }
        return result;
    }

    private (Segment[], bool) ReadCmap()
    {
        var cmap = Table("cmap", 4);
        var length = _tables["cmap"].Length;
        var n = U16(cmap + 2);
        var candidates = new List<(int Score, int Offset, bool Symbol)>();
        for (var i = 0; i < n && 4 + (i + 1) * 8 <= length; i++)
        {
            var rec = cmap + 4 + i * 8;
            int platform = U16(rec), encoding = U16(rec + 2);
            var off = U32(rec + 4);
            if (off > length - 4) continue;
            var format = U16(cmap + (int)off);
            if (format is not (4 or 12)) continue;
            var score = (platform, encoding) switch
            {
                (3, 10) => 6, (0, 4 or 6) => 5, (3, 1) => 4, (0, not 5) => 3, (3, 0) => 1, _ => 0,
            };
            if (score > 0) candidates.Add((score, cmap + (int)off, (platform, encoding) == (3, 0)));
        }
        if (candidates.Count == 0) throw Bad("it has no Unicode character map (cmap format 4 or 12)");
        var best = candidates.OrderByDescending(c => c.Score).First();
        var end = cmap + length;
        var segments = U16(best.Offset) == 4 ? Format4(best.Offset, end) : Format12(best.Offset, end);
        return (segments, best.Symbol);
    }

    private Segment[] Format4(int o, int end)
    {
        var segs = U16(o + 6) / 2;
        int endP = o + 14, startP = endP + segs * 2 + 2, deltaP = startP + segs * 2, rangeP = deltaP + segs * 2;
        if (rangeP + segs * 2 > end) throw Bad("the cmap table is too short");
        var list = new List<Segment>(segs);
        for (var s = 0; s < segs; s++)
        {
            int last = U16(endP + s * 2), first = U16(startP + s * 2), rangeOff = U16(rangeP + s * 2);
            if (first > last || first == 0xFFFF) continue;
            list.Add(new Segment(first, Math.Min(last, 0xFFFE), S16(deltaP + s * 2), rangeOff == 0 ? 0 : rangeP + s * 2 + rangeOff, true));
        }
        return Sorted(list);
    }

    private Segment[] Format12(int o, int end)
    {
        if (o + 16 > end) throw Bad("the cmap table is too short");
        var groups = U32(o + 12);
        if (groups > (uint)(end - o - 16) / 12) throw Bad("the cmap table is too short");
        var list = new List<Segment>((int)groups);
        for (var g = 0; g < groups; g++)
        {
            var rec = o + 16 + g * 12;
            uint first = U32(rec), last = U32(rec + 4), glyph = U32(rec + 8);
            if (first > last || last > 0x10FFFF || glyph > 0xFFFF) continue;
            list.Add(new Segment((int)first, (int)last, (int)glyph - (int)first, 0, false));
        }
        return Sorted(list);
    }

    private static Segment[] Sorted(List<Segment> list)
    {
        var ordered = list.OrderBy(s => s.Start).ToArray(); // stable: the first of overlapping segments is found first
        return ordered;
    }

    /// <summary>Glyph index of a Unicode code point, 0 when the font has no glyph for it.</summary>
    public int GlyphIndex(int codePoint)
    {
        var g = Lookup(codePoint);
        if (g == 0 && _symbolic && codePoint is > 0 and <= 0xFF) g = Lookup(0xF000 + codePoint);
        return g;
    }

    private int Lookup(int c)
    {
        int lo = 0, hi = _segments.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var s = _segments[mid];
            if (c < s.Start) hi = mid - 1;
            else if (c > s.End) lo = mid + 1;
            else
            {
                int glyph;
                if (s.ArrayOffset == 0) glyph = s.Wrap ? (c + s.Delta) & 0xFFFF : c + s.Delta;
                else
                {
                    var at = (long)s.ArrayOffset + (c - s.Start) * 2L;
                    if (at + 2 > Data.Length) return 0;
                    glyph = U16((int)at);
                    if (glyph != 0) glyph = (glyph + s.Delta) & 0xFFFF;
                }
                return glyph > 0 && glyph < GlyphCount ? glyph : 0;
            }
        }
        return 0;
    }

    /// <summary>True when the font can draw the code point.</summary>
    public bool HasGlyph(int codePoint) => GlyphIndex(codePoint) != 0;

    /// <summary>Advance width of a glyph in font units.</summary>
    public int AdvanceUnits(int glyph)
    {
        if (glyph < 0 || glyph >= GlyphCount) throw new ArgumentOutOfRangeException(nameof(glyph));
        return U16(_hmtx + Math.Min(glyph, _numH - 1) * 4);
    }

    private short LeftSideBearing(int glyph)
    {
        if (glyph < _numH) return S16(_hmtx + glyph * 4 + 2);
        var at = _numH * 4 + (glyph - _numH) * 2;
        return at + 2 <= _tables["hmtx"].Length ? S16(_hmtx + at) : (short)0;
    }

    private (int Offset, int Length) Outline(int glyph)
    {
        long a = _longLoca ? U32(_loca + glyph * 4) : U16(_loca + glyph * 2) * 2L;
        long b = _longLoca ? U32(_loca + glyph * 4 + 4) : U16(_loca + glyph * 2 + 2) * 2L;
        if (a == b) return (0, 0); // blank
        if (a > b || b > _glyfLength || b - a < 10) throw Bad($"the outline of glyph {glyph} is outside the glyf table");
        return (_glyf + (int)a, (int)(b - a));
    }

    /// <summary>Offsets (within the outline) of the glyph indices a composite glyph refers to; empty for simple glyphs.</summary>
    private List<int> Components(int offset, int length)
    {
        var list = new List<int>();
        if (length == 0 || S16(offset) >= 0) return list;
        var p = 10;
        while (true)
        {
            if (p + 4 > length) throw Bad("a composite glyph is truncated");
            var flags = U16(offset + p);
            list.Add(p + 2);
            p += 4 + ((flags & 1) != 0 ? 4 : 2) + ((flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0);
            if (p > length) throw Bad("a composite glyph is truncated");
            if ((flags & 0x20) == 0) return list;
        }
    }

    /// <summary>
    /// Builds a font file with only the given glyphs, renumbered from 0 (.notdef) upwards, plus whatever composite glyphs are
    /// made of. Returns the file and the new glyph index for each requested old one. Deterministic. Throws
    /// <see cref="PdfFormException"/> when an outline that is needed is damaged.
    /// </summary>
    public (byte[] File, Dictionary<int, int> NewIndex) Subset(IEnumerable<int> glyphs)
    {
        var order = new List<int> { 0 };
        var map = new Dictionary<int, int> { [0] = 0 };
        foreach (var g in glyphs.Order())
        {
            if (g < 0 || g >= GlyphCount) throw new ArgumentOutOfRangeException(nameof(glyphs));
            if (map.TryAdd(g, order.Count)) order.Add(g);
        }
        using var glyf = new MemoryStream();
        var loca = new List<uint>();
        for (var i = 0; i < order.Count; i++) // grows while composite glyphs pull in their parts
        {
            loca.Add((uint)glyf.Length);
            var (offset, length) = Outline(order[i]);
            if (length == 0) continue;
            var outline = Data.AsSpan(offset, length).ToArray();
            foreach (var at in Components(offset, length))
            {
                var part = U16(offset + at);
                if (part >= GlyphCount) throw Bad("a composite glyph refers to a glyph that does not exist");
                if (map.TryAdd(part, order.Count)) order.Add(part);
                BinaryPrimitives.WriteUInt16BigEndian(outline.AsSpan(at), (ushort)map[part]);
            }
            glyf.Write(outline);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        loca.Add((uint)glyf.Length);

        var n = order.Count;
        var hmtx = new byte[n * 4];
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4), (ushort)AdvanceUnits(order[i]));
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(i * 4 + 2), LeftSideBearing(order[i]));
        }
        var locaBytes = new byte[loca.Count * 4];
        for (var i = 0; i < loca.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(i * 4), loca[i]);
        var head = Copy("head", 54);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        var hhea = Copy("hhea", 36);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)n);
        var maxp = Copy("maxp", null);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)n);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head, ["hhea"] = hhea, ["maxp"] = maxp, ["hmtx"] = hmtx, ["loca"] = locaBytes, ["glyf"] = glyf.ToArray(),
        };
        foreach (var tag in new[] { "cvt ", "fpgm", "prep" })
            if (_tables.ContainsKey(tag)) tables[tag] = Copy(tag, null);
        return (Assemble(tables), map);
    }

    private byte[] Copy(string tag, int? length)
    {
        var (o, l) = _tables[tag];
        return Data.AsSpan(o, length ?? l).ToArray();
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var n = tables.Count;
        var total = 12 + n * 16 + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var f = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(f, 0x00010000);
        var log = (int)Math.Log2(n);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(6), (ushort)(16 << log));
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(10), (ushort)(n * 16 - (16 << log)));
        int rec = 12, pos = 12 + n * 16, headAt = 0;
        foreach (var (tag, bytes) in tables)
        {
            bytes.CopyTo(f, pos);
            Encoding.ASCII.GetBytes(tag, f.AsSpan(rec));
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(rec + 4), Checksum(f.AsSpan(pos, (bytes.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(rec + 8), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(rec + 12), (uint)bytes.Length);
            if (tag == "head") headAt = pos;
            rec += 16;
            pos += (bytes.Length + 3) & ~3;
        }
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(headAt + 8), unchecked(0xB1B0AFBA - Checksum(f)));
        return f;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        return sum;
    }
}
