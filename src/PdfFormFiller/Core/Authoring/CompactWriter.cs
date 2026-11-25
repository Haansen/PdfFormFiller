using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>
/// Writes a generated document as PDF 1.7 with object streams and a cross-reference stream, straight to a stream. The
/// structure tree is not part of the object graph; <see cref="StructureBuilder"/> supplies its objects as finished bytes.
/// </summary>
internal sealed class CompactWriter
{
    private const int ObjectsPerStream = 200;
    private const int BytesPerStream = 256 * 1024;

    private readonly Stream _out;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private long _position;
    private long[] _field1 = Array.Empty<long>();    // offset, or number of the containing object stream
    private short[] _field2 = Array.Empty<short>();  // index in the object stream, -1 for a plain object
    private int _next;                               // next free object number

    private readonly ByteBuffer _header = new();
    private readonly ByteBuffer _body = new(64 * 1024);
    private readonly List<int> _members = new();

    private CompactWriter(Stream destination) => _out = destination;

    public static void Write(PdfDocument doc, StructureBuilder? structure, Stream destination) => new CompactWriter(destination).Run(doc, structure);

    private void Run(PdfDocument doc, StructureBuilder? structure)
    {
        var root = new PdfLiteral();
        doc.Catalog["/StructTreeRoot"] = structure is null ? null : root;
        var (numbers, ordered, trailer) = PdfWriter.Collect(doc);
        var structFirst = ordered.Count + 1;
        var total = ordered.Count + (structure?.ObjectCount() ?? 0);
        root.Text = $"{structFirst} 0 R";
        // object streams get the numbers after that; one more for the xref stream
        Array.Resize(ref _field1, total + 1);
        Array.Resize(ref _field2, total + 1);
        _next = total + 1;

        Raw(Encoding.Latin1.GetBytes("%PDF-1.7\n%âãÏÓ\n"));
        var scratch = new MemoryStream();
        for (var i = 0; i < ordered.Count; i++)
        {
            scratch.SetLength(0);
            PdfWriter.WriteObject(scratch, ordered[i], numbers, topLevel: true);
            var bytes = scratch.GetBuffer().AsSpan(0, (int)scratch.Length);
            if (ordered[i] is PdfStream) Plain(i + 1, bytes);
            else Member(i + 1, bytes);
        }
        structure?.Write(structFirst, o => numbers[o], (number, b) => Member(number, b.Written));
        FlushMembers();

        var xref = _next;
        var xrefOffset = _position;
        Set(xref, xrefOffset, -1);
        var id = Convert.ToHexString(_hash.GetCurrentHash());
        var data = XrefRows(xref + 1);
        scratch.SetLength(0);
        foreach (var kv in trailer.Entries)
        {
            scratch.Write(Encoding.ASCII.GetBytes(kv.Key + " "));
            PdfWriter.WriteObject(scratch, kv.Value, numbers);
        }
        Raw(Encoding.ASCII.GetBytes($"{xref} 0 obj\n<</Type/XRef/Size {xref + 1}/W[1 5 2]{Encoding.ASCII.GetString(scratch.ToArray())}/ID[<{id}><{id}>]/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns 8>>/Length {data.Length}>>\nstream\n"));
        Raw(data);
        Raw(Encoding.ASCII.GetBytes($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n"));
        _out.Flush();
    }

    private void Raw(ReadOnlySpan<byte> bytes)
    {
        _out.Write(bytes);
        _hash.AppendData(bytes);
        _position += bytes.Length;
    }

    private void Set(int number, long field1, int field2)
    {
        if (number >= _field1.Length)
        {
            Array.Resize(ref _field1, Math.Max(number + 1, _field1.Length + _field1.Length / 64));
            Array.Resize(ref _field2, _field1.Length);
        }
        _field1[number] = field1;
        _field2[number] = (short)field2;
    }

    private void Plain(int number, ReadOnlySpan<byte> bytes)
    {
        Set(number, _position, -1);
        Raw(Encoding.ASCII.GetBytes($"{number} 0 obj\n"));
        Raw(bytes);
        Raw("\nendobj\n"u8);
    }

    private void Member(int number, ReadOnlySpan<byte> bytes)
    {
        _header.Int(number).Byte((byte)' ').Int(_body.Length).Byte((byte)' ');
        _body.Bytes(bytes).Byte((byte)'\n');
        _members.Add(number);
        if (_members.Count >= ObjectsPerStream || _body.Length >= BytesPerStream) FlushMembers();
    }

    private void FlushMembers()
    {
        if (_members.Count == 0) return;
        var number = _next++;
        for (var i = 0; i < _members.Count; i++) Set(_members[i], number, i);
        var packed = PdfStream.Deflate(_header.Written, _body.Written);
        var dict = $"<</Type/ObjStm/N {_members.Count}/First {_header.Length}/Filter/FlateDecode/Length {packed.Length}>>\nstream\n";
        Plain(number, Encoding.ASCII.GetBytes(dict).Concat(packed).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray());
        _header.Clear();
        _body.Clear();
        _members.Clear();
    }

    private byte[] XrefRows(int size)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var previous = new byte[8];
            var row = new byte[8];
            var line = new byte[9];
            line[0] = 2; // PNG "up" filter
            for (var n = 0; n < size; n++)
            {
                var (type, a, b) = n == 0 ? (0, 0L, 65535) : _field2[n] < 0 ? (1, _field1[n], 0) : (2, _field1[n], (int)_field2[n]);
                row[0] = (byte)type;
                for (var k = 0; k < 5; k++) row[1 + k] = (byte)(a >> (32 - 8 * k));
                row[6] = (byte)(b >> 8);
                row[7] = (byte)b;
                for (var k = 0; k < 8; k++) line[k + 1] = (byte)(row[k] - previous[k]);
                z.Write(line);
                (row, previous) = (previous, row);
            }
        }
        return ms.ToArray();
    }
}
