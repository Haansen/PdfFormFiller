namespace PdfFormFiller.Core;

/// <summary>
/// After pages have been removed: takes their content out of the structure tree (dropping elements left with nothing), the
/// parent tree and the bookmarks, so the file stays consistent and nothing of the pages remains in it.
/// </summary>
internal static class RemovedPageCleaner
{
    public static void Run(PdfDocument doc, IReadOnlySet<PdfObject> pages)
    {
        if (doc.Catalog.Dict("/StructTreeRoot") is { } root)
        {
            var dropped = CleanElements(root, pages);
            CleanParentTree(doc, root, pages, dropped);
            CleanNames(root.Dict("/IDTree"), dropped);
        }
        if (doc.Catalog.Dict("/Outlines") is { } outlines) CleanOutline(doc, outlines, pages);
    }

    private static PdfObject? Resolve(PdfObject? o) => PdfExtCore.Resolve(o);

    private static IEnumerable<PdfObject> Kids(PdfDictionary node) => Resolve(node["/K"]) switch
    {
        PdfArray a => a.Items,
        null => Array.Empty<PdfObject>(),
        var single => new[] { single },
    };

    private static bool IsContent(PdfDictionary kid) => kid.Name("/Type") is "/MCR" or "/OBJR";

    private static HashSet<PdfDictionary> CleanElements(PdfDictionary root, IReadOnlySet<PdfObject> pages)
    {
        // parents before children, so walking the list backwards sees every element after all of its children
        var order = new List<PdfDictionary> { root };
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        for (var i = 0; i < order.Count; i++)
            foreach (var kid in Kids(order[i]))
                if (Resolve(kid) is PdfDictionary d && !IsContent(d) && seen.Add(d)) order.Add(d);

        var dropped = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var touched = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance); // something below was taken out
        var hasContent = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var page = node.Dict("/Pg");
            var onRemoved = page is not null && pages.Contains(page);
            var changed = false;
            var content = false;
            var kept = new List<PdfObject>();
            foreach (var kid in Kids(node))
            {
                bool gone;
                switch (Resolve(kid))
                {
                    case PdfNumber: gone = onRemoved; content |= !gone; break;
                    case PdfDictionary d when IsContent(d):
                        gone = (d.Dict("/Pg") ?? page) is { } p && pages.Contains(p);
                        content |= !gone;
                        break;
                    case PdfDictionary d:
                        gone = dropped.Contains(d);
                        content |= hasContent.Contains(d);
                        if (touched.Contains(d)) touched.Add(node);
                        break;
                    default: gone = false; break;
                }
                if (gone) changed = true;
                else kept.Add(kid);
            }
            if (content) hasContent.Add(node);
            if (changed) touched.Add(node);
            if (changed) node["/K"] = kept.Count switch { 0 => null, 1 => kept[0], _ => new PdfArray(kept) };
            if (onRemoved) node.Remove("/Pg");
            if (i > 0 && touched.Contains(node) && !content) dropped.Add(node);
        }
        return dropped;
    }

    private static void CleanParentTree(PdfDocument doc, PdfDictionary root, IReadOnlySet<PdfObject> pages, HashSet<PdfDictionary> dropped)
    {
        if (root.Dict("/ParentTree") is not { } tree) return;
        var keys = new HashSet<long>();
        foreach (var page in pages.OfType<PdfDictionary>())
        {
            if (page.Long("/StructParents") is { } k) keys.Add(k);
            foreach (var annot in page.Arr("/Annots")?.Dicts() ?? Enumerable.Empty<PdfDictionary>())
                if (annot.Long("/StructParent") is { } a) keys.Add(a);
        }
        foreach (var page in doc.Pages) // a damaged file may use a key twice
            if (page.Long("/StructParents") is { } k) keys.Remove(k);
        foreach (var leaf in Leaves(tree, "/Nums"))
            Filter(leaf.Arr("/Nums")!, (key, value) => key is PdfNumber n && keys.Contains(n.LongValue) || value is PdfDictionary d && dropped.Contains(d));
    }

    private static void CleanNames(PdfDictionary? tree, HashSet<PdfDictionary> dropped)
    {
        if (tree is null || dropped.Count == 0) return;
        foreach (var leaf in Leaves(tree, "/Names"))
            Filter(leaf.Arr("/Names")!, (_, value) => value is PdfDictionary d && dropped.Contains(d));
    }

    private static void Filter(PdfArray pairs, Func<PdfObject?, PdfObject?, bool> remove)
    {
        var kept = new List<PdfObject>();
        for (var i = 0; i + 1 < pairs.Count; i += 2)
        {
            if (remove(Resolve(pairs[i]), Resolve(pairs[i + 1]))) continue;
            kept.Add(pairs[i]);
            kept.Add(pairs[i + 1]);
        }
        if (kept.Count == pairs.Count) return;
        pairs.Clear();
        pairs.Items.AddRange(kept);
    }

    private static List<PdfDictionary> Leaves(PdfDictionary tree, string key)
    {
        var result = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { tree };
        var work = new Stack<PdfDictionary>();
        work.Push(tree);
        while (work.Count > 0)
        {
            var node = work.Pop();
            if (node.Arr(key) is not null) result.Add(node);
            foreach (var kid in node.Arr("/Kids")?.Dicts() ?? Enumerable.Empty<PdfDictionary>())
                if (seen.Add(kid)) work.Push(kid);
        }
        return result;
    }

    // ------------------------------------------------------------------ bookmarks

    private static void CleanOutline(PdfDocument doc, PdfDictionary root, IReadOnlySet<PdfObject> pages)
    {
        var order = new List<(PdfDictionary Node, List<PdfDictionary> Kids)>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        order.Add((root, new()));
        for (var i = 0; i < order.Count; i++)
            for (var kid = order[i].Node.Dict("/First"); kid is not null && seen.Add(kid); kid = kid.Dict("/Next"))
            {
                order[i].Kids.Add(kid);
                order.Add((kid, new()));
            }

        var dropped = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var visible = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        if (!order.Any(o => PointsAt(o.Node, pages))) return;
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var (node, all) = order[i];
            var kids = all.Where(k => !dropped.Contains(k)).ToList();
            var pointsAtRemoved = i > 0 && PointsAt(node, pages);
            if (pointsAtRemoved)
            {
                node.Remove("/Dest");
                node.Remove("/A");
                if (kids.Count == 0) { dropped.Add(node); continue; }
            }
            if (kids.Count != all.Count)
            {
                node["/First"] = kids.Count == 0 ? null : doc.RefFor(kids[0]);
                node["/Last"] = kids.Count == 0 ? null : doc.RefFor(kids[^1]);
                for (var k = 0; k < kids.Count; k++)
                {
                    kids[k]["/Prev"] = k == 0 ? null : doc.RefFor(kids[k - 1]);
                    kids[k]["/Next"] = k == kids.Count - 1 ? null : doc.RefFor(kids[k + 1]);
                }
            }
            var open = i == 0 || (node.Int("/Count") ?? 0) > 0;
            var count = kids.Sum(k => 1 + visible.GetValueOrDefault(k));
            node["/Count"] = count == 0 ? null : new PdfNumber(open ? count : -count);
            visible[node] = open ? count : 0;
        }
    }

    private static bool PointsAt(PdfDictionary item, IReadOnlySet<PdfObject> pages)
    {
        var dest = Resolve(item["/Dest"]) ?? Resolve(item.Dict("/A")?["/D"]);
        return dest is PdfArray { Count: > 0 } a && Resolve(a[0]) is { } target && pages.Contains(target);
    }
}
