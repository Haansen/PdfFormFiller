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
        var pages = doc.Pages;

        // Decide everything from a snapshot taken before anything is removed: pages may share one /Annots array.
        var perPage = new List<List<PdfDictionary>>(pages.Count);
        var arrays = new List<PdfArray>();
        var arraySeen = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance);
        var flattened = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in pages)
        {
            var list = new List<PdfDictionary>();
            perPage.Add(list);
            var annots = page.Arr("/Annots");
            if (annots is null) continue;
            if (arraySeen.Add(annots)) arrays.Add(annots);
            var onPage = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var widget in annots.Dicts())
            {
                if (!widget.IsWidget()) continue;
                if (onlyWidgets is not null && !onlyWidgets.Contains(widget)) continue;
                flattened.Add(widget);
                if (onPage.Add(widget)) list.Add(widget);
            }
        }

        for (var p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            if (perPage[p].Count == 0) continue;
            var ops = new StringBuilder();
            var counter = 0;
            var propertyNames = new Dictionary<PdfDictionary, string>(ReferenceEqualityComparer.Instance);
            foreach (var widget in perPage[p])
            {
                var flags = widget.Int("/F") ?? 0;
                if ((flags & FlagHidden) != 0 || (flags & FlagNoView) != 0) continue;
                var form = NormalAppearance(widget);
                if (form is null) continue;
                var placement = Placement(widget, form, out var skip);
                if (skip) continue; // empty rectangle: nothing visible to draw
                var name = UniqueName(page, ref counter);
                if (!form.ContainsKey("/Subtype")) form["/Subtype"] = new PdfName("/Form");
                if (!form.ContainsKey("/Type")) form["/Type"] = new PdfName("/XObject");
                page.Resources().GetOrCreateDict("/XObject").SetRef(name, form, doc);

                // Keep optional-content visibility: a widget in a layer that is switched off stays invisible.
                var oc = widget.Dict("/OC");
                string? ocName = null;
                if (oc is not null && !propertyNames.TryGetValue(oc, out ocName))
                {
                    ocName = UniquePropertyName(page, ref counter);
                    page.Resources().GetOrCreateDict("/Properties").SetRef(ocName, oc, doc);
                    propertyNames[oc] = ocName;
                }
                if (ocName is not null) ops.Append("/OC ").Append(ocName).Append(" BDC\n");
                ops.Append(placement.Replace("{name}", name));
                if (ocName is not null) ops.Append("EMC\n");
            }

            if (ops.Length > 0)
            {
                var balance = ContentBalance.Analyze(ContentBytes(page));
                var epilogue = balance?.Epilogue() ?? "Q\n";
                page.WrapContents(doc, Encoding.ASCII.GetBytes("q\n"), Encoding.ASCII.GetBytes(epilogue + ops));
            }
        }

        RemoveStructureReferences(doc, flattened);
        foreach (var annots in arrays) annots.RemoveWhere(item => Resolve(item) is PdfDictionary d && flattened.Contains(d));
        foreach (var page in pages)
            if (page.Arr("/Annots") is { Count: 0 }) page.Remove("/Annots");

        if (onlyWidgets is null)
        {
            doc.Catalog.Remove("/AcroForm");
            doc.Catalog.Remove("/NeedsRendering");
            doc.Catalog.Remove("/Perms"); // certification/usage-rights signatures no longer apply to a flattened file
            return;
        }
        FieldTree.PruneOrphans(doc);
        if ((FieldTree.AcroForm(doc)?.Arr("/Fields")?.Count ?? 0) == 0) doc.Catalog.Remove("/AcroForm");
    }

    /// <summary>
    /// Tagged PDFs reference form widgets from the structure tree (/OBJR). Left in place they would keep the removed widgets,
    /// and through /Parent the whole field tree, alive in the output, and point at annotations that no longer exist.
    /// </summary>
    private static void RemoveStructureReferences(PdfDocument doc, HashSet<PdfDictionary> removed)
    {
        if (removed.Count == 0 || doc.Catalog.Dict("/StructTreeRoot") is not { } root) return;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        var work = new Stack<PdfDictionary>();
        work.Push(root);
        while (work.Count > 0 && seen.Count < 1_000_000)
        {
            var node = work.Pop();
            switch (Resolve(node["/K"]))
            {
                case PdfDictionary single:
                    if (IsRemovedReference(single, removed)) node.Remove("/K");
                    else if (seen.Add(single)) work.Push(single);
                    break;
                case PdfArray kids:
                    kids.RemoveWhere(item => Resolve(item) is PdfDictionary d && IsRemovedReference(d, removed));
                    foreach (var kid in kids.Dicts()) if (seen.Add(kid)) work.Push(kid);
                    break;
            }
        }
    }

    private static bool IsRemovedReference(PdfDictionary d, HashSet<PdfDictionary> removed)
        => d.Name("/Type") == "/OBJR" && d.Dict("/Obj") is { } target && removed.Contains(target);

    /// <summary>The page's decoded content, or empty when it cannot be decoded (the page is then treated as balanced).</summary>
    private static byte[] ContentBytes(PdfDictionary page)
    {
        try
        {
            using var ms = new MemoryStream();
            void Add(PdfObject? o)
            {
                if (o is not PdfStream s) return;
                var bytes = s.Decoded();
                ms.Write(bytes, 0, bytes.Length);
                ms.WriteByte((byte)'\n');
            }
            switch (Resolve(page["/Contents"]))
            {
                case PdfStream s: Add(s); break;
                case PdfArray a: foreach (var it in a.Items) Add(Resolve(it)); break;
            }
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is PdfFormException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            return Array.Empty<byte>();
        }
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

    private static string UniquePropertyName(PdfDictionary page, ref int counter)
    {
        var props = page.Dict("/Resources")?.Dict("/Properties");
        while (true)
        {
            var name = $"/PfOC{counter++}";
            if (props is null || !props.ContainsKey(name)) return name;
        }
    }

    /// <summary>
    /// PDF 32000-1 §12.5.5 algorithm: map the appearance's transformed BBox onto the annotation /Rect. The XObject name is
    /// the placeholder <c>{name}</c>. <paramref name="skip"/> is set when the rectangle is empty or the numbers are unusable.
    /// </summary>
    private static string Placement(PdfDictionary widget, PdfDictionary form, out bool skip)
    {
        skip = true;
        var (rx0, ry0, rx1, ry1) = widget.Rect();
        if (!(rx1 - rx0 > 0) || !(ry1 - ry0 > 0)) return string.Empty;
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
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(ex) || !double.IsFinite(ey)) return string.Empty;
        skip = false;
        return $"q {Fmt(sx)} 0 0 {Fmt(sy)} {Fmt(ex)} {Fmt(ey)} cm {{name}} Do Q\n";
    }
}
