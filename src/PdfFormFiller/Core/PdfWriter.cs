using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>Serializes a document as a fresh PDF 1.7 file with a classic xref table. Only reachable objects are written.</summary>
internal static class PdfWriter
{
    /// <summary>Collects the reachable indirect objects, numbering them 1..n in discovery order, and makes the trailer.</summary>
    internal static (Dictionary<PdfObject, int> Numbers, List<PdfObject> Ordered, PdfDictionary Trailer) Collect(PdfDocument doc)
    {
        var numbers = new Dictionary<PdfObject, int>(ReferenceEqualityComparer.Instance);
        var ordered = new List<PdfObject>();
        var queue = new Queue<PdfObject>();

        void Visit(PdfObject? o)
        {
            if (o is null) return;
            if (o is PdfReference r)
            {
                var target = r.Target;
                if (target is PdfNull || doc.IsRemovedPage(target)) return;
                Number(target);
                return;
            }
            // A container registered as indirect is referenced even where it is held directly, or it would be written twice.
            // Streams are always indirect.
            if (o is PdfStream || (o is PdfDictionary or PdfArray && doc.IsIndirect(o))) Number(o);
            else VisitChildren(o);
        }

        void Number(PdfObject o)
        {
            if (!numbers.TryAdd(o, ordered.Count + 1)) return;
            ordered.Add(o);
            queue.Enqueue(o);
        }

        void VisitChildren(PdfObject o)
        {
            switch (o)
            {
                case PdfReference r: Visit(r); break; // an indirect object that is itself just a reference
                case PdfArray a: foreach (var it in a.Items) Visit(it); break;
                case PdfDictionary d:
                    if (d is PdfStream st) st["/Length"] = new PdfNumber(st.Raw.Length); // a separate length object is no longer needed
                    foreach (var kv in d.Entries) Visit(kv.Value);
                    break;
            }
        }

        var trailer = new PdfDictionary();
        trailer["/Root"] = doc.RefFor(doc.Catalog);
        if (doc.Trailer.Dict("/Info") is { } info) trailer["/Info"] = doc.RefFor(info); // must be indirect
        VisitChildren(trailer);
        while (queue.Count > 0) VisitChildren(queue.Dequeue());
        return (numbers, ordered, trailer);
    }

    public static byte[] Write(PdfDocument doc)
    {
        var (numbers, ordered, trailer) = Collect(doc);
        var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[ordered.Count + 1];
        for (var i = 0; i < ordered.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n");
            WriteObject(ms, ordered[i], numbers, topLevel: true);
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
        WriteObject(ms, trailer, numbers, topLevel: true);
        W($"\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }

    internal static void WriteObject(MemoryStream ms, PdfObject o, Dictionary<PdfObject, int> numbers, bool topLevel = false)
    {
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        if (!topLevel && o is PdfDictionary or PdfArray && numbers.TryGetValue(o, out var own))
        {
            W($"{own} 0 R");
            return;
        }
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
            case PdfLiteral l: W(l.Text); break;
            case PdfKeyword: W("null"); break; // junk from a damaged file; written out it would break the syntax around it
            case PdfArray a:
                W("[");
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) W(" ");
                    WriteObject(ms, a[i], numbers);
                }
                W("]");
                break;
            case PdfStream st:
                WriteDict(ms, st, numbers);
                W("\nstream\n");
                ms.Write(st.Raw, 0, st.Raw.Length);
                W("\nendstream");
                break;
            case PdfDictionary d:
                WriteDict(ms, d, numbers);
                break;
        }
    }

    private static void WriteDict(MemoryStream ms, PdfDictionary d, Dictionary<PdfObject, int> numbers)
    {
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("<<");
        foreach (var kv in d.Entries)
        {
            W(EscapeName(kv.Key));
            W(" ");
            WriteObject(ms, kv.Value, numbers);
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
