using PdfFormFiller;
using PdfFormFiller.Compat.Widget;

namespace PdfFormFiller.Compat;

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

/// <summary>PdfFormFiller.Compat.PdfDocument look-alike covering load, form access, pages, append/merge and save.</summary>
public class PdfDocument : PdfDocumentBase
{
    private PdfFormWidget? _form;

    public PdfDocument() { }
    public PdfDocument(string filename) => LoadFromFile(filename);
    public PdfDocument(byte[] bytes) => LoadFromBytes(bytes);
    public PdfDocument(Stream stream) => LoadFromStream(stream);

    internal PdfDocument(PdfFormDocument inner) => Inner = inner;

    public void LoadFromFile(string filename) => Attach(PdfFormDocument.Load(filename));
    public void LoadFromBytes(byte[] bytes) => Attach(PdfFormDocument.Load(bytes));
    public void LoadFromStream(Stream stream) => Attach(PdfFormDocument.Load(stream));

    private void Attach(PdfFormDocument doc)
    {
        Inner = doc;
        _form = null;
    }

    /// <summary>The interactive form. Callers written for the legacy API cast it to <see cref="PdfFormWidget"/>; both work here.</summary>
    public PdfFormWidget Form => _form ??= new PdfFormWidget(this);

    public PdfPageCollection Pages => new(this);

    /// <summary>Appends every page of <paramref name="source"/>.</summary>
    public void AppendPage(PdfDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Doc.Append(source.Doc);
    }

    /// <summary>Appends page <paramref name="pageIndex"/> (0-based) of <paramref name="source"/>.</summary>
    public void InsertPage(PdfDocument source, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        Doc.Append(source.Doc, new[] { pageIndex });
    }

    /// <summary>Inserts page <paramref name="pageIndex"/> of <paramref name="source"/> so that it gets index <paramref name="resultPageIndex"/> here.</summary>
    public void InsertPage(PdfDocument source, int pageIndex, int resultPageIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        Doc.Insert(source.Doc.Save(OutputMode.Editable), resultPageIndex, new[] { pageIndex });
    }

    /// <summary>Appends pages <paramref name="startIndex"/>..<paramref name="endIndex"/> (inclusive, 0-based) of <paramref name="source"/>.</summary>
    public void InsertPageRange(PdfDocument source, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (endIndex < startIndex) throw new ArgumentOutOfRangeException(nameof(endIndex));
        Doc.Append(source.Doc, Enumerable.Range(startIndex, endIndex - startIndex + 1).ToArray());
    }

    public void InsertPage(PdfDocument source, PdfPageBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        InsertPage(source, page.Index);
    }

    public static PdfDocumentBase MergeFiles(params string[] inputFiles)
    {
        ArgumentNullException.ThrowIfNull(inputFiles);
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
        if (_form is null) return OutputMode.Editable;
        if (_form.IsFlatten) return OutputMode.Flattened;
        _form.ApplyPendingFieldState();
        return _form.ReadOnly ? OutputMode.ReadOnly : OutputMode.Editable;
    }

    public override void Close()
    {
        Inner?.Dispose();
        Inner = null;
        _form = null;
    }
}

public class PdfPageBase
{
    public int Index { get; }
    internal PdfPageBase(int index) => Index = index;
}

public class PdfPageCollection : IEnumerable<PdfPageBase>
{
    private readonly PdfDocument _doc;
    internal PdfPageCollection(PdfDocument doc) => _doc = doc;

    public int Count => _doc.Doc.PageCount;
    public PdfPageBase this[int index] => index >= 0 && index < Count ? new PdfPageBase(index) : throw new ArgumentOutOfRangeException(nameof(index));
    public void RemoveAt(int index) => _doc.Doc.RemovePage(index);
    public void Remove(PdfPageBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _doc.Doc.RemovePage(page.Index);
    }

    public IEnumerator<PdfPageBase> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return new PdfPageBase(i);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
