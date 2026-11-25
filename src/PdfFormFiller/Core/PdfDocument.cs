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
    private int _nextNumber = -1; // new objects count downwards, so they can never collide with a number used in the file
    private bool _reconstructed;
    private List<PdfDictionary>? _pages;

    public PdfDictionary Trailer { get; private set; } = new();
    public PdfDictionary Catalog { get; private set; } = new();

    private readonly record struct XrefEntry(long Offset, int StreamNumber, bool InStream)
    {
        public bool Free => !InStream && Offset < 0;
        public static XrefEntry At(long offset) => new(offset, 0, false);
        public static XrefEntry Within(int streamNumber) => new(0, streamNumber, true);
        public static readonly XrefEntry FreeEntry = new(-1, 0, false); // recorded so older sections cannot resurrect the object
    }

    /// <summary>Objects nested this deep while loading (a stream whose /Length is another stream, and so on) are hostile.</summary>
    private const int MaxNestedLoads = 32;

    // Budgets against small files made to cost unbounded work: overlapping objects are parsed over and over, and object
    // streams of a few kilobytes can expand into gigabytes of objects. Both limits are far above what real files need.
    private long _parsedBytes;
    private long _structuralBytes;
    private long MaxParsedBytes => 8L * _data.Length + (1 << 20);
    private long MaxStructuralBytes => Math.Max(32L << 20, 64L * _data.Length);
    private long MaxStructuralStream => Math.Min(Filters.MaxDecodedBytes, Math.Max(16L << 20, 16L * _data.Length));
    private int MaxXrefEntries => (int)Math.Min(8_388_608, 65_536L + _data.Length);

    private PdfDocument(byte[] data) => _data = data;

    // ------------------------------------------------------------------ loading

    /// <param name="data">The file.</param>
    /// <param name="password">User or owner password. Not needed, and ignored, when the file opens without one. Not retained.</param>
    public static PdfDocument Load(byte[] data, string? password = null)
    {
        var doc = new PdfDocument(data);
        doc.Init(password);
        return doc;
    }

    private void Init(string? password)
    {
        var ok = false;
        try
        {
            ParseXrefChain();
            ok = _xref.Count > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // fall through to reconstruction
        }
        if (!ok)
        {
            Reconstruct(initial: true, password);
        }

        // Must happen before anything else is read: the catalog may sit inside an encrypted object stream.
        SetupDecryption(password);

        var root = UsableRoot();
        if (root is null && !_reconstructed)
        {
            // the cross-reference parsed but points at the wrong things
            Reconstruct(initial: true, password);
            SetupDecryption(password);
            root = UsableRoot();
        }
        root ??= FindCatalog() ?? PdfExtCore.Resolve(Trailer["/Root"]) as PdfDictionary;
        Catalog = root ?? throw new PdfFormException("Could not find the document catalog; the file is not a usable PDF.");
    }

    private PdfDictionary? UsableRoot()
        => PdfExtCore.Resolve(Trailer["/Root"]) is PdfDictionary root && root.Dict("/Pages") is not null ? root : null;

    /// <summary>No usable trailer: looks through every object for a catalog. The one stored last in the file is the newest.</summary>
    private PdfDictionary? FindCatalog()
    {
        PdfDictionary? best = null;
        var bestRank = (HasPages: false, Position: -1L);
        foreach (var (num, entry) in _xref.ToList())
        {
            try
            {
                if (GetObject(num) is not PdfDictionary d || d.Name("/Type") != "/Catalog") continue;
                var rank = (d.Dict("/Pages") is not null, PositionOf(entry));
                if (rank.CompareTo(bestRank) <= 0) continue;
                best = d;
                bestRank = rank;
                Trailer["/Root"] = new PdfReference(this, num, 0);
            }
            catch (PdfFormException) { /* unreadable object */ }
        }
        return best;
    }

    private long PositionOf(XrefEntry entry)
        => !entry.InStream ? entry.Offset : _xref.TryGetValue(entry.StreamNumber, out var s) && !s.InStream ? s.Offset : 0;

    private int _encryptNumber; // object number of the encryption dictionary, 0 when it is not indirect

    /// <summary>Creates the decryptor from the trailer's /Encrypt entry. Idempotent.</summary>
    private void SetupDecryption(string? password)
    {
        if (_decryptor is not null)
        {
            Trailer.Remove("/Encrypt");
            return;
        }
        var encRaw = Trailer["/Encrypt"];
        if (PdfExtCore.Resolve(encRaw) is not PdfDictionary enc)
        {
            if (encRaw is PdfReference && !_reconstructed)
            {
                // a lost encryption dictionary: the cross-reference is wrong
                Reconstruct(initial: true, password);
                return;
            }
            Trailer.Remove("/Encrypt"); // names nothing: the file is not encrypted
            ListPendingObjectStreams();
            return;
        }
        var ids = Trailer.Arr("/ID");
        var id0 = ids is not null && ids.Count > 0 && PdfExtCore.Resolve(ids[0]) is PdfString s ? s.Bytes : Array.Empty<byte>();
        _encryptNumber = encRaw is PdfReference er ? er.Number : 0;
        _decryptor = Decryptor.Create(enc, id0, password);

        // Everything read so far was read without the key. Forget it; it is re-read and decrypted on demand.
        _cache.Clear();
        _numbers.Clear();
        _objStmCache.Clear();
        if (_encryptNumber > 0)
        {
            _cache[_encryptNumber] = enc; // the encryption dictionary itself is never encrypted
            _numbers[enc] = _encryptNumber;
        }
        Trailer.Remove("/Encrypt");
        ListPendingObjectStreams();
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
        var freed = new List<int>();

        PdfDictionary Section(long off)
        {
            // a section that cannot be read means missing objects: better to rebuild the whole table
            if (off < 0 || off >= _data.Length) throw new PdfFormException("xref section outside the file");
            var trailer = ParseXrefSection(off, freed) ?? throw new PdfFormException("bad xref section");
            trailers.Add(trailer);
            return trailer;
        }

        while (queue.Count > 0)
        {
            var off = queue.Dequeue();
            if (off == 0 || !seen.Add(off)) continue; // "/Prev 0" is a common way to say "none"
            freed.Clear();
            var trailer = Section(off);
            if (trailer.Long("/Prev") is { } prev) queue.Enqueue(prev);
            // hybrid file: the table lists the objects hidden in the xref stream as free, so the stream goes first
            if (trailer.Long("/XRefStm") is { } xs && xs != 0 && seen.Add(xs) && Section(xs).Long("/Prev") is { } xsPrev) queue.Enqueue(xsPrev);
            foreach (var num in freed) AddEntry(num, XrefEntry.FreeEntry);
        }
        // merge trailers: first (newest) wins
        var merged = new PdfDictionary();
        foreach (var tr in trailers)
            foreach (var kv in tr.Entries)
                if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
        Trailer = merged;
        if (Trailer["/Root"] is null) throw new PdfFormException("trailer lacks /Root");
    }

    /// <summary>Records an entry unless a newer section already has one for the object.</summary>
    private void AddEntry(long num, XrefEntry entry)
    {
        if (num is < 0 or > int.MaxValue || !_xref.TryAdd((int)num, entry)) return;
        if (_xref.Count > MaxXrefEntries) throw new PdfFormException("The cross-reference lists more objects than the file can hold.");
    }

    /// <summary>
    /// Parses either a classic "xref" table or an xref stream at the offset. Returns its trailer dictionary. Free entries of
    /// a classic table are only collected, see the hybrid file handling in <see cref="ParseXrefChain"/>.
    /// </summary>
    private PdfDictionary? ParseXrefSection(long offset, List<int> freed)
    {
        var lex = new Lexer(_data, (int)offset);
        lex.SkipWhitespace();
        if (ObjectParser.Matches(_data, lex.Position, "xref"))
        {
            lex.Position += 4;
            return ParseXrefTable(lex, freed);
        }
        // xref stream: "n g obj <<...>> stream"
        var parser = new ObjectParser(lex, this);
        var obj = ParseIndirect(parser, (int)offset, -1, out _);
        if (obj is PdfStream xs && xs.Name("/Type") == "/XRef")
        {
            ParseXrefStream(xs);
            return xs;
        }
        return null;
    }

    private PdfDictionary? ParseXrefTable(Lexer lex, List<int> freed)
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
                // the head of the free list is always object 0; some writers number it 1 and shift everything after it
                if (i == 0 && start == 1 && c.Text == "f" && Lexer.ParseNumber(b.Text) == 65535) start = 0;
                var num = (long)start + i;
                if (c.Text == "n") AddEntry(num, XrefEntry.At((long)Lexer.ParseNumber(a.Text)));
                else if (num is >= 0 and <= int.MaxValue) freed.Add((int)num);
            }
        }
    }

    private void ParseXrefStream(PdfStream xs)
    {
        var w = xs.Arr("/W") ?? throw new PdfFormException("xref stream lacks /W");
        if (w.Count is < 2 or > 16) throw new PdfFormException("xref stream has an invalid /W");
        var widths = new int[w.Count];
        for (var i = 0; i < w.Count; i++) widths[i] = Math.Clamp(w.IntAt(i), 0, 8);
        if (widths.Sum() == 0) throw new PdfFormException("xref stream has an invalid /W");
        var size = xs.Int("/Size") ?? 0;
        var index = new List<(int start, int count)>();
        var idx = xs.Arr("/Index");
        if (idx is null) index.Add((0, size));
        else for (var i = 0; i + 1 < idx.Count; i += 2) index.Add((idx.IntAt(i), idx.IntAt(i + 1)));
        var data = DecodeStructural(xs);
        var rowLen = widths.Sum();
        var pos = 0;
        var f = new long[widths.Length];
        foreach (var (start, count) in index)
        {
            for (var i = 0; i < count; i++)
            {
                if (pos + rowLen > data.Length) return;
                for (var k = 0; k < widths.Length; k++)
                {
                    long v = 0;
                    for (var b = 0; b < widths[k]; b++) v = unchecked((v << 8) | data[pos++]);
                    f[k] = v;
                }
                var type = widths[0] == 0 ? 1 : f[0];
                var num = (long)start + i;
                switch (type)
                {
                    case 1: AddEntry(num, XrefEntry.At(f[1])); break;
                    case 2: AddEntry(num, XrefEntry.Within(unchecked((int)f[1]))); break;
                    case 0: AddEntry(num, XrefEntry.FreeEntry); break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ reconstruction

    private static readonly Regex ObjHeader = new(@"(?<!\d)(\d{1,10})\s+(\d{1,5})\s+obj\b", RegexOptions.Compiled);

    /// <summary>Object streams (number, offset) found by <see cref="Reconstruct"/> whose members are not in the xref yet.</summary>
    private List<(int Number, int Offset)> _pendingObjStms = new();

    /// <summary>
    /// Scans the whole file for "n g obj" when the cross-reference is unusable.
    /// <paramref name="initial"/> = called while loading (trailer and caches are rebuilt). When called later, because a
    /// single offset turned out wrong, already-loaded objects and the trailer are kept so object identity is preserved.
    /// </summary>
    private void Reconstruct(bool initial, string? password = null)
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
        var headers = new List<(int Number, int Offset)>();
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
            headers.Add((num, m.Index));
            _xref[num] = XrefEntry.At(m.Index); // last occurrence wins
        }

        // Pass 1: look at the objects in file order. Headers inside a stream with a trustworthy /Length belong to an embedded
        // file, not to this one. Cross-reference streams carry the trailer keys and are never encrypted.
        var cachedBefore = new HashSet<int>(_cache.Keys);
        var kept = new List<(int Number, int Offset)>();
        var streams = new List<(int Start, int End)>();
        var trailers = new List<(int Offset, PdfDictionary Dict)>();
        var objStms = new List<(int Number, int Offset)>();
        PdfReference? strayEncrypt = null;
        var skipUntil = 0;
        foreach (var h in headers)
        {
            if (h.Offset < skipUntil) continue;
            kept.Add(h);
            var parser = new ObjectParser(new Lexer(_data), this);
            PdfObject? obj;
            try { obj = ParseIndirect(parser, h.Offset, h.Number, out _); } catch (PdfFormException) { continue; }
            if (obj is PdfStream s)
            {
                if (parser.StreamLengthVerified)
                {
                    skipUntil = parser.Lexer.Position;
                    streams.Add((h.Offset, skipUntil));
                }
                var type = s.Name("/Type"); // names are readable even when the file is encrypted
                if (type == "/XRef") trailers.Add((h.Offset, s));
                else if (type == "/ObjStm") objStms.Add(h);
            }
            else if (obj is PdfDictionary d && d.Name("/Filter") == "/Standard" && d.ContainsKey("/O") && d.ContainsKey("/U"))
                strayEncrypt = new PdfReference(this, h.Number, 0);
        }
        _xref.Clear();
        foreach (var (num, offset) in kept) _xref[num] = XrefEntry.At(offset);
        // the scan may have cached indirect /Length values looked up through the provisional table
        foreach (var num in _cache.Keys.Where(k => !cachedBefore.Contains(k)).ToList())
        {
            _numbers.Remove(_cache[num]);
            _cache.Remove(num);
        }
        _pendingObjStms = objStms.Where(o => _xref[o.Number].Offset == o.Offset).ToList();

        if (initial)
        {
            var tpos = 0;
            var inside = 0;
            while ((tpos = text.IndexOf("trailer", tpos, StringComparison.Ordinal)) >= 0)
            {
                while (inside < streams.Count && streams[inside].End <= tpos) inside++;
                if (inside >= streams.Count || streams[inside].Start > tpos)
                {
                    var lex = new Lexer(_data, tpos + 7);
                    try
                    {
                        if (_parsedBytes > MaxParsedBytes) break;
                        if (new ObjectParser(lex, this).ParseObject() is PdfDictionary td) trailers.Add((tpos, td));
                    }
                    catch (PdfFormException) { /* garbage after a "trailer" keyword */ }
                    _parsedBytes += Math.Max(0, lex.Position - tpos);
                }
                tpos += 7;
            }
            var newTrailer = new PdfDictionary();
            foreach (var (_, dict) in trailers.OrderBy(t => t.Offset)) // the newest is last and wins
                foreach (var kv in dict.Entries)
                    if (kv.Key is "/Root" or "/Info" or "/ID" or "/Encrypt") newTrailer[kv.Key] = kv.Value;
            // no trailer survived: without its encryption dictionary an encrypted file would load as garbage
            if (trailers.Count == 0 && strayEncrypt is not null) newTrailer["/Encrypt"] = strayEncrypt;
            Trailer = newTrailer;
            SetupDecryption(password); // object streams are encrypted when the file is
        }
        // While the encryption dictionary itself is being located, the streams cannot be read yet: SetupDecryption gets back to them.
        if (_decryptor is not null || Trailer["/Encrypt"] is null) ListPendingObjectStreams();
    }

    /// <summary>Pass 2 of reconstruction: objects that live inside object streams. Whatever is stored last in the file wins.</summary>
    private void ListPendingObjectStreams()
    {
        var pending = _pendingObjStms;
        _pendingObjStms = new List<(int Number, int Offset)>();
        foreach (var (num, offset) in pending)
        {
            try
            {
                // not through GetObject: this may run while the very same stream is being fetched
                if (LoadAtOffset(offset, num, out var generation) is not PdfStream s) continue;
                _decryptor?.DecryptObject(s, num, generation);
                foreach (var inner in ObjectStreamMembers(s))
                {
                    if (inner <= 0 || inner == num || (_xref.TryGetValue(inner, out var e) && PositionOf(e) > offset)) continue;
                    _xref[inner] = XrefEntry.Within(num);
                }
            }
            catch (PdfFormException) { /* skip a broken object stream */ }
        }
    }

    // ------------------------------------------------------------------ object access

    public PdfObject GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached)) return cached;
        if (!_xref.ContainsKey(number) || _loading.Count >= MaxNestedLoads) return PdfNull.Instance;
        if (!_loading.Add(number)) return PdfNull.Instance; // cycle (e.g. /Length pointing at itself)
        try
        {
            var obj = Fetch(number, out var generation) ?? PdfNull.Instance;
            _cache[number] = obj;
            if (obj is not PdfNull) _numbers[obj] = number;
            if (_decryptor is not null && generation >= 0 && number != _encryptNumber) _decryptor.DecryptObject(obj, number, generation);
            return obj;
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    /// <summary>Reads an object from the file. <paramref name="generation"/> is -1 for a member of an object stream (not encrypted by itself).</summary>
    private PdfObject? Fetch(int number, out int generation)
    {
        generation = -1;
        var entry = _xref[number];
        if (entry.Free) return null;
        var hadReconstructed = _reconstructed;
        var obj = LoadEntry(entry, number, out generation);
        if (obj is not null || hadReconstructed) return obj;
        Reconstruct(initial: false); // does nothing when the attempt above already led here; the entry is new either way
        return _xref.TryGetValue(number, out entry) ? LoadEntry(entry, number, out generation) : null;
    }

    private PdfObject? LoadEntry(XrefEntry entry, int number, out int generation)
    {
        generation = -1;
        return entry.InStream ? LoadFromObjectStream(entry.StreamNumber, number) : LoadAtOffset(entry.Offset, number, out generation);
    }

    private PdfObject? ParseIndirect(ObjectParser parser, int offset, int expectedNumber, out int generation)
    {
        if (_parsedBytes > MaxParsedBytes) throw new PdfFormException("The objects in the PDF overlap; the file is too damaged to read.");
        try { return parser.ParseIndirectAt(offset, expectedNumber, out _, out generation); }
        finally { _parsedBytes += Math.Max(0, parser.Lexer.Position - offset); }
    }

    /// <summary>Decodes an object or cross-reference stream, without keeping the decoded copy.</summary>
    private byte[] DecodeStructural(PdfStream s)
    {
        if (_structuralBytes <= MaxStructuralBytes)
        {
            var data = Filters.Decode(s, MaxStructuralStream);
            _structuralBytes += data.Length;
            if (_structuralBytes <= MaxStructuralBytes) return data;
        }
        throw new PdfFormException("The object streams in the PDF are out of proportion to the file; refusing to decode them.");
    }

    private PdfObject? LoadAtOffset(long offset, int number, out int generation)
    {
        generation = -1;
        if (offset < 0 || offset >= _data.Length || offset > int.MaxValue) return null;
        var parser = new ObjectParser(new Lexer(_data), this);
        var obj = ParseIndirect(parser, (int)offset, number, out generation);
        if (obj is not null) return obj;
        // off-by-a-few offsets are common: look around
        var text = Encoding.Latin1.GetString(_data, (int)Math.Max(0, offset - 40), (int)Math.Min(_data.Length - Math.Max(0, offset - 40), 200));
        var m = Regex.Match(text, @"(?<!\d)" + number + @"\s+\d+\s+obj\b");
        if (m.Success) return ParseIndirect(parser, (int)Math.Max(0, offset - 40) + m.Index, number, out generation);
        return null;
    }

    private PdfObject? LoadFromObjectStream(int streamNumber, int number)
    {
        if (!_objStmCache.TryGetValue(streamNumber, out var members))
        {
            if (GetObject(streamNumber) is not PdfStream s) return null;
            members = ParseObjectStream(s);
            _objStmCache[streamNumber] = members;
        }
        return members.TryGetValue(number, out var o) ? o : null;
    }

    private Dictionary<int, PdfObject> ParseObjectStream(PdfStream s)
    {
        var result = new Dictionary<int, PdfObject>();
        var data = DecodeStructural(s);
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
        var budget = 4L * data.Length + 65_536; // members do not overlap in a sane stream
        foreach (var (num, off) in offsets)
        {
            var p = (long)first + off;
            if (p < 0 || p >= data.Length) continue;
            var lex = new Lexer(data, (int)p);
            result[num] = new ObjectParser(lex, this).ParseObject() ?? PdfNull.Instance;
            budget -= lex.Position - p;
            if (budget < 0) throw new PdfFormException("The objects in an object stream overlap; the file is too damaged to read.");
        }
        return result;
    }

    private IEnumerable<int> ObjectStreamMembers(PdfStream s)
    {
        var data = DecodeStructural(s);
        var n = s.Int("/N") ?? 0;
        var hdr = new Lexer(data);
        for (var i = 0; i < n; i++)
        {
            var a = hdr.Next(); var b = hdr.Next();
            if (a.Kind != TokenKind.Number || b.Kind != TokenKind.Number) yield break;
            yield return ObjectParser.ToInt(Lexer.ParseNumber(a.Text));
        }
    }

    // ------------------------------------------------------------------ allocation

    /// <summary>Registers an object as indirect (if it is not already) and returns a reference to it.</summary>
    public PdfReference RefFor(PdfObject obj)
    {
        if (obj is PdfReference r) return r;
        if (_numbers.TryGetValue(obj, out var num)) return new PdfReference(this, num, 0);
        num = _nextNumber--;
        _numbers[obj] = num;
        _cache[num] = obj;
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
        // an explicit stack, not recursion: the depth of the tree is up to the file
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Node, Dictionary<string, PdfObject> Inherited)>();
        stack.Push((root, new Dictionary<string, PdfObject>()));
        while (stack.Count > 0)
        {
            var (node, inherited) = stack.Pop();
            if (!seen.Add(node)) continue;
            var inh = new Dictionary<string, PdfObject>(inherited);
            foreach (var k in Inheritable) if (node[k] is { } v) inh[k] = v;
            var type = node.Name("/Type");
            var kids = node.Arr("/Kids");
            if (type == "/Page" || (kids is null && type != "/Pages" && node.ContainsKey("/Contents")))
            {
                foreach (var kv in inh) if (!node.ContainsKey(kv.Key)) node[kv.Key] = kv.Value;
                list.Add(node);
            }
            else if (kids is not null)
                foreach (var kid in kids.Dicts().Reverse()) stack.Push((kid, inh));
        }
        return list;
    }

    public void RemovePage(int index) => SetPageOrder(Pages.Where((_, i) => i != index).ToList());

    /// <summary>Appends an already-built page dictionary (owned by this document) to the root page tree.</summary>
    public void AppendPage(PdfDictionary page)
    {
        _ = Pages; // copies inherited attributes onto the existing pages, so the root can drop them: the new page must not inherit them
        var root = Catalog.Dict("/Pages");
        if (root is null || Pages.Count == 1 && ReferenceEquals(Pages[0], root)) // missing, or pointing straight at the only page
        {
            root = new PdfDictionary();
            root["/Type"] = new PdfName("/Pages");
            root["/Kids"] = new PdfArray(Pages.Select(RefFor));
            root["/Count"] = new PdfNumber(Pages.Count);
            foreach (var p in Pages) p["/Parent"] = RefFor(root);
        }
        Catalog["/Pages"] = RefFor(root); // the tree root may have been a direct object
        foreach (var k in Inheritable) root.Remove(k);
        var kids = root.Arr("/Kids");
        if (kids is null) { kids = new PdfArray(); root["/Kids"] = kids; }
        page["/Parent"] = RefFor(root);
        kids.Add(RefFor(page));
        root["/Count"] = new PdfNumber((root.Int("/Count") ?? 0) + 1);
        _pages!.Add(page);
    }

    /// <summary>Rewrites the page tree as one flat node holding the given pages in the given order.</summary>
    public void SetPageOrder(IReadOnlyList<PdfDictionary> order)
    {
        _ = Pages; // makes sure inherited attributes are copied onto every page before the tree is flattened
        var root = Catalog.Dict("/Pages") ?? throw new PdfFormException("The document has no page tree.");
        var rootRef = RefFor(root);
        Catalog["/Pages"] = rootRef; // the tree root may have been a direct object
        var kids = new PdfArray();
        foreach (var page in order)
        {
            page["/Parent"] = rootRef;
            kids.Add(RefFor(page));
        }
        root["/Kids"] = kids;
        root["/Count"] = new PdfNumber(order.Count);
        foreach (var k in Inheritable) root.Remove(k);
        _pages = order.ToList();
    }

    public byte[] Save() => PdfWriter.Write(this);
}
