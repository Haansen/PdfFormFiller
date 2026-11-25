using System.IO.Compression;

namespace PdfFormFiller.Core;

/// <summary>Standard stream filters (ISO 32000-1 §7.4). Image codecs are passed through untouched.</summary>
internal static class Filters
{
    /// <summary>Upper bound for a single decoded stream. Stops decompression bombs from exhausting memory.</summary>
    public static long MaxDecodedBytes = 256L * 1024 * 1024;

    /// <summary>Real filter chains have one to three entries.</summary>
    private const int MaxChain = 16;

    private static void Guard(long length, long limit)
    {
        if (length <= limit) return;
        if (limit < MaxDecodedBytes)
            throw new PdfFormException($"An object or cross-reference stream in the PDF expands to more than {limit / (1024 * 1024)} MB, which is out of proportion to the file; refusing to decode it.");
        throw new PdfFormException($"A stream in the PDF expands to more than {limit / (1024 * 1024)} MB; refusing to decode it. Raise PdfFormDocument.MaxDecodedStreamBytes if this file is legitimate.");
    }

    public static byte[] Decode(PdfStream stream) => Decode(stream, MaxDecodedBytes);

    public static byte[] Decode(PdfStream stream, long limit)
    {
        var data = stream.Raw;
        var filters = NamesOf(stream["/Filter"]);
        if (filters.Count > MaxChain) throw new PdfFormException("A stream in the PDF has an unreasonably long filter chain.");
        var parms = ParmsOf(stream["/DecodeParms"], filters.Count);
        for (var i = 0; i < filters.Count; i++)
        {
            data = filters[i] switch
            {
                "/FlateDecode" or "/Fl" => Predictor(FlateDecode(data, limit), parms[i]),
                "/LZWDecode" or "/LZW" => Predictor(LzwDecode(data, parms[i], limit), parms[i]),
                "/ASCIIHexDecode" or "/AHx" => AsciiHexDecode(data),
                "/ASCII85Decode" or "/A85" => Ascii85Decode(data, limit),
                "/RunLengthDecode" or "/RL" => RunLengthDecode(data, limit),
                _ => data, // DCTDecode, JPXDecode, CCITTFaxDecode, JBIG2Decode, Crypt: leave alone
            };
        }
        return data;
    }

    public static List<string> NamesOf(PdfObject? filter)
    {
        var list = new List<string>();
        switch (PdfExtCore.Resolve(filter))
        {
            case PdfName n: list.Add(n.Value); break;
            case PdfArray a:
                foreach (var it in a.Items) if (PdfExtCore.Resolve(it) is PdfName fn) list.Add(fn.Value);
                break;
        }
        return list;
    }

    private static List<PdfDictionary?> ParmsOf(PdfObject? parms, int count)
    {
        var list = new List<PdfDictionary?>();
        var r = PdfExtCore.Resolve(parms);
        if (r is PdfDictionary d) list.Add(d);
        else if (r is PdfArray a) foreach (var it in a.Items) list.Add(PdfExtCore.Resolve(it) as PdfDictionary);
        while (list.Count < count) list.Add(null);
        return list;
    }

    // ------------------------------------------------------------------ Flate

    public static byte[] FlateEncode(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] FlateDecode(byte[] data, long limit)
    {
        if (data.Length == 0) return data;
        var start = 0;
        while (start < data.Length && Lexer.IsWhitespace(data[start])) start++;
        var result = TryInflate(data, start, zlib: true, limit);
        if (result is null || result.Length == 0)
        {
            // corrupt zlib header? try raw deflate, with and without the 2-byte header
            result = TryInflate(data, start, zlib: false, limit) ?? TryInflate(data, start + 2, zlib: false, limit) ?? Array.Empty<byte>();
        }
        return result;
    }

    private static byte[]? TryInflate(byte[] data, int offset, bool zlib, long limit)
    {
        if (offset >= data.Length) return null;
        try
        {
            using var input = new MemoryStream(data, offset, data.Length - offset);
            using Stream z = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buf = new byte[65536];
            try
            {
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    output.Write(buf, 0, n);
                    Guard(output.Length, limit);
                }
            }
            catch (InvalidDataException)
            {
                // keep whatever was decoded before the corruption
                if (output.Length == 0) return null;
            }
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not PdfFormException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Predictor (PNG / TIFF)

    private static byte[] Predictor(byte[] data, PdfDictionary? parms)
    {
        if (parms is null) return data;
        var predictor = parms.Int("/Predictor") ?? 1;
        if (predictor <= 1) return data;
        var colors = parms.Int("/Colors") ?? 1;
        var bpc = parms.Int("/BitsPerComponent") ?? 8;
        var columns = parms.Int("/Columns") ?? 1;
        if (colors is < 1 or > 256 || bpc is < 1 or > 32 || columns < 1 || (long)colors * bpc * columns > int.MaxValue / 2) return data;
        var bpp = (colors * bpc + 7) / 8;
        var rowLen = (colors * bpc * columns + 7) / 8;
        if (rowLen < 1) return data;

        if (predictor == 2)
        {
            if (bpc != 8) return data;
            var rows = data.Length / rowLen;
            for (var r = 0; r < rows; r++)
                for (var i = bpp; i < rowLen; i++)
                    data[r * rowLen + i] += data[r * rowLen + i - bpp];
            return data;
        }

        // PNG predictors: each row prefixed with a filter-type byte
        var nrows = data.Length / (rowLen + 1);
        if (nrows == 0) return Array.Empty<byte>();
        var output = new byte[nrows * rowLen];
        var prev = new byte[rowLen];
        for (var r = 0; r < nrows; r++)
        {
            var ft = data[r * (rowLen + 1)];
            var src = r * (rowLen + 1) + 1;
            var dst = r * rowLen;
            for (var i = 0; i < rowLen; i++)
            {
                var raw = data[src + i];
                var left = i >= bpp ? output[dst + i - bpp] : (byte)0;
                var up = prev[i];
                var upLeft = i >= bpp ? prev[i - bpp] : (byte)0;
                byte val = ft switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + ((left + up) >> 1)),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => raw,
                };
                output[dst + i] = val;
            }
            Array.Copy(output, dst, prev, 0, rowLen);
        }
        return output;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    // ------------------------------------------------------------------ LZW

    private static byte[] LzwDecode(byte[] data, PdfDictionary? parms, long limit)
    {
        var early = (parms?.Int("/EarlyChange") ?? 1) != 0 ? 1 : 0;
        var output = new MemoryStream();
        var table = new List<byte[]>();
        void Reset()
        {
            table.Clear();
            for (var i = 0; i < 256; i++) table.Add(new[] { (byte)i });
            table.Add(Array.Empty<byte>()); // 256 clear
            table.Add(Array.Empty<byte>()); // 257 eod
        }
        Reset();
        var codeLen = 9;
        byte[]? prev = null;
        long bitBuf = 0; var bitCnt = 0; var pos = 0;
        while (true)
        {
            while (bitCnt < codeLen && pos < data.Length) { bitBuf = (bitBuf << 8) | data[pos++]; bitCnt += 8; }
            if (bitCnt < codeLen) break;
            var code = (int)((bitBuf >> (bitCnt - codeLen)) & ((1 << codeLen) - 1));
            bitCnt -= codeLen;
            if (code == 256) { Reset(); codeLen = 9; prev = null; continue; }
            if (code == 257) break;
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else if (prev is not null) { entry = new byte[prev.Length + 1]; prev.CopyTo(entry, 0); entry[^1] = prev[0]; }
            else break;
            output.Write(entry, 0, entry.Length);
            Guard(output.Length, limit);
            if (prev is not null && table.Count < 4096)
            {
                var ne = new byte[prev.Length + 1];
                prev.CopyTo(ne, 0); ne[^1] = entry[0];
                table.Add(ne);
            }
            prev = entry;
            if (table.Count + early >= (1 << codeLen) && codeLen < 12) codeLen++;
        }
        return output.ToArray();
    }

    // ------------------------------------------------------------------ ASCII filters

    private static byte[] AsciiHexDecode(byte[] data)
    {
        var ms = new MemoryStream();
        var hi = -1;
        foreach (var c in data)
        {
            if (c == (byte)'>') break;
            if (!Lexer.IsHex(c)) continue;
            var v = Lexer.HexVal(c);
            if (hi < 0) hi = v; else { ms.WriteByte((byte)((hi << 4) | v)); hi = -1; }
        }
        if (hi >= 0) ms.WriteByte((byte)(hi << 4));
        return ms.ToArray();
    }

    private static byte[] Ascii85Decode(byte[] data, long limit)
    {
        var ms = new MemoryStream();
        var tuple = new uint[5];
        var n = 0;
        var i = 0;
        if (data.Length >= 2 && data[0] == (byte)'<' && data[1] == (byte)'~') i = 2;
        for (; i < data.Length; i++)
        {
            var c = data[i];
            if (Lexer.IsWhitespace(c)) continue;
            if (c == (byte)'~') break;
            if (c == (byte)'z' && n == 0) { ms.Write(new byte[4], 0, 4); Guard(ms.Length, limit); continue; }
            if (c < (byte)'!' || c > (byte)'u') continue;
            tuple[n++] = (uint)(c - '!');
            if (n == 5)
            {
                uint v = 0;
                foreach (var t in tuple) v = v * 85 + t;
                ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
                n = 0;
            }
        }
        if (n > 0)
        {
            for (var k = n; k < 5; k++) tuple[k] = 84;
            uint v = 0;
            foreach (var t in tuple) v = v * 85 + t;
            var bytes = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            ms.Write(bytes, 0, n - 1);
        }
        return ms.ToArray();
    }

    private static byte[] RunLengthDecode(byte[] data, long limit)
    {
        var ms = new MemoryStream();
        var i = 0;
        while (i < data.Length)
        {
            int len = data[i++];
            if (len == 128) break;
            if (len < 128)
            {
                var cnt = Math.Min(len + 1, data.Length - i);
                ms.Write(data, i, cnt); i += cnt;
            }
            else
            {
                if (i >= data.Length) break;
                var b = data[i++];
                for (var k = 0; k < 257 - len; k++) ms.WriteByte(b);
                Guard(ms.Length, limit);
            }
        }
        return ms.ToArray();
    }
}
