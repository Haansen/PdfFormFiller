using System.Text;
using PdfFormFiller.Core;
using static PdfFormFiller.Internal.PdfExt;

namespace PdfFormFiller.Internal;

/// <summary>Draws every widget's normal appearance into its page and removes the form.</summary>
internal static class Flattener
{
    private const int FlagHidden = 1 << 1;
    private const int FlagNoView = 1 << 5;

    /// <param name="doc">Document to modify in place.</param>
    /// <param name="onlyWidgets">When given, only these widgets are flattened and the rest of the form is kept.</param>
    public static void Flatten(PdfDocument doc, HashSet<PdfDictionary>? onlyWidgets = null)
    {
        for (var p = 0; p < doc.Pages.Count; p++)
        {
            var page = doc.Pages[p];
            var annots = page.Arr("/Annots");
            if (annots is null) continue;

            var ops = new StringBuilder();
            var toRemove = new List<PdfDictionary>();
            var counter = 0;
            foreach (var widget in annots.Dicts().ToList())
            {
                if (!widget.IsWidget()) continue;
                if (onlyWidgets is not null && !onlyWidgets.Contains(widget)) continue;
                toRemove.Add(widget);
                var flags = widget.Int("/F") ?? 0;
                if ((flags & FlagHidden) != 0 || (flags & FlagNoView) != 0) continue;
                var form = NormalAppearance(widget);
                if (form is null) continue;
                var name = UniqueName(page, ref counter);
                if (!form.ContainsKey("/Subtype")) form["/Subtype"] = new PdfName("/Form");
                if (!form.ContainsKey("/Type")) form["/Type"] = new PdfName("/XObject");
                page.Resources().GetOrCreateDict("/XObject").SetRef(name, form, doc);
                ops.Append(Placement(widget, form, name));
            }

            foreach (var w in toRemove) annots.RemoveObjectRef(w);
            if (annots.Count == 0) page.Remove("/Annots");

            if (ops.Length > 0)
                page.WrapContents(doc, Encoding.ASCII.GetBytes("q\n"), Encoding.ASCII.GetBytes("Q\n" + ops));
        }
        if (onlyWidgets is null)
        {
            doc.Catalog.Remove("/AcroForm");
            doc.Catalog.Remove("/Perms"); // certification/usage-rights signatures no longer apply to a flattened file
            return;
        }
        FieldTree.PruneOrphans(doc);
        if ((FieldTree.AcroForm(doc)?.Arr("/Fields")?.Count ?? 0) == 0) doc.Catalog.Remove("/AcroForm");
    }

    private static PdfStream? NormalAppearance(PdfDictionary widget)
    {
        var n = widget.Dict("/AP")?.Dict("/N");
        if (n is null) return null;
        if (n is PdfStream s) return s;
        var state = widget.Name("/AS") ?? "/Off";
        return n.Dict(state) as PdfStream;
    }

    private static string UniqueName(PdfDictionary page, ref int counter)
    {
        var xobjs = page.Dict("/Resources")?.Dict("/XObject");
        while (true)
        {
            var name = $"/PfFlat{counter++}";
            if (xobjs is null || !xobjs.ContainsKey(name)) return name;
        }
    }

    /// <summary>PDF 32000-1 §12.5.5 algorithm: map the appearance's transformed BBox onto the annotation /Rect.</summary>
    private static string Placement(PdfDictionary widget, PdfDictionary form, string name)
    {
        var (rx0, ry0, rx1, ry1) = widget.Rect();
        var bbox = form.Arr("/BBox");
        double bx0 = 0, by0 = 0, bx1 = 1, by1 = 1;
        if (bbox is not null && bbox.Count >= 4)
        {
            bx0 = bbox.RealAt(0); by0 = bbox.RealAt(1); bx1 = bbox.RealAt(2); by1 = bbox.RealAt(3);
        }
        double a = 1, b = 0, c = 0, d = 1, e = 0, f = 0;
        var m = form.Arr("/Matrix");
        if (m is not null && m.Count >= 6)
        {
            a = m.RealAt(0); b = m.RealAt(1); c = m.RealAt(2); d = m.RealAt(3); e = m.RealAt(4); f = m.RealAt(5);
        }
        var xs = new double[4]; var ys = new double[4];
        var corners = new[] { (bx0, by0), (bx1, by0), (bx1, by1), (bx0, by1) };
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = corners[i];
            xs[i] = a * x + c * y + e;
            ys[i] = b * x + d * y + f;
        }
        var tx0 = xs.Min(); var tx1 = xs.Max(); var ty0 = ys.Min(); var ty1 = ys.Max();
        var sx = tx1 - tx0 > 1e-6 ? (rx1 - rx0) / (tx1 - tx0) : 1;
        var sy = ty1 - ty0 > 1e-6 ? (ry1 - ry0) / (ty1 - ty0) : 1;
        var ex = rx0 - tx0 * sx;
        var ey = ry0 - ty0 * sy;
        return $"q {Fmt(sx)} 0 0 {Fmt(sy)} {Fmt(ex)} {Fmt(ey)} cm {name} Do Q\n";
    }
}
