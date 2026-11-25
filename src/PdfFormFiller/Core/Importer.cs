namespace PdfFormFiller.Core;

/// <summary>
/// Deep-copies pages (and everything they reference) from one document into another. The copy is driven by a work
/// queue, not recursion, so long object chains in the source cannot overflow the stack.
/// </summary>
internal sealed class Importer
{
    private readonly PdfDocument _dest;
    private readonly Dictionary<PdfObject, PdfObject> _memo = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<(PdfObject Source, PdfObject Copy)> _work = new();

    public Importer(PdfDocument dest) => _dest = dest;

    private static readonly string[] Inheritable = { "/Resources", "/MediaBox", "/CropBox", "/Rotate" };

    public PdfDictionary ImportPage(PdfDictionary srcPage)
    {
        var newPage = new PdfDictionary();
        _memo[srcPage] = newPage;
        _dest.RefFor(newPage);
        foreach (var kv in srcPage.Entries)
        {
            // the structure tree stays behind; its keys would point into the destination's own tree
            if (kv.Key is "/Parent" or "/StructParents") continue;
            newPage[kv.Key] = Copy(kv.Value);
        }
        // inherited attributes may live on ancestors
        var anc = srcPage.Dict("/Parent");
        var guard = 0;
        while (anc is not null && guard++ < 64)
        {
            foreach (var k in Inheritable)
                if (!newPage.ContainsKey(k) && anc[k] is { } v) newPage[k] = Copy(v);
            anc = anc.Dict("/Parent");
        }
        Drain();
        newPage["/Type"] = new PdfName("/Page");
        _dest.AppendPage(newPage);
        return newPage;
    }

    /// <summary>Copies an arbitrary object graph (used for form default resources).</summary>
    public PdfObject CopyObject(PdfObject o)
    {
        var r = Copy(o);
        Drain();
        return r;
    }

    private void Drain()
    {
        while (_work.Count > 0)
        {
            var (src, dst) = _work.Dequeue();
            switch (src)
            {
                case PdfDictionary sd when dst is PdfDictionary dd:
                    foreach (var kv in sd.Entries)
                        if (kv.Key is not ("/StructParent" or "/StructParents")) dd[kv.Key] = Copy(kv.Value);
                    break;
                case PdfArray sa when dst is PdfArray da:
                    foreach (var it in sa.Items) da.Add(Copy(it));
                    break;
            }
        }
    }

    /// <summary>Returns the copy (or a reference to it). Containers are created empty and filled later by <see cref="Drain"/>.</summary>
    private PdfObject Copy(PdfObject o)
    {
        if (o is PdfReference r)
        {
            var target = PdfExtCore.Resolve(r) ?? PdfNull.Instance;
            if (target is PdfNull) return PdfNull.Instance;
            if (_memo.TryGetValue(target, out var done)) return _dest.RefFor(done);
            if (target is PdfDictionary td)
            {
                var type = td.Name("/Type");
                if (type is "/Pages" or "/Page") return PdfNull.Instance; // never drag in the source page tree; links to other pages are cut
            }
            var shell = Shell(target);
            if (shell is null) return Scalar(target); // indirect scalars are simply inlined
            _memo[target] = shell;
            _work.Enqueue((target, shell));
            return _dest.RefFor(shell);
        }
        var direct = Shell(o);
        if (direct is null) return Scalar(o);
        _work.Enqueue((o, direct));
        return direct;
    }

    private static PdfObject? Shell(PdfObject o) => o switch
    {
        PdfStream s => new PdfStream(s.Raw),
        PdfDictionary => new PdfDictionary(),
        PdfArray => new PdfArray(),
        _ => null,
    };

    private static PdfObject Scalar(PdfObject o) => o is PdfString s ? new PdfString((byte[])s.Bytes.Clone(), s.Hex) : o;
}
