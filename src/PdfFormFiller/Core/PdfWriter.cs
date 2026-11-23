using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>Serializes a document as a fresh PDF 1.7 file with a classic xref table. Only reachable objects are written.</summary>
internal static class PdfWriter
{
    public static byte[] Write(PdfDocument doc)
    {
        // 1. collect reachable indirect objects, numbering them 1..n in discovery order
        var numbers = new Dictionary<PdfObject, int>(ReferenceEqualityComparer.Instance);
        var ordered = new List<PdfObject>();
        var queue = new Queue<PdfObject>();

        void Visit(PdfObject? o)
        {
            if (o is null) return;
            if (o is PdfReference r)
            {
                var target = r.Target;
                if (target is PdfNull) return;
                if (!numbers.ContainsKey(target)) { numbers[target] = ordered.Count + 1; ordered.Add(target); queue.Enqueue(target); }
                return;
            }
            if (doc.IsIndirect(o) && o is not PdfDictionary && o is not PdfArray)
            {
                // scalar registered as indirect: still inline it, simpler
            }
            switch (o)
            {
                case PdfArray a: foreach (var it in a.Items) Visit(it); break;
                case PdfDictionary d: foreach (var kv in d.Entries) Visit(kv.Value); break;
            }
        }

        var trailer = new PdfDictionary();
        foreach (var kv in doc.Trailer.Entries)
            if (kv.Key is "/Root" or "/Info") trailer[kv.Key] = kv.Value;
        trailer["/Root"] = doc.RefFor(doc.Catalog);
        Visit(trailer);
        while (queue.Count > 0)
        {
            var o = queue.Dequeue();
            switch (o)
            {
                case PdfArray a: foreach (var it in a.Items) Visit(it); break;
                case PdfDictionary d: foreach (var kv in d.Entries) Visit(kv.Value); break;
            }
        }

        // 2. write
        var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[ordered.Count + 1];
        for (var i = 0; i < ordered.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n");
            WriteObject(ms, ordered[i], numbers, doc);
            W("\nendobj\n");
        }
        var xrefPos = ms.Position;
        W($"xref\n0 {ordered.Count + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= ordered.Count; i++) W($"{offsets[i]:D10} 00000 n \n");
        var id = MD5.HashData(ms.ToArray());
        trailer["/Size"] = new PdfNumber(ordered.Count + 1);
        trailer["/ID"] = new PdfArray(new PdfString(id, hex: true), new PdfString(id, hex: true));
        W("trailer\n");
        WriteObject(ms, trailer, numbers, doc);
        W($"\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }

    private static void WriteObject(MemoryStream ms, PdfObject o, Dictionary<PdfObject, int> numbers, PdfDocument doc)
    {
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        switch (o)
        {
            case PdfReference r:
            {
                var target = r.Target;
                if (target is PdfNull || !numbers.TryGetValue(target, out var n)) W("null");
                else W($"{n} 0 R");
                break;
            }
            case PdfNull: W("null"); break;
            case PdfBool b: W(b.ToString()); break;
            case PdfNumber n: W(n.ToString()); break;
            case PdfName name: W(EscapeName(name.Value)); break;
            case PdfString s: WriteString(ms, s); break;
            case PdfKeyword k: W(k.Text); break;
            case PdfArray a:
                W("[");
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) W(" ");
                    WriteObject(ms, a[i], numbers, doc);
                }
                W("]");
                break;
            case PdfStream st:
                st["/Length"] = new PdfNumber(st.Raw.Length);
                WriteDict(ms, st, numbers, doc);
                W("\nstream\n");
                ms.Write(st.Raw, 0, st.Raw.Length);
                W("\nendstream");
                break;
            case PdfDictionary d:
                WriteDict(ms, d, numbers, doc);
                break;
        }
    }

    private static void WriteDict(MemoryStream ms, PdfDictionary d, Dictionary<PdfObject, int> numbers, PdfDocument doc)
    {
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("<<");
        foreach (var kv in d.Entries)
        {
            W(EscapeName(kv.Key));
            W(" ");
            WriteObject(ms, kv.Value, numbers, doc);
            W("\n");
        }
        W(">>");
    }

    private static string EscapeName(string name)
    {
        var sb = new StringBuilder("/");
        foreach (var c in name.AsSpan(1))
        {
            if (c < 0x21 || c > 0x7E || c == '#' || Lexer.IsDelimiter((byte)c)) sb.Append('#').Append(((int)c & 0xFF).ToString("X2"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static void WriteString(MemoryStream ms, PdfString s)
    {
        var b = s.Bytes;
        var binary = s.Hex || b.Any(x => x < 0x20 && x != '\n' && x != '\r' && x != '\t' || x >= 0x7F);
        if (binary)
        {
            var sb = new StringBuilder(b.Length * 2 + 2);
            sb.Append('<');
            foreach (var x in b) sb.Append(x.ToString("X2"));
            sb.Append('>');
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            ms.Write(bytes, 0, bytes.Length);
            return;
        }
        ms.WriteByte((byte)'(');
        foreach (var x in b)
        {
            switch (x)
            {
                case (byte)'(': case (byte)')': case (byte)'\\': ms.WriteByte((byte)'\\'); ms.WriteByte(x); break;
                case (byte)'\n': ms.WriteByte((byte)'\\'); ms.WriteByte((byte)'n'); break;
                case (byte)'\r': ms.WriteByte((byte)'\\'); ms.WriteByte((byte)'r'); break;
                case (byte)'\t': ms.WriteByte((byte)'\\'); ms.WriteByte((byte)'t'); break;
                default: ms.WriteByte(x); break;
            }
        }
        ms.WriteByte((byte)')');
    }
}
