using System.Text;
using System.Text.RegularExpressions;

namespace PdfFormFiller.Core;

/// <summary>
/// In-memory PDF: cross-reference loading (tables, streams, hybrid files, broken files via reconstruction),
/// object streams, decryption, page tree and object allocation. Writing is in <see cref="PdfWriter"/>.
/// </summary>
internal sealed class PdfDocument
{
    private readonly byte[] _data;
    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject> _cache = new();
    private readonly Dictionary<PdfObject, int> _numbers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _loading = new();
    private readonly Dictionary<int, Dictionary<int, PdfObject>> _objStmCache = new();
    private Decryptor? _decryptor;
    private int _nextNumber;
    private bool _reconstructed;
    private List<PdfDictionary>? _pages;

    public PdfDictionary Trailer { get; private set; } = new();
    public PdfDictionary Catalog { get; private set; } = new();

    private readonly record struct XrefEntry(long Offset, int StreamNumber, int IndexInStream, bool InStream);

    private PdfDocument(byte[] data) => _data = data;

    // ------------------------------------------------------------------ loading

    public static PdfDocument Load(byte[] data)
    {
        var doc = new PdfDocument(data);
        doc.Init();
        return doc;
    }

    private void Init()
    {
        var ok = false;
        try
        {
            ParseXrefChain();
            ok = _xref.Count > 0;
        }
        catch (Exception ex) when (ex is PdfFormException or IndexOutOfRangeException or ArgumentException or InvalidCastException or OverflowException or KeyNotFoundException)
        {
            // fall through to reconstruction
        }
        if (!ok)
        {
            _xref.Clear();
            Reconstruct(initial: true);
        }

        // Must happen before anything else is read: the catalog may sit inside an encrypted object stream.
        SetupDecryption();

        var root = PdfExtCore.Resolve(Trailer["/Root"]) as PdfDictionary;
        if ((root is null || root.Dict("/Pages") is null) && !_reconstructed)
        {
            // the cross-reference parsed but points at the wrong things
            Reconstruct(initial: true);
            SetupDecryption();
            root = PdfExtCore.Resolve(Trailer["/Root"]) as PdfDictionary;
        }
        Catalog = root ?? throw new PdfFormException("Could not find the document catalog; the file is not a usable PDF.");
        _nextNumber = Math.Max(Trailer.Int("/Size") ?? 0, _xref.Count == 0 ? 1 : _xref.Keys.Max() + 1);
        if (_nextNumber < 1) _nextNumber = 1;
    }

    private int _encryptNumber = -1;

    /// <summary>Creates the decryptor from the trailer's /Encrypt entry. Idempotent.</summary>
    private void SetupDecryption()
    {
        if (_decryptor is not null)
        {
            Trailer.Remove("/Encrypt");
            return;
        }
        var encRaw = Trailer["/Encrypt"];
        if (PdfExtCore.Resolve(encRaw) is not PdfDictionary enc) return;
        var ids = Trailer.Arr("/ID");
        var id0 = ids is not null && ids.Count > 0 && PdfExtCore.Resolve(ids[0]) is PdfString s ? s.Bytes : Array.Empty<byte>();
        _encryptNumber = encRaw is PdfReference er ? er.Number : -1;
        _decryptor = Decryptor.Create(enc, id0);

        // Everything read so far was read without the key. Forget it; it is re-read and decrypted on demand.
        _cache.Clear();
        _numbers.Clear();
        _objStmCache.Clear();
        if (_encryptNumber >= 0)
        {
            _cache[_encryptNumber] = enc; // the encryption dictionary itself is never encrypted
            _numbers[enc] = _encryptNumber;
        }
        Trailer.Remove("/Encrypt");
    }

    // ------------------------------------------------------------------ xref

    private void ParseXrefChain()
    {
        var lex = new Lexer(_data);
        var sx = lex.LastIndexOf("startxref", _data.Length);
        if (sx < 0) throw new PdfFormException("startxref not found");
        lex.Position = sx + 9;
        var t = lex.Next();
        if (t.Kind != TokenKind.Number) throw new PdfFormException("bad startxref");
        var offset = (long)Lexer.ParseNumber(t.Text);
        var seen = new HashSet<long>();
        var trailers = new List<PdfDictionary>();
        var queue = new Queue<long>();
        queue.Enqueue(offset);
        while (queue.Count > 0)
        {
            var off = queue.Dequeue();
            if (off <= 0 || off >= _data.Length || !seen.Add(off)) continue;
            var trailer = ParseXrefSection(off);
            if (trailer is null) throw new PdfFormException("bad xref section");
            trailers.Add(trailer);
            if (trailer.Long("/XRefStm") is { } xs) queue.Enqueue(xs);
            if (trailer.Long("/Prev") is { } prev) queue.Enqueue(prev);
        }
        // merge trailers: first (newest) wins
        var merged = new PdfDictionary();
        foreach (var tr in trailers)
            foreach (var kv in tr.Entries)
                if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
        Trailer = merged;
        if (Trailer["/Root"] is null) throw new PdfFormException("trailer lacks /Root");
    }

    /// <summary>Parses either a classic "xref" table or an xref stream at the offset. Returns its trailer dictionary.</summary>
    private PdfDictionary? ParseXrefSection(long offset)
    {
        var lex = new Lexer(_data, (int)offset);
        lex.SkipWhitespace();
        if (ObjectParser.Matches(_data, lex.Position, "xref"))
        {
            lex.Position += 4;
            return ParseXrefTable(lex);
        }
        // xref stream: "n g obj <<...>> stream"
        var parser = new ObjectParser(lex, this);
        var obj = parser.ParseIndirectAt((int)offset, -1, out _, out _);
        if (obj is PdfStream xs && xs.Name("/Type") == "/XRef")
        {
            ParseXrefStream(xs);
            return xs;
        }
        return null;
    }

    private PdfDictionary? ParseXrefTable(Lexer lex)
    {
        while (true)
        {
            lex.SkipWhitespace();
            if (ObjectParser.Matches(_data, lex.Position, "trailer"))
            {
                lex.Position += 7;
                var parser = new ObjectParser(lex, this);
                return parser.ParseObject() as PdfDictionary ?? new PdfDictionary();
            }
            var t1 = lex.Next();
            if (t1.Kind != TokenKind.Number) return null;
            var t2 = lex.Next();
            if (t2.Kind != TokenKind.Number) return null;
            var start = ObjectParser.ToInt(Lexer.ParseNumber(t1.Text));
            var count = ObjectParser.ToInt(Lexer.ParseNumber(t2.Text));
            for (var i = 0; i < count; i++)
            {
                lex.SkipWhitespace();
                // tolerate subsection headers appearing early (broken count)
                var save = lex.Position;
                var a = lex.Next(); var b = lex.Next(); var c = lex.Next();
                if (a.Kind != TokenKind.Number || b.Kind != TokenKind.Number || c.Kind != TokenKind.Keyword)
                {
                    lex.Position = save;
                    break;
                }
                if (c.Text != "n" && c.Text != "f")
                {
                    // probably a new subsection header "start count" followed by entries
                    lex.Position = save;
                    break;
                }
                var num = start + i;
                if (c.Text == "n" && !_xref.ContainsKey(num))
                    _xref[num] = new XrefEntry((long)Lexer.ParseNumber(a.Text), 0, 0, false);
                else if (c.Text == "f" && !_xref.ContainsKey(num))
                    _xref[num] = new XrefEntry(-1, 0, 0, false); // free: marks as seen so older sections cannot resurrect it
            }
        }
    }

    private void ParseXrefStream(PdfStream xs)
    {
        var w = xs.Arr("/W") ?? throw new PdfFormException("xref stream lacks /W");
        var widths = new int[w.Count];
        for (var i = 0; i < w.Count; i++) widths[i] = Math.Clamp(w.IntAt(i), 0, 8);
        if (widths.Length < 2 || widths.Sum() == 0) throw new PdfFormException("xref stream has an invalid /W");
        var size = xs.Int("/Size") ?? 0;
        var index = new List<(int start, int count)>();
        var idx = xs.Arr("/Index");
        if (idx is null) index.Add((0, size));
        else for (var i = 0; i + 1 < idx.Count; i += 2) index.Add((idx.IntAt(i), idx.IntAt(i + 1)));
        var data = xs.Decoded();
        var rowLen = widths.Sum();
        var pos = 0;
        foreach (var (start, count) in index)
        {
            for (var i = 0; i < count; i++)
            {
                if (pos + rowLen > data.Length) return;
                long[] f = new long[widths.Length];
                for (var k = 0; k < widths.Length; k++)
                {
                    long v = 0;
                    for (var b = 0; b < widths[k]; b++) v = unchecked((v << 8) | data[pos++]);
                    f[k] = v;
                }
                var type = widths[0] == 0 ? 1 : f[0];
                var num = start + i;
                if (_xref.ContainsKey(num)) continue;
                switch (type)
                {
                    case 1: _xref[num] = new XrefEntry(f[1], 0, 0, false); break;
                    case 2: _xref[num] = new XrefEntry(0, unchecked((int)f[1]), widths.Length > 2 ? unchecked((int)f[2]) : 0, true); break;
                    case 0: _xref[num] = new XrefEntry(-1, 0, 0, false); break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ reconstruction

    private static readonly Regex ObjHeader = new(@"(?<!\d)(\d{1,10})\s+(\d{1,5})\s+obj\b", RegexOptions.Compiled);

    /// <summary>
    /// Scans the whole file for "n g obj" when the cross-reference is unusable.
    /// <paramref name="initial"/> = called while loading (trailer and caches are rebuilt). When called later, because a
    /// single offset turned out wrong, already-loaded objects and the trailer are kept so object identity is preserved.
    /// </summary>
    private void Reconstruct(bool initial)
    {
        if (_reconstructed) return;
        _reconstructed = true;
        _xref.Clear();
        _objStmCache.Clear();
        if (initial)
        {
            _cache.Clear();
            _numbers.Clear();
        }
        var text = Encoding.Latin1.GetString(_data);
        foreach (Match m in ObjHeader.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out var num) || num <= 0) continue;
            // cheap plausibility check so "1 0 obj" byte patterns inside binary streams are not mistaken for objects
            var q = m.Index + m.Length;
            while (q < _data.Length && Lexer.IsWhitespace(_data[q])) q++;
            if (q < _data.Length)
            {
                var c = _data[q];
                var plausible = c is (byte)'<' or (byte)'[' or (byte)'(' or (byte)'/' or (byte)'+' or (byte)'-' or (byte)'.' or (byte)'t' or (byte)'f' or (byte)'n' or (byte)'e' or (byte)'%'
                                || (c >= (byte)'0' && c <= (byte)'9');
                if (!plausible) continue;
            }
            _xref[num] = new XrefEntry(m.Index, 0, 0, false); // last occurrence wins
        }

        var newTrailer = new PdfDictionary();
        if (initial)
        {
            var tpos = 0;
            while ((tpos = text.IndexOf("trailer", tpos, StringComparison.Ordinal)) >= 0)
            {
                try
                {
                    var parser = new ObjectParser(new Lexer(_data, tpos + 7), this);
                    if (parser.ParseObject() is PdfDictionary td)
                        foreach (var kv in td.Entries) newTrailer[kv.Key] = kv.Value; // later trailers override
                }
                catch (PdfFormException) { /* garbage after a "trailer" keyword */ }
                tpos += 7;
            }
        }

        // pass 1: cross-reference streams carry the trailer keys and are never encrypted
        var objStms = new List<int>();
        foreach (var num in _xref.Keys.ToList())
        {
            PdfObject? obj;
            try { obj = Peek(num); } catch (PdfFormException) { continue; }
            if (obj is not PdfStream s) continue;
            var type = s.Name("/Type"); // names are readable even when the file is encrypted
            if (type == "/XRef")
            {
                foreach (var kv in s.Entries)
                    if (kv.Key is "/Root" or "/Info" or "/ID" or "/Encrypt" && !newTrailer.ContainsKey(kv.Key)) newTrailer[kv.Key] = kv.Value;
            }
            else if (type == "/ObjStm") objStms.Add(num);
        }

        if (initial)
        {
            Trailer = newTrailer;
            SetupDecryption(); // object streams below are encrypted when the file is
        }

        // pass 2: objects that live inside object streams
        foreach (var num in objStms)
        {
            try
            {
                if (GetObject(num) is not PdfStream s) continue;
                foreach (var inner in ObjectStreamMembers(s))
                    if (!_xref.ContainsKey(inner.Key)) _xref[inner.Key] = new XrefEntry(0, num, inner.Value, true);
            }
            catch (PdfFormException) { /* skip a broken object stream */ }
        }

        if (initial && PdfExtCore.Resolve(Trailer["/Root"]) is not PdfDictionary)
        {
            // no usable trailer: find a catalog by brute force
            foreach (var num in _xref.Keys.OrderBy(k => k))
            {
                PdfObject? obj;
                try { obj = GetObject(num); } catch (PdfFormException) { continue; }
                if (obj is PdfDictionary d && d.Name("/Type") == "/Catalog") { Trailer["/Root"] = new PdfReference(this, num, 0); break; }
            }
        }
    }

    /// <summary>Parses an object straight from the file without caching or decrypting it.</summary>
    private PdfObject? Peek(int number)
        => _xref.TryGetValue(number, out var e) && !e.InStream ? LoadAtOffset(e.Offset, number) : null;

    // ------------------------------------------------------------------ object access

    public PdfObject GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached)) return cached;
        if (!_xref.TryGetValue(number, out var entry)) return PdfNull.Instance;
        if (!_loading.Add(number)) return PdfNull.Instance; // cycle (e.g. /Length pointing at itself)
        try
        {
            PdfObject obj;
            if (entry.InStream) obj = LoadFromObjectStream(entry.StreamNumber, number);
            else
            {
                obj = LoadAtOffset(entry.Offset, number) ?? (TryReconstructThenLoad(number) ?? PdfNull.Instance);
            }
            _cache[number] = obj;
            if (obj is not PdfNull) _numbers[obj] = number;
            if (_decryptor is not null && !entry.InStream && number != _encryptNumber) _decryptor.DecryptObject(obj, number, 0);
            return obj;
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    private PdfObject? TryReconstructThenLoad(int number)
    {
        if (_reconstructed) return null;
        Reconstruct(initial: false);
        if (!_xref.TryGetValue(number, out var entry)) return null;
        return entry.InStream ? LoadFromObjectStream(entry.StreamNumber, number) : LoadAtOffset(entry.Offset, number);
    }

    private PdfObject? LoadAtOffset(long offset, int number)
    {
        if (offset < 0 || offset >= _data.Length || offset > int.MaxValue) return null;
        var parser = new ObjectParser(new Lexer(_data), this);
        var obj = parser.ParseIndirectAt((int)offset, number, out _, out _);
        if (obj is not null) return obj;
        // off-by-a-few offsets are common: look around
        var text = Encoding.Latin1.GetString(_data, (int)Math.Max(0, offset - 40), (int)Math.Min(_data.Length - Math.Max(0, offset - 40), 200));
        var m = Regex.Match(text, @"(?<!\d)" + number + @"\s+\d+\s+obj\b");
        if (m.Success) return parser.ParseIndirectAt((int)Math.Max(0, offset - 40) + m.Index, number, out _, out _);
        return null;
    }

    private PdfObject LoadFromObjectStream(int streamNumber, int number)
    {
        if (!_objStmCache.TryGetValue(streamNumber, out var members))
        {
            if (GetObject(streamNumber) is not PdfStream s) return PdfNull.Instance;
            members = ParseObjectStream(s);
            _objStmCache[streamNumber] = members;
        }
        return members.TryGetValue(number, out var o) ? o : PdfNull.Instance;
    }

    private Dictionary<int, PdfObject> ParseObjectStream(PdfStream s)
    {
        var result = new Dictionary<int, PdfObject>();
        var data = s.Decoded();
        var n = s.Int("/N") ?? 0;
        var first = s.Int("/First") ?? 0;
        var hdr = new Lexer(data);
        var offsets = new List<(int num, int off)>();
        for (var i = 0; i < n; i++)
        {
            var a = hdr.Next(); var b = hdr.Next();
            if (a.Kind != TokenKind.Number || b.Kind != TokenKind.Number) break;
            offsets.Add((ObjectParser.ToInt(Lexer.ParseNumber(a.Text)), ObjectParser.ToInt(Lexer.ParseNumber(b.Text))));
        }
        foreach (var (num, off) in offsets)
        {
            var p = first + off;
            if (p < 0 || p >= data.Length) continue;
            var parser = new ObjectParser(new Lexer(data, p), this);
            var obj = parser.ParseObject() ?? PdfNull.Instance;
            result[num] = obj;
        }
        return result;
    }

    private static IEnumerable<KeyValuePair<int, int>> ObjectStreamMembers(PdfStream s)
    {
        var data = s.Decoded();
        var n = s.Int("/N") ?? 0;
        var hdr = new Lexer(data);
        for (var i = 0; i < n; i++)
        {
            var a = hdr.Next(); var b = hdr.Next();
            if (a.Kind != TokenKind.Number || b.Kind != TokenKind.Number) yield break;
            yield return new KeyValuePair<int, int>(ObjectParser.ToInt(Lexer.ParseNumber(a.Text)), i);
        }
    }

    // ------------------------------------------------------------------ allocation

    /// <summary>Registers an object as indirect (if it is not already) and returns a reference to it.</summary>
    public PdfReference RefFor(PdfObject obj)
    {
        if (obj is PdfReference r) return r;
        if (_numbers.TryGetValue(obj, out var num)) return new PdfReference(this, num, 0);
        num = _nextNumber++;
        _numbers[obj] = num;
        _cache[num] = obj;
        _xref[num] = new XrefEntry(-1, 0, 0, false);
        return new PdfReference(this, num, 0);
    }

    public bool IsIndirect(PdfObject obj) => _numbers.ContainsKey(obj);

    // ------------------------------------------------------------------ pages

    private static readonly string[] Inheritable = { "/Resources", "/MediaBox", "/CropBox", "/Rotate" };

    public IReadOnlyList<PdfDictionary> Pages => _pages ??= CollectPages();

    public void InvalidatePages() => _pages = null;

    private List<PdfDictionary> CollectPages()
    {
        var list = new List<PdfDictionary>();
        var root = Catalog.Dict("/Pages");
        if (root is null) return list;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        Walk(root, new Dictionary<string, PdfObject>(), list, seen, 0);
        return list;
    }

    private void Walk(PdfDictionary node, Dictionary<string, PdfObject> inherited, List<PdfDictionary> list, HashSet<PdfDictionary> seen, int depth)
    {
        if (!seen.Add(node) || depth > 64) return;
        var inh = new Dictionary<string, PdfObject>(inherited);
        foreach (var k in Inheritable) if (node[k] is { } v) inh[k] = v;
        var type = node.Name("/Type");
        var kids = node.Arr("/Kids");
        if (type == "/Page" || (kids is null && type != "/Pages" && node.ContainsKey("/Contents")))
        {
            foreach (var kv in inh) if (!node.ContainsKey(kv.Key)) node[kv.Key] = kv.Value;
            list.Add(node);
            return;
        }
        if (kids is null) return;
        foreach (var kid in kids.Dicts()) Walk(kid, inh, list, seen, depth + 1);
    }

    public void RemovePage(int index)
    {
        var node = Pages[index];
        var parent = node.Dict("/Parent");
        var guard = 0;
        // detach from the immediate parent, collapsing intermediate nodes that become empty
        while (parent is not null && guard++ < 64)
        {
            var kids = parent.Arr("/Kids");
            if (kids is not null) kids.RemoveObjectRefCore(node);
            parent["/Count"] = new PdfNumber(Math.Max(0, (parent.Int("/Count") ?? 1) - 1));
            var gp = parent.Dict("/Parent");
            if ((parent.Arr("/Kids")?.Count ?? 0) == 0 && gp is not null)
            {
                node = parent; parent = gp;
                continue;
            }
            // remaining ancestors: only the count changes
            var anc = gp;
            while (anc is not null && guard++ < 64)
            {
                anc["/Count"] = new PdfNumber(Math.Max(0, (anc.Int("/Count") ?? 1) - 1));
                anc = anc.Dict("/Parent");
            }
            break;
        }
        InvalidatePages();
    }

    /// <summary>Appends an already-built page dictionary (owned by this document) to the root page tree.</summary>
    public void AppendPage(PdfDictionary page)
    {
        var root = Catalog.Dict("/Pages");
        if (root is null)
        {
            root = new PdfDictionary();
            root["/Type"] = new PdfName("/Pages");
            root["/Kids"] = new PdfArray();
            root["/Count"] = new PdfNumber(0);
            Catalog["/Pages"] = RefFor(root);
        }
        var kids = root.Arr("/Kids");
        if (kids is null) { kids = new PdfArray(); root["/Kids"] = kids; }
        page["/Parent"] = RefFor(root);
        kids.Add(RefFor(page));
        root["/Count"] = new PdfNumber((root.Int("/Count") ?? 0) + 1);
        InvalidatePages();
    }

    /// <summary>Rewrites the page tree as one flat node holding the given pages in the given order.</summary>
    public void SetPageOrder(IReadOnlyList<PdfDictionary> order)
    {
        _ = Pages; // makes sure inherited attributes are copied onto every page before the tree is flattened
        var root = Catalog.Dict("/Pages") ?? throw new PdfFormException("The document has no page tree.");
        var rootRef = RefFor(root);
        var kids = new PdfArray();
        foreach (var page in order)
        {
            page["/Parent"] = rootRef;
            kids.Add(RefFor(page));
        }
        root["/Kids"] = kids;
        root["/Count"] = new PdfNumber(order.Count);
        foreach (var k in Inheritable) root.Remove(k);
        InvalidatePages();
    }

    public byte[] Save() => PdfWriter.Write(this);
}
