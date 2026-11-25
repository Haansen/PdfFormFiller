using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfFormFiller.Core;

/// <summary>
/// A JPEG or PNG image used in a generated document. Get one from <see cref="PdfBuilder.AddImage"/>, which keeps a single
/// copy of identical images. JPEG data is passed through unchanged (metadata such as EXIF included; EXIF orientation is not
/// applied). PNG colour profiles and gamma are ignored.
/// </summary>
internal sealed class PdfImage
{
    private PdfImage(int width, int height, PdfStream stream, PdfStream? mask)
    {
        Width = width; Height = height; Stream = stream; Mask = mask;
        stream["/Type"] = new PdfName("/XObject");
        stream["/Subtype"] = new PdfName("/Image");
        stream["/Width"] = new PdfNumber(width);
        stream["/Height"] = new PdfNumber(height);
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }
    /// <summary>Height in pixels.</summary>
    public int Height { get; }
    /// <summary>Horizontal resolution stated in the file, dots per inch; null when not stated.</summary>
    public double? DpiX { get; private set; }
    /// <summary>Vertical resolution stated in the file, dots per inch; null when not stated.</summary>
    public double? DpiY { get; private set; }
    /// <summary>True when parts of the image are transparent.</summary>
    public bool HasTransparency => Mask is not null;

    internal PdfStream Stream { get; }
    internal PdfStream? Mask { get; }
    internal string ResourceName { get; set; } = "";

    /// <summary>
    /// Reads a JPEG or PNG. Throws <see cref="PdfFormException"/> for other formats, malformed files, JPEG kinds PDF cannot
    /// show (arithmetic coding, lossless, 12 bits) and images larger than <see cref="PdfFormDocument.MaxDecodedStreamBytes"/>
    /// when decoded.
    /// </summary>
    public static PdfImage Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8) return LoadJpeg(data);
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(PngSignature)) return LoadPng(data);
        throw new PdfFormException("The image is neither a JPEG nor a PNG file.");
    }

    private const int MaxSide = 1_000_000;

    private static void CheckSize(long width, long height, long bitsPerPixel)
    {
        if (width < 1 || height < 1) throw new PdfFormException("The image has no pixels.");
        if (width > MaxSide || height > MaxSide) throw new PdfFormException($"The image is {width} x {height} pixels; the largest side allowed is {MaxSide}.");
        if (((width * bitsPerPixel + 7) / 8 + 1) * height > Math.Min(Filters.MaxDecodedBytes, Array.MaxLength))
            throw new PdfFormException($"The image is {width} x {height} pixels, more than the limit of {Filters.MaxDecodedBytes / (1024 * 1024)} MB decoded. Scale it down, or raise PdfFormDocument.MaxDecodedStreamBytes.");
    }

    // ------------------------------------------------------------------ JPEG

    private static PdfImage LoadJpeg(byte[] d)
    {
        static PdfFormException Bad(string why) => new($"The JPEG image is damaged or not supported: {why}.");
        int width = 0, height = 0, components = 0;
        var adobe = false;
        (double X, double Y)? dpi = null;
        var p = 2;
        while (true)
        {
            if (p >= d.Length || d[p] != 0xFF) throw Bad("a marker is missing");
            while (p < d.Length && d[p] == 0xFF) p++; // fill bytes
            if (p >= d.Length) throw Bad("it ends too early");
            var marker = d[p++];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue;
            if (marker is 0xD8 or 0xD9 or 0x00) throw Bad("the markers are out of order");
            if (p + 2 > d.Length) throw Bad("it ends too early");
            var len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
            if (len < 2 || p + len > d.Length) throw Bad("a segment is truncated");
            var seg = d.AsSpan(p + 2, len - 2);
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    if (components != 0) throw Bad("it has several frames");
                    if (seg.Length < 6) throw Bad("the frame header is truncated");
                    if (seg[0] != 8) throw Bad($"{seg[0]} bits per sample");
                    height = BinaryPrimitives.ReadUInt16BigEndian(seg[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(seg[3..]);
                    components = seg[5];
                    if (components is not (1 or 3 or 4)) throw Bad($"{components} colour components");
                    if (seg.Length != 6 + components * 3) throw Bad("the frame header is truncated");
                    CheckSize(width, height, components * 8);
                    break;
                case 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    throw Bad("lossless, hierarchical and arithmetic-coded JPEG cannot be shown in PDF");
                case 0xE0 when seg.Length >= 12 && seg[..5].SequenceEqual("JFIF\0"u8):
                    double x = BinaryPrimitives.ReadUInt16BigEndian(seg[8..]), y = BinaryPrimitives.ReadUInt16BigEndian(seg[10..]);
                    if (x > 0 && y > 0 && seg[7] is 1 or 2) dpi = seg[7] == 1 ? (x, y) : (x * 2.54, y * 2.54);
                    break;
                case 0xEE when seg.Length >= 12 && seg[..5].SequenceEqual("Adobe"u8):
                    adobe = true;
                    break;
            }
            p += len;
            if (marker == 0xDA) break;
        }
        if (components == 0) throw Bad("the frame header is missing");
        var eoi = d.AsSpan(p).LastIndexOf(new byte[] { 0xFF, 0xD9 });
        if (eoi < 1) throw Bad("the image data is truncated");

        var stream = new PdfStream(d.AsSpan(0, p + eoi + 2).ToArray()); // anything after the end marker is left out
        stream["/Filter"] = new PdfName("/DCTDecode");
        stream["/ColorSpace"] = new PdfName(components switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => "/DeviceCMYK" });
        stream["/BitsPerComponent"] = new PdfNumber(8);
        if (components == 4 && adobe) // Adobe writes CMYK inverted
            stream["/Decode"] = new PdfArray(Enumerable.Range(0, 8).Select(i => (PdfObject)new PdfNumber(1 - i % 2)));
        return new PdfImage(width, height, stream, null) { DpiX = dpi?.X, DpiY = dpi?.Y };
    }

    // ------------------------------------------------------------------ PNG

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static PdfFormException BadPng(string why) => new($"The PNG image is damaged or not supported: {why}.");

    private static PdfImage LoadPng(byte[] d)
    {
        int width = 0, height = 0, depth = 0, type = -1;
        var interlaced = false;
        byte[]? palette = null, trns = null;
        double? dpiX = null, dpiY = null;
        var idat = new MemoryStream();
        var ended = false;
        var p = 8;
        while (!ended)
        {
            if (p + 12 > d.Length) throw BadPng("it ends too early");
            var len = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            if (len > d.Length - p - 12) throw BadPng("a chunk is truncated");
            var body = d.AsSpan(p + 8, (int)len);
            var name = System.Text.Encoding.Latin1.GetString(d, p + 4, 4);
            if (Crc(d.AsSpan(p + 4, (int)len + 4)) != BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 8 + (int)len))) throw BadPng($"the {name} chunk has a wrong checksum");
            if (type < 0 && name != "IHDR") throw BadPng("the header is missing");
            switch (name)
            {
                case "IHDR":
                    if (type >= 0 || len != 13) throw BadPng("the header is wrong");
                    var (w, h) = (BinaryPrimitives.ReadUInt32BigEndian(body), BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                    if (w > int.MaxValue || h > int.MaxValue) throw BadPng("the size is wrong");
                    (width, height, depth, type) = ((int)w, (int)h, body[8], body[9]);
                    var valid = type switch
                    {
                        0 => depth is 1 or 2 or 4 or 8 or 16,
                        3 => depth is 1 or 2 or 4 or 8,
                        2 or 4 or 6 => depth is 8 or 16,
                        _ => false,
                    };
                    if (!valid || body[10] != 0 || body[11] != 0 || body[12] > 1) throw BadPng("the colour type or encoding is unknown");
                    interlaced = body[12] == 1;
                    CheckSize(width, height, Channels(type) * depth);
                    break;
                case "PLTE":
                    if (len == 0 || len % 3 != 0 || len > 768 || palette is not null) throw BadPng("the palette is wrong");
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    trns = body.ToArray();
                    break;
                case "pHYs" when len == 9 && body[8] == 1:
                    dpiX = BinaryPrimitives.ReadUInt32BigEndian(body) * 0.0254;
                    dpiY = BinaryPrimitives.ReadUInt32BigEndian(body[4..]) * 0.0254;
                    break;
                case "IDAT": idat.Write(body); break;
                case "IEND": ended = true; break;
                default:
                    if ((name[0] & 0x20) == 0) throw BadPng($"it needs the unknown chunk {name}");
                    break;
            }
            p += 12 + (int)len;
        }
        if (type == 3 && palette is null) throw BadPng("the palette is missing");
        if (dpiX is not > 0 || dpiY is not > 0) dpiX = dpiY = null;
        if (trns is not null && (type is 4 or 6 || trns.Length != (type switch { 0 => 2, 2 => 6, _ => trns.Length }) || type == 3 && trns.Length > palette!.Length / 3))
            throw BadPng("the transparency chunk is wrong");

        var channels = Channels(type);
        var bpp = Math.Max(1, channels * depth / 8);
        int Stride(int w) => (int)(((long)w * channels * depth + 7) / 8);
        var passes = interlaced ? Adam7(width, height) : new List<(int X, int Y, int Dx, int Dy, int W, int H)> { (0, 0, 1, 1, width, height) };
        var expected = passes.Sum(q => (long)q.H * (Stride(q.W) + 1));
        if (expected > Array.MaxLength) throw BadPng("it is too large");
        var packed = idat.ToArray();
        // Deflate cannot expand more than 1032 times: a tiny file with a huge claim is refused before any memory is taken.
        if (expected > packed.Length * 1032L + 1024) throw BadPng("the image data is truncated");
        var raw = Inflate(packed, (int)expected);
        Unfilter(raw, passes, Stride, bpp, keepFiltered: !interlaced && type is 0 or 2 or 3 && trns is null, out var reusable);

        PdfStream color;
        PdfStream? mask = null;
        var colorChannels = type is 4 or 6 ? channels - 1 : channels;
        if (reusable)
        {
            color = new PdfStream(packed);
            color["/Filter"] = new PdfName("/FlateDecode");
            color["/DecodeParms"] = Parms(channels, depth, width);
        }
        else
        {
            var stride = Stride(width);
            var pixels = Assemble(raw, passes, Stride, stride, height, channels * depth);
            if (type is 4 or 6)
            {
                var bytes = depth / 8;
                var colorStride = width * colorChannels * bytes;
                var c = new byte[(long)colorStride * height];
                var a = new byte[(long)width * bytes * height];
                var opaque = true;
                for (int s = 0, ci = 0, ai = 0; s < pixels.Length; s += bpp)
                {
                    for (var k = 0; k < colorChannels * bytes; k++) c[ci++] = pixels[s + k];
                    for (var k = colorChannels * bytes; k < bpp; k++) { a[ai++] = pixels[s + k]; opaque &= pixels[s + k] == 0xFF; }
                }
                color = Encode(c, colorStride, height, colorChannels, depth, width);
                if (!opaque) mask = Encode(a, width * bytes, height, 1, depth, width);
            }
            else
            {
                color = Encode(pixels, stride, height, channels, depth, width);
                if (trns is not null)
                {
                    CheckSize(width, height, 8);
                    var a = KeyMask(pixels, stride, width, height, depth, channels, type, trns);
                    if (a is not null) mask = Encode(a, width, height, 1, 8, width);
                }
            }
        }
        color["/ColorSpace"] = type switch
        {
            3 => new PdfArray(new PdfName("/Indexed"), new PdfName("/DeviceRGB"), new PdfNumber(palette!.Length / 3 - 1), new PdfString(palette, hex: true)),
            2 or 6 => new PdfName("/DeviceRGB"),
            _ => new PdfName("/DeviceGray"),
        };
        color["/BitsPerComponent"] = new PdfNumber(depth);
        if (mask is not null)
        {
            mask["/Type"] = new PdfName("/XObject");
            mask["/Subtype"] = new PdfName("/Image");
            mask["/Width"] = new PdfNumber(width);
            mask["/Height"] = new PdfNumber(height);
            mask["/ColorSpace"] = new PdfName("/DeviceGray");
            mask["/BitsPerComponent"] = new PdfNumber(type is 4 or 6 ? depth : 8);
        }
        return new PdfImage(width, height, color, mask) { DpiX = dpiX, DpiY = dpiY };
    }

    private static int Channels(int type) => type switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };

    private static PdfDictionary Parms(int colors, int depth, int width)
    {
        var d = new PdfDictionary();
        d["/Predictor"] = new PdfNumber(15);
        d["/Colors"] = new PdfNumber(colors);
        d["/BitsPerComponent"] = new PdfNumber(depth);
        d["/Columns"] = new PdfNumber(width);
        return d;
    }

    private static List<(int X, int Y, int Dx, int Dy, int W, int H)> Adam7(int width, int height)
    {
        var list = new List<(int, int, int, int, int, int)>();
        foreach (var (x, y, dx, dy) in new[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) })
        {
            int w = (int)(((long)width - x + dx - 1) / dx), h = (int)(((long)height - y + dy - 1) / dy);
            if (w > 0 && h > 0) list.Add((x, y, dx, dy, w, h));
        }
        return list;
    }

    private static byte[] Inflate(byte[] packed, int expected)
    {
        var raw = new byte[expected];
        try
        {
            using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
            if (z.ReadAtLeast(raw, expected, throwOnEndOfStream: false) < expected) throw BadPng("the image data is truncated");
        }
        catch (InvalidDataException) { throw BadPng("the image data is corrupt"); }
        return raw;
    }

    /// <summary>Undoes the row filters in place. With <paramref name="keepFiltered"/> only the filter types are checked.</summary>
    private static void Unfilter(byte[] raw, List<(int X, int Y, int Dx, int Dy, int W, int H)> passes, Func<int, int> strideOf, int bpp, bool keepFiltered, out bool reusable)
    {
        reusable = keepFiltered;
        var p = 0;
        foreach (var pass in passes)
        {
            var stride = strideOf(pass.W);
            for (var y = 0; y < pass.H; y++, p += stride + 1)
            {
                var ft = raw[p];
                if (ft > 4) throw BadPng("a row has an unknown filter");
                if (keepFiltered || ft == 0) continue;
                for (var i = 0; i < stride; i++)
                {
                    var at = p + 1 + i;
                    int left = i >= bpp ? raw[at - bpp] : 0;
                    int up = y > 0 ? raw[at - stride - 1] : 0;
                    int corner = y > 0 && i >= bpp ? raw[at - stride - 1 - bpp] : 0;
                    raw[at] += (byte)(ft switch { 1 => left, 2 => up, 3 => (left + up) >> 1, _ => Filters.Paeth((byte)left, (byte)up, (byte)corner) });
                }
            }
        }
    }

    /// <summary>Rows without filter bytes, with interlacing undone.</summary>
    private static byte[] Assemble(byte[] raw, List<(int X, int Y, int Dx, int Dy, int W, int H)> passes, Func<int, int> strideOf, int stride, int height, int bitsPerPixel)
    {
        var o = new byte[(long)stride * height];
        var p = 0;
        foreach (var pass in passes)
        {
            var ps = strideOf(pass.W);
            for (var y = 0; y < pass.H; y++, p += ps + 1)
            {
                var row = (pass.Y + y * pass.Dy) * stride;
                if (pass.Dx == 1 && pass.X == 0) { Buffer.BlockCopy(raw, p + 1, o, row, ps); continue; }
                for (var x = 0; x < pass.W; x++)
                {
                    var tx = pass.X + x * pass.Dx;
                    if (bitsPerPixel >= 8) Buffer.BlockCopy(raw, p + 1 + x * (bitsPerPixel / 8), o, row + tx * (bitsPerPixel / 8), bitsPerPixel / 8);
                    else o[row + tx * bitsPerPixel / 8] |= (byte)(Sample(raw, p + 1, x, bitsPerPixel) << (8 - bitsPerPixel - tx * bitsPerPixel % 8));
                }
            }
        }
        return o;
    }

    private static int Sample(byte[] data, int rowStart, int index, int bits) => bits switch
    {
        16 => data[rowStart + index * 2] << 8 | data[rowStart + index * 2 + 1],
        8 => data[rowStart + index],
        _ => data[rowStart + index * bits / 8] >> (8 - bits - index * bits % 8) & ((1 << bits) - 1),
    };

    /// <summary>Opacity per pixel from a tRNS chunk; null when every pixel is opaque.</summary>
    private static byte[]? KeyMask(byte[] pixels, int stride, int width, int height, int depth, int channels, int type, byte[] trns)
    {
        var key = new int[channels];
        if (type != 3) for (var k = 0; k < channels; k++) key[k] = BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(k * 2));
        var mask = new byte[(long)width * height];
        var opaque = true;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                byte a;
                if (type == 3)
                {
                    var index = Sample(pixels, y * stride, x, depth);
                    a = index < trns.Length ? trns[index] : (byte)255;
                }
                else
                {
                    var match = true;
                    for (var k = 0; k < channels; k++) match &= Sample(pixels, y * stride, x * channels + k, depth) == key[k];
                    a = match ? (byte)0 : (byte)255;
                }
                opaque &= a == 255;
                mask[y * width + x] = a;
            }
        return opaque ? null : mask;
    }

    /// <summary>Flate with PNG row filters, choosing the filter with the smallest sum of differences per row.</summary>
    private static PdfStream Encode(byte[] pixels, int stride, int height, int colors, int depth, int width)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bpp = Math.Max(1, colors * depth / 8);
            var rows = new byte[5][];
            for (var f = 0; f < 5; f++) { rows[f] = new byte[stride + 1]; rows[f][0] = (byte)f; }
            for (var y = 0; y < height; y++)
            {
                var at = y * stride;
                var best = 0;
                long bestSum = long.MaxValue;
                for (var f = 0; f < (depth < 8 ? 1 : 5); f++)
                {
                    long sum = 0;
                    var row = rows[f];
                    for (var i = 0; i < stride; i++)
                    {
                        int left = i >= bpp ? pixels[at + i - bpp] : 0;
                        int up = y > 0 ? pixels[at + i - stride] : 0;
                        int corner = y > 0 && i >= bpp ? pixels[at + i - stride - bpp] : 0;
                        var v = (byte)(pixels[at + i] - (f switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) >> 1, _ => Filters.Paeth((byte)left, (byte)up, (byte)corner) }));
                        row[i + 1] = v;
                        sum += v < 128 ? v : 256 - v;
                    }
                    if (sum < bestSum) { bestSum = sum; best = f; }
                }
                z.Write(rows[best]);
            }
        }
        var s = new PdfStream(ms.ToArray());
        s["/Filter"] = new PdfName("/FlateDecode");
        s["/DecodeParms"] = Parms(colors, depth, width);
        return s;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
