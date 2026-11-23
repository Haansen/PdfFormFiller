using System.Text;

namespace PdfFormFiller.Core;

/// <summary>Base class for every PDF object (ISO 32000-1 §7.3).</summary>
internal abstract class PdfObject
{
}

internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    private PdfNull() { }
    public override string ToString() => "null";
}

internal sealed class PdfBool : PdfObject
{
    public static readonly PdfBool True = new(true);
    public static readonly PdfBool False = new(false);
    public bool Value { get; }
    private PdfBool(bool v) => Value = v;
    public static PdfBool Of(bool v) => v ? True : False;
    public override string ToString() => Value ? "true" : "false";
}

internal sealed class PdfNumber : PdfObject
{
    public double Value { get; }
    public bool IsInteger { get; }
    public int IntValue => double.IsNaN(Value) ? 0 : (int)Math.Clamp(Math.Round(Value), int.MinValue, int.MaxValue);
    public long LongValue => double.IsNaN(Value) ? 0 : (long)Math.Clamp(Math.Round(Value), -9.2e18, 9.2e18);

    public PdfNumber(int v) { Value = v; IsInteger = true; }
    public PdfNumber(long v) { Value = v; IsInteger = true; }
    public PdfNumber(double v)
    {
        Value = v;
        IsInteger = Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15;
    }

    public override string ToString() => IsInteger
        ? LongValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class PdfName : PdfObject
{
    /// <summary>Name including the leading slash, e.g. "/Type".</summary>
    public string Value { get; }
    public PdfName(string value) => Value = value.StartsWith('/') ? value : "/" + value;
    public string Bare => Value[1..];
    public override string ToString() => Value;
    public override bool Equals(object? obj) => obj is PdfName n && n.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

internal sealed class PdfString : PdfObject
{
    public byte[] Bytes { get; set; }
    public bool Hex { get; set; }

    public PdfString(byte[] bytes, bool hex = false) { Bytes = bytes; Hex = hex; }

    /// <summary>Creates a text string: ASCII as-is, otherwise UTF-16BE with BOM (readable by every viewer).</summary>
    public static PdfString FromText(string text)
    {
        if (text.All(c => c >= 0x20 && c < 0x7F)) return new PdfString(Encoding.ASCII.GetBytes(text));
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var bytes = new byte[utf16.Length + 2];
        bytes[0] = 0xFE; bytes[1] = 0xFF;
        Buffer.BlockCopy(utf16, 0, bytes, 2, utf16.Length);
        return new PdfString(bytes);
    }

    /// <summary>Decodes as a PDF text string (UTF-16 with BOM, UTF-8 with BOM, or PDFDocEncoding).</summary>
    public string Text
    {
        get
        {
            var b = Bytes;
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2).TrimEnd('\0');
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2).TrimEnd('\0');
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            var sb = new StringBuilder(b.Length);
            foreach (var x in b) sb.Append(PdfDocEncoding.ToChar(x));
            return sb.ToString();
        }
    }

    public override string ToString() => Text;
}

internal sealed class PdfArray : PdfObject
{
    public List<PdfObject> Items { get; } = new();
    public PdfArray() { }
    public PdfArray(IEnumerable<PdfObject> items) => Items.AddRange(items);
    public PdfArray(params PdfObject[] items) => Items.AddRange(items);
    public int Count => Items.Count;
    public PdfObject this[int i] { get => Items[i]; set => Items[i] = value; }
    public void Add(PdfObject o) => Items.Add(o);
    public void RemoveAt(int i) => Items.RemoveAt(i);
    public void Insert(int i, PdfObject o) => Items.Insert(i, o);
    public void Clear() => Items.Clear();
}

internal class PdfDictionary : PdfObject
{
    private readonly Dictionary<string, PdfObject> _items = new();

    /// <summary>Raw (unresolved) item or null. Keys carry the leading slash.</summary>
    public PdfObject? this[string key]
    {
        get => _items.TryGetValue(key, out var v) ? v : null;
        set { if (value is null) _items.Remove(key); else _items[key] = value; }
    }

    public bool ContainsKey(string key) => _items.ContainsKey(key);
    public bool Remove(string key) => _items.Remove(key);
    public IEnumerable<string> Keys => _items.Keys;
    public IEnumerable<KeyValuePair<string, PdfObject>> Entries => _items;
    public int Count => _items.Count;
}

/// <summary>A stream: dictionary plus bytes. <see cref="Raw"/> is what is on disk (filtered); <see cref="Decoded"/> applies the filters.</summary>
internal sealed class PdfStream : PdfDictionary
{
    public byte[] Raw { get; private set; }
    private byte[]? _decoded;

    public PdfStream(byte[] raw) => Raw = raw;

    public PdfStream(PdfDictionary dict, byte[] raw)
    {
        foreach (var kv in dict.Entries) this[kv.Key] = kv.Value;
        Raw = raw;
    }

    /// <summary>Applies standard filters. Image-specific filters (DCT, JPX, CCITT, JBIG2) are left as-is.</summary>
    public byte[] Decoded() => _decoded ??= Filters.Decode(this);

    /// <summary>Replaces the content. Flate-compresses when asked and drops any previous filter chain.</summary>
    public void SetData(byte[] plain, bool compress = true)
    {
        Remove("/DecodeParms");
        Remove("/DL");
        if (compress)
        {
            Raw = Filters.FlateEncode(plain);
            this["/Filter"] = new PdfName("/FlateDecode");
        }
        else
        {
            Raw = plain;
            Remove("/Filter");
        }
        this["/Length"] = new PdfNumber(Raw.Length);
        _decoded = plain;
    }

    /// <summary>Replaces raw bytes without touching the filter entries (used by decryption).</summary>
    internal void SetRaw(byte[] raw)
    {
        Raw = raw;
        this["/Length"] = new PdfNumber(raw.Length);
        _decoded = null;
    }
}

/// <summary>Indirect reference "n g R". Resolves lazily through the owning document.</summary>
internal sealed class PdfReference : PdfObject
{
    public PdfDocument Document { get; }
    public int Number { get; }
    public int Generation { get; }

    public PdfReference(PdfDocument doc, int number, int generation)
    {
        Document = doc; Number = number; Generation = generation;
    }

    public PdfObject Target => Document.GetObject(Number);

    public override string ToString() => $"{Number} {Generation} R";
    public override bool Equals(object? obj) => obj is PdfReference r && r.Number == Number && ReferenceEquals(r.Document, Document);
    public override int GetHashCode() => Number;
}

internal static class PdfDocEncoding
{
    private static readonly char[] High =
    {
        '•', '†', '‡', '…', '—', '–', 'ƒ', '⁄', '‹', '›', '−', '‰',
        '„', '“', '”', '‘', '’', '‚', '™', 'ﬁ', 'ﬂ', 'Ł', 'Œ', 'Š',
        'Ÿ', 'Ž', 'ı', 'ł', 'œ', 'š', 'ž', '�', '€',
    };

    public static char ToChar(byte b)
    {
        if (b >= 0x80 && b <= 0xA0) return High[b - 0x80];
        return (char)b;
    }
}
