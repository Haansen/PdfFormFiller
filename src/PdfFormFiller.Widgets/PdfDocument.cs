using PdfFormFiller;

namespace PdfFormFiller.Widgets;

public enum FileFormat { PDF }

/// <summary>Base type returned by <see cref="PdfDocument.MergeFiles(string[])"/>.</summary>
public abstract class PdfDocumentBase : IDisposable
{
    internal PdfFormDocument? Inner;

    internal PdfFormDocument Doc => Inner ?? throw new InvalidOperationException("No document loaded. Call LoadFromFile/LoadFromBytes first.");

    public void Save(string filename) => Doc.Save(filename, Mode());
    public void Save(Stream stream) => Doc.Save(stream, Mode());

    internal abstract OutputMode Mode();

    public virtual void Close() { }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing) Close();
    }
}

/// <summary>
/// The document behind a <see cref="PdfDocument"/> plus a cache of the field descriptions. Every change made through this
/// assembly drops the cache, and no one else can reach the document, so cached reads are never stale.
/// </summary>
internal sealed class FormState
{
    private PdfFormDocument? _doc;
    private IReadOnlyList<FormFieldInfo>? _infos;
    private Dictionary<string, FormFieldInfo>? _byName;

    /// <summary>Incremented whenever the document is replaced or closed; field objects remember the one they were made in.</summary>
    public int Generation { get; private set; }

    public PdfFormDocument Doc => _doc ?? throw new InvalidOperationException("No document loaded. Call LoadFromFile/LoadFromBytes first.");

    public void Attach(PdfFormDocument? doc)
    {
        _doc = doc;
        Generation++;
        Invalidate();
    }

    public void Invalidate()
    {
        _infos = null;
        _byName = null;
    }

    public IReadOnlyList<FormFieldInfo> Infos => _infos ??= Doc.GetFields();

    public FormFieldInfo? Find(string name)
    {
        if (_byName is null)
        {
            var map = new Dictionary<string, FormFieldInfo>();
            foreach (var i in Infos) map.TryAdd(i.Name, i);
            _byName = map;
        }
        return _byName.TryGetValue(name, out var info) ? info : null;
    }
}

/// <summary>PdfFormFiller.Widgets.PdfDocument look-alike covering load, form access, pages, append/merge and save.</summary>
public class PdfDocument : PdfDocumentBase
{
    private PdfFormWidget? _form;
    private readonly List<PdfPageBase> _pageHandles = new();

    internal FormState State { get; } = new();

    public PdfDocument() { }
    public PdfDocument(string filename) => LoadFromFile(filename);
    public PdfDocument(byte[] bytes) => LoadFromBytes(bytes);
    public PdfDocument(Stream stream) => LoadFromStream(stream);

    /// <summary>Opens a password-protected file. The user or the owner password works; a file without protection ignores it. The password is not kept, and saved output is always unencrypted.</summary>
    public PdfDocument(string filename, string? password) => LoadFromFile(filename, password);

    /// <summary>Opens password-protected bytes. The user or the owner password works; a file without protection ignores it. The password is not kept, and saved output is always unencrypted.</summary>
    public PdfDocument(byte[] bytes, string? password) => LoadFromBytes(bytes, password);

    /// <summary>Opens a password-protected stream. The user or the owner password works; a file without protection ignores it. The password is not kept, and saved output is always unencrypted.</summary>
    public PdfDocument(Stream stream, string? password) => LoadFromStream(stream, password);

    internal PdfDocument(PdfFormDocument inner) => Attach(inner);

    public void LoadFromFile(string filename) => Attach(PdfFormDocument.Load(filename));
    public void LoadFromBytes(byte[] bytes) => Attach(PdfFormDocument.Load(bytes));
    public void LoadFromStream(Stream stream) => Attach(PdfFormDocument.Load(stream));

    /// <summary>Loads a password-protected file. User or owner password; the password is not kept and saved output is unencrypted. A failed load (wrong or missing password, damaged file) leaves the current document untouched.</summary>
    public void LoadFromFile(string filename, string? password) => Attach(PdfFormDocument.Load(filename, password));

    /// <summary>Loads password-protected bytes. User or owner password; the password is not kept and saved output is unencrypted. A failed load leaves the current document untouched.</summary>
    public void LoadFromBytes(byte[] bytes, string? password) => Attach(PdfFormDocument.Load(bytes, password));

    /// <summary>Loads a password-protected stream. User or owner password; the password is not kept and saved output is unencrypted. A failed load leaves the current document untouched.</summary>
    public void LoadFromStream(Stream stream, string? password) => Attach(PdfFormDocument.Load(stream, password));

    /// <summary>Replaces the document. The Form object stays the same but starts over (switches off, new field list); field and page objects of the old document stop working.</summary>
    private void Attach(PdfFormDocument? doc)
    {
        var old = Inner;
        Inner = doc;
        State.Attach(doc);
        foreach (var h in _pageHandles) h.Kill();
        _pageHandles.Clear();
        _form?.Reset();
        old?.Dispose();
    }

    /// <summary>The interactive form. Callers written for the legacy API cast it to <see cref="PdfFormWidget"/>; both work here.</summary>
    public PdfFormWidget Form => _form ??= new PdfFormWidget(this);

    public PdfPageCollection Pages => new(this);

    // ---- page structure. Everything that adds or removes pages goes through Structural so page handles,
    // ---- the field cache and the field list stay in step.

    private void SyncHandles()
    {
        var n = Doc.PageCount;
        while (_pageHandles.Count < n) _pageHandles.Add(new PdfPageBase(this, _pageHandles.Count));
        while (_pageHandles.Count > n)
        {
            _pageHandles[^1].Kill();
            _pageHandles.RemoveAt(_pageHandles.Count - 1);
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < _pageHandles.Count; i++) _pageHandles[i].Position = i;
    }

    /// <summary>Runs a page-changing operation. New pages land at <paramref name="insertAt"/>, or at the end when it is -1.</summary>
    private void Structural(Action operation, int insertAt = -1)
    {
        var doc = Doc;
        var before = doc.PageCount;
        SyncHandles();
        State.Invalidate();
        try
        {
            operation();
        }
        finally
        {
            var added = doc.PageCount - before;
            var at = insertAt < 0 ? before : insertAt;
            for (var i = 0; i < added; i++) _pageHandles.Insert(at + i, new PdfPageBase(this, at + i));
            Renumber();
            State.Invalidate();
            _form?.Resync();
        }
    }

    internal PdfPageBase PageHandle(int index)
    {
        SyncHandles();
        return _pageHandles[index];
    }

    internal void RemovePageAt(int index)
    {
        var doc = Doc;
        SyncHandles();
        if (index < 0 || index >= _pageHandles.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var handle = _pageHandles[index];
        State.Invalidate();
        try
        {
            doc.RemovePage(index);
        }
        finally
        {
            if (doc.PageCount < _pageHandles.Count)
            {
                handle.Kill();
                _pageHandles.RemoveAt(index);
                Renumber();
            }
            State.Invalidate();
            _form?.Resync();
        }
    }

    /// <summary>
    /// The live document of <paramref name="source"/> as a page source. Per-field Flatten flags are a save-time setting and
    /// not part of what gets copied, so flags left behind by an earlier save are cleared first (they are re-applied on every save).
    /// </summary>
    private static PdfFormDocument AsSource(PdfDocument source)
    {
        var doc = source.Doc;
        doc.ClearFlattenFields();
        return doc;
    }

    /// <summary>Appends every page of <paramref name="source"/>.</summary>
    public void AppendPage(PdfDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var src = AsSource(source);
        try { Structural(() => Doc.Append(src)); }
        finally { source.State.Invalidate(); } // copying saves the source, which reloads it
    }

    /// <summary>Appends page <paramref name="pageIndex"/> (0-based) of <paramref name="source"/>.</summary>
    public void InsertPage(PdfDocument source, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        var src = AsSource(source);
        try { Structural(() => Doc.Append(src, new[] { pageIndex })); }
        finally { source.State.Invalidate(); }
    }

    /// <summary>Inserts page <paramref name="pageIndex"/> of <paramref name="source"/> so that it gets index <paramref name="resultPageIndex"/> here.</summary>
    public void InsertPage(PdfDocument source, int pageIndex, int resultPageIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (resultPageIndex < 0 || resultPageIndex > Doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(resultPageIndex), $"Insert position {resultPageIndex} is out of range (0..{Doc.PageCount}).");
        var src = AsSource(source);
        try { Structural(() => Doc.Insert(src.Save(OutputMode.Editable), resultPageIndex, new[] { pageIndex }), resultPageIndex); }
        finally { source.State.Invalidate(); }
    }

    /// <summary>Appends pages <paramref name="startIndex"/>..<paramref name="endIndex"/> (inclusive, 0-based) of <paramref name="source"/>.</summary>
    public void InsertPageRange(PdfDocument source, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (endIndex < startIndex) throw new ArgumentOutOfRangeException(nameof(endIndex));
        var src = AsSource(source);
        try { Structural(() => Doc.Append(src, Enumerable.Range(startIndex, endIndex - startIndex + 1).ToArray())); }
        finally { source.State.Invalidate(); }
    }

    /// <summary>Appends the page <paramref name="page"/> of <paramref name="source"/>.</summary>
    public void InsertPage(PdfDocument source, PdfPageBase page)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(page);
        InsertPage(source, page.CheckOwner(source));
    }

    public static PdfDocumentBase MergeFiles(params string[] inputFiles)
    {
        ArgumentNullException.ThrowIfNull(inputFiles);
        if (inputFiles.Length == 0) throw new ArgumentException("At least one input file is required.", nameof(inputFiles));
        if (inputFiles.Any(f => f is null)) throw new ArgumentException("Input files must not contain null.", nameof(inputFiles));
        return new PdfDocument(PdfFormDocument.Merge(inputFiles.Select(File.ReadAllBytes)));
    }

    public static PdfDocumentBase MergeFiles(string firstInputFile, string secondInputFile)
        => MergeFiles(new[] { firstInputFile, secondInputFile });

    public void SaveToFile(string filename) => Doc.Save(filename, Mode());
    public void SaveToFile(string filename, FileFormat format) => SaveToFile(filename);
    public void SaveToStream(Stream stream) => Doc.Save(stream, Mode());
    public void SaveToStream(Stream stream, FileFormat format) => SaveToStream(stream);

    /// <summary>Convenience: the finished bytes directly.</summary>
    public byte[] SaveToBytes() => Doc.Save(Mode());

    internal override OutputMode Mode()
    {
        _ = Doc;
        if (_form is null) return OutputMode.Editable;
        if (_form.IsFlatten)
        {
            Doc.ClearFlattenFields(); // not needed for a full flatten; do not leave flags from an earlier save behind
            return OutputMode.Flattened;
        }
        _form.ApplyPendingFieldState();
        return _form.ReadOnly ? OutputMode.ReadOnly : OutputMode.Editable;
    }

    public override void Close()
    {
        Attach(null);
        _form = null;
    }
}

/// <summary>
/// A page of a document. The handle follows its page when other pages are removed or inserted; after the page itself is
/// removed (or the document closed or replaced) <see cref="Index"/> is -1 and the handle cannot be used any more.
/// </summary>
public class PdfPageBase
{
    private readonly PdfDocument _owner;
    private int _index;

    internal PdfPageBase(PdfDocument owner, int index)
    {
        _owner = owner;
        _index = index;
    }

    /// <summary>Current 0-based position of the page; -1 once the page is gone.</summary>
    public int Index => _index;

    internal int Position { set { if (_index >= 0) _index = value; } }

    internal void Kill() => _index = -1;

    /// <summary>Position of this page in <paramref name="doc"/>; throws for a removed page or a page of another document.</summary>
    internal int CheckOwner(PdfDocument doc)
    {
        if (!ReferenceEquals(_owner, doc)) throw new ArgumentException("The page belongs to another document.", "page");
        if (_index < 0) throw new InvalidOperationException("The page has been removed from its document.");
        return _index;
    }
}

public class PdfPageCollection : IEnumerable<PdfPageBase>
{
    private readonly PdfDocument _doc;
    internal PdfPageCollection(PdfDocument doc) => _doc = doc;

    public int Count => _doc.Doc.PageCount;
    public PdfPageBase this[int index] => index >= 0 && index < Count ? _doc.PageHandle(index) : throw new ArgumentOutOfRangeException(nameof(index));
    public void RemoveAt(int index) => _doc.RemovePageAt(index);
    public void Remove(PdfPageBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _doc.RemovePageAt(page.CheckOwner(_doc));
    }

    public IEnumerator<PdfPageBase> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return _doc.PageHandle(i);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
