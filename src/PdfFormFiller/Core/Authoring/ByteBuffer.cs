using System.Buffers.Text;

namespace PdfFormFiller.Core;

/// <summary>Growable byte buffer with allocation-free, culture-independent formatting of PDF tokens.</summary>
internal sealed class ByteBuffer
{
    private byte[] _data;

    public ByteBuffer(int capacity = 4096) => _data = new byte[capacity];

    public int Length { get; private set; }
    public ReadOnlySpan<byte> Written => _data.AsSpan(0, Length);
    public void Clear() => Length = 0;

    private Span<byte> Reserve(int n)
    {
        if (_data.Length - Length < n) Array.Resize(ref _data, (int)Math.Min(Array.MaxLength, Math.Max((long)Length + n, _data.Length * 2L)));
        return _data.AsSpan(Length);
    }

    public ByteBuffer Byte(byte b) { Reserve(1)[0] = b; Length++; return this; }

    public ByteBuffer Bytes(ReadOnlySpan<byte> b) { b.CopyTo(Reserve(b.Length)); Length += b.Length; return this; }

    /// <summary>Text made of ASCII characters only.</summary>
    public ByteBuffer Ascii(string s)
    {
        var span = Reserve(s.Length);
        for (var i = 0; i < s.Length; i++) span[i] = (byte)s[i];
        Length += s.Length;
        return this;
    }

    public ByteBuffer Int(long v)
    {
        Utf8Formatter.TryFormat(v, Reserve(20), out var n);
        Length += n;
        return this;
    }

    /// <summary>A number with at most three decimals, followed by a space.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not finite, or beyond a thousand million.</exception>
    public ByteBuffer Num(double v)
    {
        if (!(Math.Abs(v) <= 1e9)) throw new ArgumentOutOfRangeException(nameof(v), "A coordinate or size is not a usable number.");
        var n = (long)Math.Round(v * 1000);
        if (n < 0) { Byte((byte)'-'); n = -n; }
        Int(n / 1000);
        var f = (int)(n % 1000);
        if (f != 0)
        {
            Byte((byte)'.').Byte((byte)('0' + f / 100));
            if (f % 100 != 0) Byte((byte)('0' + f / 10 % 10));
            if (f % 10 != 0) Byte((byte)('0' + f % 10));
        }
        return Byte((byte)' ');
    }

    public ByteBuffer Hex4(ushort v)
    {
        const string digits = "0123456789ABCDEF";
        var s = Reserve(4);
        s[0] = (byte)digits[v >> 12]; s[1] = (byte)digits[v >> 8 & 15]; s[2] = (byte)digits[v >> 4 & 15]; s[3] = (byte)digits[v & 15];
        Length += 4;
        return this;
    }

    /// <summary>A text string as UTF-16 with byte order mark, hexadecimal.</summary>
    public ByteBuffer TextString(string s)
    {
        Ascii("<FEFF");
        foreach (var c in s) Hex4(c);
        return Byte((byte)'>');
    }
}
