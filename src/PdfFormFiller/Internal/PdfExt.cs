using PdfFormFiller.Core;

namespace PdfFormFiller.Internal;

internal static class PdfExt
{
    public static PdfObject? Resolve(PdfObject? item) => PdfExtCore.Resolve(item);

    /// <summary>Text of a string, or bare name of a name object.</summary>
    public static string? Str(this PdfDictionary d, string key)
        => Resolve(d[key]) switch
        {
            PdfString s => s.Text,
            PdfName n => n.Bare,
            _ => null,
        };

    public static PdfDictionary? Parent(this PdfDictionary d) => d.Dict("/Parent");

    /// <summary>Looks up <paramref name="key"/> on the node, then walks the /Parent chain (PDF field attribute inheritance).</summary>
    public static PdfObject? Inherited(this PdfDictionary node, string key)
    {
        var cur = node;
        var guard = 0;
        while (cur is not null && guard++ < 64)
        {
            var v = Resolve(cur[key]);
            if (v is not null) return v;
            cur = cur.Parent();
        }
        return null;
    }

    public static bool IsWidget(this PdfDictionary d) => d.Name("/Subtype") == "/Widget";

    public static (double x0, double y0, double x1, double y1) Rect(this PdfDictionary widget)
    {
        var a = widget.Arr("/Rect");
        if (a is null || a.Count < 4) return (0, 0, 0, 0);
        var x0 = a.RealAt(0); var y0 = a.RealAt(1); var x1 = a.RealAt(2); var y1 = a.RealAt(3);
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    public static PdfDictionary GetOrCreateDict(this PdfDictionary d, string key)
    {
        var existing = d.Dict(key);
        if (existing is not null) return existing;
        var nd = new PdfDictionary();
        d[key] = nd;
        return nd;
    }

    public static PdfArray GetOrCreateArray(this PdfDictionary d, string key)
    {
        var existing = d.Arr(key);
        if (existing is not null) return existing;
        var na = new PdfArray();
        d[key] = na;
        return na;
    }

    public static void SetRef(this PdfDictionary d, string key, PdfObject target, PdfDocument doc) => d[key] = doc.RefFor(target);

    public static void RemoveObjectRef(this PdfArray a, PdfDictionary target)
    {
        for (var i = a.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Resolve(a[i]), target)) a.RemoveAt(i);
    }

    public static PdfArray NumberArray(params double[] values) => new(values.Select(v => (PdfObject)new PdfNumber(v)));

    public static string Fmt(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Page /Resources dictionary, created if missing.</summary>
    public static PdfDictionary Resources(this PdfDictionary page) => page.GetOrCreateDict("/Resources");

    /// <summary>Adds content streams before and after the page's existing content.</summary>
    public static void WrapContents(this PdfDictionary page, PdfDocument doc, byte[] before, byte[] after)
    {
        var arr = new PdfArray();
        switch (Resolve(page["/Contents"]))
        {
            case PdfStream s: arr.Add(doc.RefFor(s)); break;
            case PdfArray a: foreach (var it in a.Items) arr.Add(it); break;
        }
        var pre = new PdfStream(Array.Empty<byte>()); pre.SetData(before, compress: false);
        var post = new PdfStream(Array.Empty<byte>()); post.SetData(after, compress: false);
        arr.Insert(0, doc.RefFor(pre));
        arr.Add(doc.RefFor(post));
        page["/Contents"] = arr;
    }
}
