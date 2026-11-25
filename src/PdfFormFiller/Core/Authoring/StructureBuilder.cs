namespace PdfFormFiller.Core;

/// <summary>Standard structure types. Table parts must be nested as Table &gt; THead/TBody/TFoot &gt; TR &gt; TH/TD (TR may also sit directly in Table).</summary>
internal enum StructType : byte
{
    Document, Sect, Div, H1, H2, H3, P, Span, Link, Figure, Caption, Table, THead, TBody, TFoot, TR, TH, TD,
}

/// <summary>What a table header cell is a header for.</summary>
internal enum HeaderScope : byte { None, Column, Row, Both }

/// <summary>A structure element of a <see cref="StructureBuilder"/>.</summary>
internal readonly record struct StructId(int Index);

/// <summary>
/// The tag tree of a generated document, see <see cref="PdfBuilder.Structure"/>. Elements are read by assistive technology in
/// the order they are added to their parent. Content is attached with <see cref="ContentWriter.BeginTagged"/>; a table that
/// continues on the next page just keeps adding rows to the same TBody (draw the repeated header row as an artifact).
/// <para>
/// Elements are kept in compact arrays, about 40 bytes per table cell including its content, and written straight to the
/// file, so a million cells cost about 40 MB here. Readers walk all rows of a TBody for every page they show, so long bodies
/// are divided into several TBody elements of <see cref="RowsPerBody"/> rows in the same table, which PDF allows; callers keep
/// using the one they created. pdf.js (Firefox) still reads a whole Table for every page: about 0.25 s per page at 10,000 rows
/// and 3 s at 100,000 rows of 8 columns. Several smaller tables, one per group for example, avoid that. The division needs the rows to be in a TBody, and the TFoot to be added after them. At most 4,000,000 elements. In a document created without tagging every
/// method is accepted and does nothing.
/// </para>
/// </summary>
internal sealed class StructureBuilder
{
    /// <summary>More than this would exceed what common readers (and the loader in this library) accept as object numbers.</summary>
    public const int MaxElements = 4_000_000;

    /// <summary>Rows per TBody element in the file.</summary>
    public const int RowsPerBody = 250;

    internal static readonly string[] Names = Enum.GetNames<StructType>().Select(n => "/" + n).ToArray();

    private record struct Element(int Parent, int First, int Last, StructType Type, HeaderScope Scope);

    /// <summary>One kid of an element: a child element (A), marked content (A = page, B = mcid) or an annotation (A = index).</summary>
    private record struct Item(int Next, int A, int B);
    private const int ChildElement = -1;
    private const int AnnotationItem = -2;

    private sealed class Extra
    {
        public string? Alt, ActualText, Language, Title;
        public int ColSpan = 1, RowSpan = 1;
        public (double X, double Y, double W, double H)? Box;
    }

    private readonly PdfBuilder _builder;
    private readonly ChunkedList<Element> _elements = new();
    private readonly ChunkedList<Item> _items = new();
    private readonly Dictionary<int, Extra> _extras = new();
    private readonly List<(PdfDictionary Annotation, PdfPageBuilder Page)> _annotations = new();
    private readonly SortedDictionary<int, int> _annotationKeys = new(); // StructParent key -> element
    private readonly Dictionary<int, (int Current, int Rows)> _bodies = new(); // TBody as the caller knows it -> the one being filled
    private int _nextKey;

    internal StructureBuilder(PdfBuilder builder)
    {
        _builder = builder;
        if (builder.Tagged) _elements.Add(new Element(-1, -1, -1, StructType.Document, HeaderScope.None));
    }

    /// <summary>The root element; the parent of the report's top-level parts.</summary>
    public StructId Document => new(0);

    internal int Count => _elements.Count;
    internal int NextKey() => _nextKey++;
    internal StructType TypeOf(StructId id) => _elements[id.Index].Type;

    /// <summary>Adds an element as the last child of <paramref name="parent"/>.</summary>
    /// <exception cref="InvalidOperationException">A table part outside its proper parent, or too many elements.</exception>
    public StructId Add(StructId parent, StructType type)
    {
        if (!_builder.Tagged) return default;
        var parentType = _elements[parent.Index].Type;
        var ok = type switch
        {
            StructType.Document => false,
            StructType.THead or StructType.TBody or StructType.TFoot => parentType == StructType.Table,
            StructType.TR => parentType is StructType.Table or StructType.THead or StructType.TBody or StructType.TFoot,
            StructType.TH or StructType.TD => parentType == StructType.TR,
            StructType.Caption => parentType is StructType.Table or StructType.Figure,
            _ => parentType is not (StructType.Table or StructType.THead or StructType.TBody or StructType.TFoot or StructType.TR),
        };
        if (!ok) throw new InvalidOperationException($"{type} cannot be placed in {parentType}.");
        if (type == StructType.TR && parentType == StructType.TBody) parent = new StructId(BodyForNextRow(parent.Index));
        if (_elements.Count >= MaxElements)
            throw new PdfFormException($"The report needs more than {MaxElements:N0} structure elements. Split it, or generate it without tagging.");
        var id = _elements.Count;
        _elements.Add(new Element(parent.Index, -1, -1, type, HeaderScope.None));
        AddItem(parent.Index, id, ChildElement);
        return new StructId(id);
    }

    private int BodyForNextRow(int body)
    {
        var (current, rows) = _bodies.TryGetValue(body, out var state) ? state : (body, 0);
        var table = _elements[body].Parent;
        // only while the body is the last part of its table; otherwise a new one would end up out of order
        if (rows >= RowsPerBody && _items[_elements[table].Last].A == current)
        {
            current = Add(new StructId(table), StructType.TBody).Index;
            rows = 0;
        }
        _bodies[body] = (current, rows + 1);
        return current;
    }

    /// <summary>Adds a table header cell.</summary>
    public StructId AddHeaderCell(StructId row, HeaderScope scope, int colSpan = 1, int rowSpan = 1)
    {
        var id = AddCell(row, StructType.TH, colSpan, rowSpan);
        if (_builder.Tagged) _elements[id.Index].Scope = scope;
        return id;
    }

    /// <summary>Adds a table data cell. A cell without content is fine: just never begin tagged content for it.</summary>
    public StructId AddCell(StructId row, int colSpan = 1, int rowSpan = 1) => AddCell(row, StructType.TD, colSpan, rowSpan);

    private StructId AddCell(StructId row, StructType type, int colSpan, int rowSpan)
    {
        if (colSpan < 1) throw new ArgumentOutOfRangeException(nameof(colSpan));
        if (rowSpan < 1) throw new ArgumentOutOfRangeException(nameof(rowSpan));
        var id = Add(row, type);
        if (_builder.Tagged && (colSpan > 1 || rowSpan > 1)) (ExtraOf(id).ColSpan, ExtraOf(id).RowSpan) = (colSpan, rowSpan);
        return id;
    }

    /// <summary>Alternative text, required for figures.</summary>
    public void SetAlt(StructId id, string text) { if (_builder.Tagged) ExtraOf(id).Alt = text; }

    /// <summary>Text that replaces the element's content for assistive technology.</summary>
    public void SetActualText(StructId id, string text) { if (_builder.Tagged) ExtraOf(id).ActualText = text; }

    /// <summary>Language of the element when it differs from the document's, e.g. "en".</summary>
    public void SetLanguage(StructId id, string language) { if (_builder.Tagged) ExtraOf(id).Language = language; }

    /// <summary>Title shown in the tag tree of PDF editors.</summary>
    public void SetTitle(StructId id, string title) { if (_builder.Tagged) ExtraOf(id).Title = title; }

    /// <summary>Where a figure is on its page (lower left corner and size in points).</summary>
    public void SetBox(StructId id, double x, double y, double width, double height) { if (_builder.Tagged) ExtraOf(id).Box = (x, y, width, height); }

    private Extra ExtraOf(StructId id)
    {
        if (!_extras.TryGetValue(id.Index, out var e)) _extras[id.Index] = e = new Extra();
        return e;
    }

    private void AddItem(int element, int a, int b)
    {
        var index = _items.Count;
        _items.Add(new Item(-1, a, b));
        ref var e = ref _elements[element];
        if (e.First < 0) e.First = index;
        else _items[e.Last].Next = index;
        e.Last = index;
    }

    /// <summary>Allocates the next marked-content id of the page for the element.</summary>
    internal int AddContent(StructId id, PdfPageBuilder page)
    {
        if (id.Index < 0 || id.Index >= _elements.Count) throw new ArgumentOutOfRangeException(nameof(id));
        if (_elements[id.Index].Type is StructType.Document or StructType.Table or StructType.THead or StructType.TBody or StructType.TFoot or StructType.TR)
            throw new InvalidOperationException($"{_elements[id.Index].Type} groups other elements and cannot have content of its own.");
        var mcid = page.ContentOwners.Count;
        page.ContentOwners.Add(id.Index);
        AddItem(id.Index, page.Index, mcid);
        return mcid;
    }

    internal void AddAnnotation(StructId id, PdfDictionary annotation, PdfPageBuilder page)
    {
        var key = NextKey();
        annotation["/StructParent"] = new PdfNumber(key);
        _annotationKeys[key] = id.Index;
        AddItem(id.Index, _annotations.Count, AnnotationItem);
        _annotations.Add((annotation, page));
    }

    // ------------------------------------------------------------------ output

    private const int KeysPerLeaf = 32;

    private List<(int Key, PdfPageBuilder? Page, int Element)> Keys()
        => _builder.Pages.Where(p => p.ContentOwners.Count > 0).Select(p => (p.StructKey, (PdfPageBuilder?)p, 0))
            .Concat(_annotationKeys.Select(k => (k.Key, (PdfPageBuilder?)null, k.Value))).OrderBy(k => k.Item1).ToList();

    /// <summary>Number of objects <see cref="Write"/> will produce.</summary>
    internal int ObjectCount() => 2 + (Keys().Count + KeysPerLeaf - 1) / KeysPerLeaf + _elements.Count;

    /// <summary>
    /// Produces the StructTreeRoot, the ParentTree and all elements as consecutive objects, the root being <paramref name="first"/>.
    /// <paramref name="emit"/> takes the finished bytes of each object from the buffer.
    /// </summary>
    internal void Write(int first, Func<PdfObject, int> numberOf, Action<int, ByteBuffer> emit)
    {
        var keys = Keys();
        var leaves = (keys.Count + KeysPerLeaf - 1) / KeysPerLeaf;
        var parentTree = first + 1;
        var elementBase = first + 2 + leaves;
        var pages = _builder.Pages.Select(p => numberOf(p.Dictionary)).ToArray();
        var b = new ByteBuffer(65536);
        void Done(int number) { emit(number, b); b.Clear(); }
        void Ref(int number) => b.Int(number).Ascii(" 0 R");
        void RefTo(int element) => Ref(elementBase + element);

        b.Ascii("<</Type/StructTreeRoot/K ");
        RefTo(0);
        b.Ascii("/ParentTree ");
        Ref(parentTree);
        b.Ascii("/ParentTreeNextKey ").Int(_nextKey).Ascii(">>");
        Done(first);

        b.Ascii(leaves == 0 ? "<</Nums[" : "<</Kids[");
        for (var i = 0; i < leaves; i++) { Ref(parentTree + 1 + i); b.Byte((byte)' '); }
        b.Ascii("]>>");
        Done(parentTree);

        for (var i = 0; i < leaves; i++)
        {
            var part = keys.GetRange(i * KeysPerLeaf, Math.Min(KeysPerLeaf, keys.Count - i * KeysPerLeaf));
            b.Ascii("<</Limits[").Int(part[0].Key).Byte((byte)' ').Int(part[^1].Key).Ascii("]/Nums[");
            foreach (var (key, page, element) in part)
            {
                b.Int(key).Byte((byte)' ');
                if (page is null) RefTo(element);
                else
                {
                    b.Byte((byte)'[');
                    foreach (var owner in page.ContentOwners) { RefTo(owner); b.Byte((byte)' '); }
                    b.Byte((byte)']');
                }
                b.Byte((byte)' ');
            }
            b.Ascii("]>>");
            Done(parentTree + 1 + i);
        }

        for (var i = 0; i < _elements.Count; i++)
        {
            ref var e = ref _elements[i];
            b.Ascii("<</S").Ascii(Names[(int)e.Type]).Ascii("/P ");
            if (e.Parent < 0) Ref(first); else RefTo(e.Parent);

            var page = -1; // the page whose content ids can be written as plain numbers
            for (var k = e.First; k >= 0 && page < 0; k = _items[k].Next) if (_items[k].B >= 0) page = _items[k].A;
            if (page >= 0) { b.Ascii("/Pg "); Ref(pages[page]); }
            if (e.First >= 0)
            {
                b.Ascii("/K");
                var several = e.First != e.Last;
                b.Byte(several ? (byte)'[' : (byte)' ');
                for (var k = e.First; k >= 0; k = _items[k].Next)
                {
                    var (_, a, kind) = _items[k];
                    if (kind == ChildElement) RefTo(a);
                    else if (kind == AnnotationItem)
                    {
                        b.Ascii("<</Type/OBJR/Obj ");
                        Ref(numberOf(_annotations[a].Annotation));
                        b.Ascii("/Pg ");
                        Ref(pages[_annotations[a].Page.Index]);
                        b.Ascii(">>");
                    }
                    else if (a == page) b.Int(kind);
                    else
                    {
                        b.Ascii("<</Type/MCR/Pg ");
                        Ref(pages[a]);
                        b.Ascii("/MCID ").Int(kind).Ascii(">>");
                    }
                    if (several) b.Byte((byte)' ');
                }
                if (several) b.Byte((byte)']');
            }
            _extras.TryGetValue(i, out var x);
            WriteAttributes(b, e.Scope, x);
            if (x?.Alt is not null) b.Ascii("/Alt ").TextString(x.Alt);
            if (x?.ActualText is not null) b.Ascii("/ActualText ").TextString(x.ActualText);
            if (x?.Language is not null) b.Ascii("/Lang ").TextString(x.Language);
            if (x?.Title is not null) b.Ascii("/T ").TextString(x.Title);
            b.Ascii(">>");
            Done(elementBase + i);
        }
    }

    private static void WriteAttributes(ByteBuffer b, HeaderScope scope, Extra? x)
    {
        var table = scope != HeaderScope.None || x is { ColSpan: > 1 } or { RowSpan: > 1 };
        if (!table && x?.Box is null) return;
        b.Ascii("/A");
        var both = table && x?.Box is not null;
        if (both) b.Byte((byte)'[');
        if (table)
        {
            b.Ascii("<</O/Table");
            if (scope != HeaderScope.None) b.Ascii("/Scope/").Ascii(scope.ToString());
            if (x is { ColSpan: > 1 }) b.Ascii("/ColSpan ").Int(x.ColSpan);
            if (x is { RowSpan: > 1 }) b.Ascii("/RowSpan ").Int(x.RowSpan);
            b.Ascii(">>");
        }
        if (x?.Box is { } r) b.Ascii("<</O/Layout/Placement/Block/BBox[").Num(r.X).Num(r.Y).Num(r.X + r.W).Num(r.Y + r.H).Ascii("]>>");
        if (both) b.Byte((byte)']');
    }
}

/// <summary>List that grows without copying what it holds, so millions of entries do not need twice their memory.</summary>
internal sealed class ChunkedList<T> where T : struct
{
    private const int Bits = 14;
    private readonly List<T[]> _chunks = new();

    public int Count { get; private set; }

    public ref T this[int index] => ref (uint)index < (uint)Count ? ref _chunks[index >> Bits][index & (1 << Bits) - 1] : ref Throw();

    private static ref T Throw() => throw new ArgumentOutOfRangeException("index");

    public void Add(T value)
    {
        if (Count >> Bits == _chunks.Count) _chunks.Add(new T[1 << Bits]);
        _chunks[Count >> Bits][Count & (1 << Bits) - 1] = value;
        Count++;
    }
}
