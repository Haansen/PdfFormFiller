using System.Buffers.Binary;

namespace PdfFormFiller.Core;

/// <summary>Minimal TrueType reader: unitsPerEm, advance widths and the Unicode cmap. Enough to measure text.</summary>
internal sealed class TrueTypeMetrics
{
    private readonly ushort[] _advance;
    private readonly Dictionary<int, int> _cmap = new();
    public int UnitsPerEm { get; }
    public byte[] Data { get; }
    public int GlyphCount { get; }
    public short Ascender { get; }
    public short Descender { get; }
    public short[] BBox { get; } = new short[4];

    public TrueTypeMetrics(byte[] data)
    {
        Data = data;
        var tables = new Dictionary<string, (int off, int len)>();
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(data, rec, 4);
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 8));
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 12));
            tables[tag] = (off, len);
        }
        var head = tables["head"].off;
        UnitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(head + 18));
        for (var i = 0; i < 4; i++) BBox[i] = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head + 36 + i * 2));
        GlyphCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(tables["maxp"].off + 4));
        var hhea = tables["hhea"].off;
        Ascender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea + 4));
        Descender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea + 6));
        var numH = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(hhea + 34));
        var hmtx = tables["hmtx"].off;
        _advance = new ushort[numH];
        for (var i = 0; i < numH; i++) _advance[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(hmtx + i * 4));
        ParseCmap(data, tables["cmap"].off);
    }

    private void ParseCmap(byte[] data, int cmap)
    {
        var n = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cmap + 2));
        var best = -1; var bestScore = -1;
        for (var i = 0; i < n; i++)
        {
            var rec = cmap + 4 + i * 8;
            var platform = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rec));
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rec + 2));
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 4));
            var score = (platform, encoding) switch { (3, 1) => 3, (0, _) => 2, (3, 0) => 1, _ => 0 };
            if (score > bestScore) { bestScore = score; best = cmap + off; }
        }
        if (best < 0) return;
        var format = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(best));
        if (format != 4) return;
        var segX2 = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(best + 6));
        var segs = segX2 / 2;
        var endP = best + 14; var startP = endP + segX2 + 2; var deltaP = startP + segX2; var rangeP = deltaP + segX2;
        for (var s = 0; s < segs; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(endP + s * 2));
            int start = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(startP + s * 2));
            int delta = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(deltaP + s * 2));
            int rangeOff = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rangeP + s * 2));
            if (start == 0xFFFF) continue;
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOff == 0) glyph = (c + delta) & 0xFFFF;
                else
                {
                    var gp = rangeP + s * 2 + rangeOff + (c - start) * 2;
                    if (gp + 1 >= data.Length) continue;
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(gp));
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0) _cmap[c] = glyph;
            }
        }
    }

    public bool HasGlyph(char c) => _cmap.ContainsKey(c);

    /// <summary>Glyph index for a character; falls back to '?' and then .notdef.</summary>
    public int GlyphOf(char c) => _cmap.TryGetValue(c, out var g) ? g : _cmap.TryGetValue('?', out var q) ? q : 0;

    /// <summary>Advance of a glyph in 1/1000 em, as PDF width arrays expect.</summary>
    public int GlyphWidth1000(int glyph)
        => _advance.Length == 0 ? 500 : (int)Math.Round(_advance[Math.Min(glyph, _advance.Length - 1)] * 1000.0 / UnitsPerEm);

    /// <summary>Character → glyph pairs, lowest character first.</summary>
    public IEnumerable<KeyValuePair<int, int>> CharToGlyph => _cmap.OrderBy(kv => kv.Key);

    /// <summary>Advance width of a character in em units (0..1 ≈ typical).</summary>
    public double Advance(char c)
    {
        if (!_cmap.TryGetValue(c, out var g)) g = _cmap.TryGetValue('?', out var q) ? q : 0;
        var idx = Math.Min(g, _advance.Length - 1);
        return _advance.Length == 0 ? 0.5 : (double)_advance[idx] / UnitsPerEm;
    }

    public double Width(string s)
    {
        double w = 0;
        foreach (var c in s) w += Advance(c);
        return w;
    }
}
