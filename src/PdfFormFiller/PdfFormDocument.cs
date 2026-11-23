using PdfFormFiller.Core;
using PdfFormFiller.Internal;
using static PdfFormFiller.Internal.PdfExt;

namespace PdfFormFiller;

/// <summary>
/// Fills, merges, trims and flattens AcroForm PDFs. Load a document, mutate it with the fluent setters,
/// then call <see cref="Save(OutputMode)"/> to get the finished PDF bytes. The instance stays usable after saving.
/// Instances are not thread-safe; use one per thread or request.
/// </summary>
public sealed class PdfFormDocument : IDisposable
{
    private PdfDocument _doc;
    private List<FieldNode>? _fields;
    private AppearanceBuilder? _appearances;
    private readonly HashSet<string> _flattenOnly = new();
    private bool _rebuildAllAppearances;
    private bool _throwOnUnsupported;
    private bool _disposed;

    private PdfFormDocument(PdfDocument doc) => _doc = doc;

    /// <summary>
    /// Upper bound, in bytes, for any single stream after decompression (default 256 MB). Protects against
    /// decompression bombs in untrusted files. Applies process-wide.
    /// </summary>
    public static long MaxDecodedStreamBytes
    {
        get => Filters.MaxDecodedBytes;
        set => Filters.MaxDecodedBytes = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// When true, setting or saving a value with characters that neither Helvetica nor the embedded Unicode font can draw
    /// throws <see cref="PdfFormException"/>. When false (default) such characters are drawn as '?'; the stored field value is always kept intact.
    /// </summary>
    public bool ThrowOnUnsupportedCharacters
    {
        get => _throwOnUnsupported;
        set
        {
            _throwOnUnsupported = value;
            if (_appearances is not null) _appearances.Strict = value;
        }
    }

    // ------------------------------------------------------------------ loading

    public static PdfFormDocument Load(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return new PdfFormDocument(Open(pdf));
    }

    public static PdfFormDocument Load(Stream pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var ms = new MemoryStream();
        pdf.CopyTo(ms);
        return Load(ms.ToArray());
    }

    public static PdfFormDocument Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>Opens a PDF and guarantees that every failure surfaces as <see cref="PdfFormException"/>.</summary>
    private static PdfDocument Open(byte[] pdf)
    {
        try
        {
            var doc = PdfDocument.Load(pdf);
            _ = doc.Pages.Count; // walk the page tree now so structural damage is reported at load time
            return doc;
        }
        catch (Exception ex) when (ex is not PdfFormException and not OutOfMemoryException)
        {
            throw new PdfFormException("Could not open PDF: " + ex.Message, ex);
        }
    }

    // ------------------------------------------------------------------ inspection

    public int PageCount
    {
        get { ThrowIfDisposed(); return _doc.Pages.Count; }
    }

    /// <summary>All fillable fields with their current values, options and page indices (0-based).</summary>
    public IReadOnlyList<FormFieldInfo> GetFields()
    {
        ThrowIfDisposed();
        var pages = FieldTree.AnnotationPages(_doc);
        return Fields.Select(f => new FormFieldInfo(
            f.FullName,
            f.Type,
            f.ValueString(),
            f.Type is FieldType.CheckBox or FieldType.RadioButton
                ? f.Widgets.Select(f.ExportValue).Distinct().ToList()
                : f.Options().Select(o => o.Export).ToList(),
            f.Widgets.Select(w => pages.TryGetValue(w, out var p) ? p : -1).Where(p => p >= 0).Distinct().OrderBy(p => p).ToList(),
            f.Has(FieldFlag.ReadOnly),
            f.Has(FieldFlag.Required),
            f.Has(FieldFlag.Multiline))).ToList();
    }

    public string? GetValue(string fieldName) => Find(fieldName).ValueString();

    public bool HasField(string fieldName)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fieldName);
        return Fields.Any(f => f.FullName == fieldName);
    }

    // ------------------------------------------------------------------ setting values

    /// <summary>Sets a value on any field type. Check boxes accept true/false/yes/no/ja/nej/1/0/on/off or an export value;
    /// radio buttons and choices accept export value or display text.</summary>
    public PdfFormDocument SetValue(string fieldName, string? value)
    {
        var f = Find(fieldName);
        switch (f.Type)
        {
            case FieldType.Text: return SetText(fieldName, value);
            case FieldType.CheckBox:
                if (value is not null && f.Widgets.Any(w => string.Equals(f.StateName(w), value.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return SetCheckBox(fieldName, value.Trim());
                return SetCheckBox(fieldName, ParseBool(value, fieldName));
            case FieldType.RadioButton: return SetRadio(fieldName, value);
            case FieldType.ComboBox:
            case FieldType.ListBox: return SetChoice(fieldName, value);
            default: throw new PdfFormException($"Field '{fieldName}' is of type {f.Type} and cannot take a value.");
        }
    }

    public PdfFormDocument SetValues(IEnumerable<KeyValuePair<string, string?>> values)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(values);
        foreach (var kv in values) SetValue(kv.Key, kv.Value);
        return this;
    }

    public PdfFormDocument SetText(string fieldName, string? value)
    {
        var f = Find(fieldName, FieldType.Text);
        var previous = f.Dict["/V"];
        if (string.IsNullOrEmpty(value)) f.Dict.Remove("/V");
        else f.Dict["/V"] = PdfString.FromText(value);
        try
        {
            Appearances.Rebuild(f);
        }
        catch (PdfFormException)
        {
            f.Dict["/V"] = previous; // strict character check failed: leave the field as it was
            throw;
        }
        return this;
    }

    /// <summary>
    /// Checks or clears a check box. For a group whose boxes have different export values, <c>true</c> checks the first
    /// one; use <see cref="SetCheckBox(string, string)"/> to pick a specific box.
    /// </summary>
    public PdfFormDocument SetCheckBox(string fieldName, bool isChecked)
    {
        var f = Find(fieldName, FieldType.CheckBox);
        var on = f.Widgets.Count > 0 ? f.StateName(f.Widgets[0]) : "Yes";
        ApplyButtonState(f, isChecked ? on : null);
        return this;
    }

    /// <summary>Checks the box (or boxes) in a check box group that carry the given export value and clears the others.</summary>
    public PdfFormDocument SetCheckBox(string fieldName, string exportValue)
    {
        ArgumentNullException.ThrowIfNull(exportValue);
        var f = Find(fieldName, FieldType.CheckBox);
        var states = f.Widgets.Select(f.StateName).Distinct().ToList();
        var state = states.FirstOrDefault(s => s == exportValue)
                    ?? states.FirstOrDefault(s => string.Equals(s, exportValue, StringComparison.OrdinalIgnoreCase))
                    ?? throw new PdfFormException($"Check box '{fieldName}' has no export value '{exportValue}'. Available: {string.Join(", ", states)}.");
        ApplyButtonState(f, state);
        return this;
    }

    private void ApplyButtonState(FieldNode f, string? state)
    {
        f.Dict["/V"] = new PdfName(state ?? "Off");
        foreach (var w in f.Widgets) w["/AS"] = new PdfName(state is not null && f.StateName(w) == state ? state : "Off");
        Appearances.Rebuild(f);
    }

    /// <summary>Selects a radio option by export value, display text from /Opt, or 0-based option index. Null clears the selection.</summary>
    public PdfFormDocument SetRadio(string fieldName, string? exportValue)
    {
        var f = Find(fieldName, FieldType.RadioButton);
        if (exportValue is null)
        {
            ApplyButtonState(f, null);
            return this;
        }
        var opts = f.Options();
        var match = f.Widgets.FirstOrDefault(w => f.ExportValue(w) == exportValue)
                    ?? f.Widgets.FirstOrDefault(w => string.Equals(f.ExportValue(w), exportValue, StringComparison.OrdinalIgnoreCase))
                    ?? (opts.FindIndex(o => string.Equals(o.Display, exportValue, StringComparison.OrdinalIgnoreCase)) is var di && di >= 0 && di < f.Widgets.Count ? f.Widgets[di] : null)
                    ?? (int.TryParse(exportValue, out var idx) && idx >= 0 && idx < f.Widgets.Count ? f.Widgets[idx] : null)
                    ?? throw new PdfFormException(
                        $"Radio group '{fieldName}' has no option '{exportValue}'. Available: {string.Join(", ", f.Widgets.Select(f.ExportValue).Distinct())}.");
        ApplyButtonState(f, f.StateName(match));
        return this;
    }

    /// <summary>Selects a combo box / list box value by export value or display text. Null clears it.</summary>
    public PdfFormDocument SetChoice(string fieldName, string? value)
    {
        var f = Find(fieldName);
        if (f.Type is not (FieldType.ComboBox or FieldType.ListBox))
            throw new PdfFormException($"Field '{fieldName}' is {f.Type}, not a choice field.");
        var opts = f.Options();
        if (string.IsNullOrEmpty(value))
        {
            f.Dict.Remove("/V");
            f.Dict.Remove("/I");
        }
        else
        {
            var idx = opts.FindIndex(o => o.Export == value);
            if (idx < 0) idx = opts.FindIndex(o => o.Display == value);
            if (idx < 0) idx = opts.FindIndex(o => string.Equals(o.Display, value, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && !f.Has(FieldFlag.Edit) && opts.Count > 0)
                throw new PdfFormException(
                    $"Choice '{fieldName}' has no option '{value}'. Available: {string.Join(", ", opts.Select(o => o.Export))}.");
            var export = idx >= 0 ? opts[idx].Export : value;
            f.Dict["/V"] = PdfString.FromText(export);
            if (idx >= 0) f.Dict["/I"] = new PdfArray(new PdfNumber(idx));
            else f.Dict.Remove("/I");
        }
        Appearances.Rebuild(f);
        return this;
    }

    /// <summary>Sets the horizontal alignment of a text, combo box or list box field and redraws it.</summary>
    public PdfFormDocument SetTextAlignment(string fieldName, TextAlignment alignment)
    {
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        var f = Find(fieldName);
        if (f.Type is not (FieldType.Text or FieldType.ComboBox or FieldType.ListBox))
            throw new PdfFormException($"Field '{fieldName}' is {f.Type}; alignment applies to text and choice fields.");
        f.Dict["/Q"] = new PdfNumber((int)alignment);
        Appearances.Rebuild(f);
        return this;
    }

    /// <summary>Current horizontal alignment of a field.</summary>
    public TextAlignment GetTextAlignment(string fieldName)
    {
        var q = Find(fieldName).Quadding;
        return q is 1 or 2 ? (TextAlignment)q : TextAlignment.Left;
    }

    /// <summary>Flags a single field read-only (or editable again). Persists in Editable and ReadOnly output.</summary>
    public PdfFormDocument SetReadOnly(string fieldName, bool readOnly = true)
    {
        var f = Find(fieldName);
        var flags = readOnly ? f.Flags | (int)FieldFlag.ReadOnly : f.Flags & ~(int)FieldFlag.ReadOnly;
        f.Dict["/Ff"] = new PdfNumber(flags);
        Invalidate();
        return this;
    }

    /// <summary>
    /// Marks fields to be flattened individually when saving as Editable or ReadOnly, leaving the other fields live.
    /// Has no extra effect with <see cref="OutputMode.Flattened"/>, which flattens everything.
    /// </summary>
    public PdfFormDocument FlattenFields(params string[] fieldNames)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fieldNames);
        foreach (var n in fieldNames) Find(n);           // validate all first so the call is all-or-nothing
        foreach (var n in fieldNames) _flattenOnly.Add(n);
        return this;
    }

    /// <summary>Undoes every earlier <see cref="FlattenFields"/> call.</summary>
    public PdfFormDocument ClearFlattenFields()
    {
        ThrowIfDisposed();
        _flattenOnly.Clear();
        return this;
    }

    // ------------------------------------------------------------------ renaming

    /// <summary>Renames a field. <paramref name="newName"/> replaces the last dotted component of the name.</summary>
    public PdfFormDocument RenameField(string fieldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('.'))
            throw new PdfFormException("New field name must be non-empty and must not contain '.'.");
        var f = Find(fieldName);
        var carrier = f.Dict.ContainsKey("/T") ? f.Dict : f.Dict.Parent()
                      ?? throw new PdfFormException($"Field '{fieldName}' has no name to change.");
        var prefix = fieldName.Contains('.') ? fieldName[..fieldName.LastIndexOf('.')] + "." : string.Empty;
        var target = prefix + newName;
        if (Fields.Any(x => !ReferenceEquals(x.Dict, f.Dict) && (x.FullName == target || x.FullName.StartsWith(target + ".", StringComparison.Ordinal))))
            throw new PdfFormException($"A field named '{target}' already exists.");
        carrier["/T"] = PdfString.FromText(newName);
        if (_flattenOnly.Remove(fieldName)) _flattenOnly.Add(target);
        Invalidate();
        return this;
    }

    /// <summary>
    /// Nests every field under <paramref name="prefix"/>, so "namn" becomes "prefix.namn". Useful before appending
    /// another copy of the same template.
    /// </summary>
    public PdfFormDocument PrefixFields(string prefix)
    {
        ThrowIfDisposed();
        ValidatePrefix(prefix, Fields.Select(f => f.FullName));
        var fields = FieldTree.AcroForm(_doc)?.Arr("/Fields");
        if (fields is null || fields.Count == 0) return this;
        var group = new PdfDictionary();
        group["/T"] = PdfString.FromText(prefix);
        var kids = new PdfArray();
        group["/Kids"] = kids;
        var groupRef = _doc.RefFor(group);
        foreach (var r in fields.Dicts().ToList())
        {
            kids.Add(_doc.RefFor(r));
            r["/Parent"] = groupRef;
        }
        fields.Clear();
        fields.Add(groupRef);
        var renamed = _flattenOnly.Select(n => prefix + "." + n).ToList();
        _flattenOnly.Clear();
        foreach (var n in renamed) _flattenOnly.Add(n);
        Invalidate();
        return this;
    }

    // ------------------------------------------------------------------ pages

    /// <summary>Removes a page (0-based). Fields that only lived on that page are removed too.</summary>
    public PdfFormDocument RemovePage(int pageIndex) => RemovePages(pageIndex);

    /// <summary>Removes several pages. All indices are validated first; either every page is removed or none.</summary>
    public PdfFormDocument RemovePages(params int[] pageIndices)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pageIndices);
        var distinct = pageIndices.Distinct().OrderByDescending(i => i).ToList();
        foreach (var i in distinct)
            if (i < 0 || i >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndices), $"Page index {i} is out of range (0..{PageCount - 1}).");
        if (distinct.Count == 0) return this;
        if (distinct.Count >= PageCount) throw new PdfFormException("Cannot remove every page of a document.");
        foreach (var i in distinct) _doc.RemovePage(i);
        FieldTree.PruneOrphans(_doc);
        Invalidate();
        return this;
    }

    /// <summary>Keeps only the given pages (0-based), in document order. At least one page must be kept.</summary>
    public PdfFormDocument KeepPages(params int[] pageIndices)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pageIndices);
        foreach (var i in pageIndices)
            if (i < 0 || i >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndices), $"Page index {i} is out of range (0..{PageCount - 1}).");
        if (pageIndices.Length == 0) throw new PdfFormException("KeepPages needs at least one page index.");
        var keep = pageIndices.ToHashSet();
        return RemovePages(Enumerable.Range(0, PageCount).Where(i => !keep.Contains(i)).ToArray());
    }

    /// <summary>
    /// Appends pages (and their form fields) from another PDF. Pass <paramref name="pageIndices"/> to pick pages.
    /// Fields from the appended document are nested under <paramref name="fieldPrefix"/> ("prefix.name") when given;
    /// otherwise colliding names get a numeric suffix. A prefix can be used once per document.
    /// </summary>
    public PdfFormDocument Append(byte[] otherPdf, int[]? pageIndices = null, string? fieldPrefix = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(otherPdf);
        var existingNames = Fields.Select(f => f.FullName).ToHashSet();
        if (fieldPrefix is not null) ValidatePrefix(fieldPrefix, existingNames);

        var other = Open(otherPdf);
        var indices = pageIndices ?? Enumerable.Range(0, other.Pages.Count).ToArray();
        foreach (var i in indices)
            if (i < 0 || i >= other.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndices), $"Page index {i} is out of range for the appended document (0..{other.Pages.Count - 1}).");

        var before = CurrentRootFields();
        var importer = new Importer(_doc);
        foreach (var i in indices) importer.ImportPage(other.Pages[i]);

        MergeDefaultResources(other, importer);
        RebuildFieldsArray(before, fieldPrefix, existingNames);
        FieldTree.PruneOrphans(_doc);
        Invalidate();
        return this;
    }

    public PdfFormDocument Append(PdfFormDocument other, int[]? pageIndices = null, string? fieldPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Append(other.Save(OutputMode.Editable), pageIndices, fieldPrefix);
    }

    /// <summary>
    /// Like <see cref="Append(byte[], int[], string)"/>, but places the pages so that the first of them gets index
    /// <paramref name="atPageIndex"/> (0 = before the first page, <see cref="PageCount"/> = at the end).
    /// </summary>
    public PdfFormDocument Insert(byte[] otherPdf, int atPageIndex, int[]? pageIndices = null, string? fieldPrefix = null)
    {
        ThrowIfDisposed();
        var before = PageCount;
        if (atPageIndex < 0 || atPageIndex > before)
            throw new ArgumentOutOfRangeException(nameof(atPageIndex), $"Insert position {atPageIndex} is out of range (0..{before}).");
        Append(otherPdf, pageIndices, fieldPrefix);
        if (atPageIndex == before) return this;
        var pages = _doc.Pages.ToList();
        var added = pages.Skip(before).ToList();
        var order = pages.Take(atPageIndex).Concat(added).Concat(pages.Skip(atPageIndex).Take(before - atPageIndex)).ToList();
        _doc.SetPageOrder(order);
        Invalidate();
        return this;
    }

    /// <summary>Moves a page to a new position (both 0-based).</summary>
    public PdfFormDocument MovePage(int fromIndex, int toIndex)
    {
        ThrowIfDisposed();
        var n = PageCount;
        if (fromIndex < 0 || fromIndex >= n) throw new ArgumentOutOfRangeException(nameof(fromIndex), $"Page index {fromIndex} is out of range (0..{n - 1}).");
        if (toIndex < 0 || toIndex >= n) throw new ArgumentOutOfRangeException(nameof(toIndex), $"Page index {toIndex} is out of range (0..{n - 1}).");
        if (fromIndex == toIndex) return this;
        var pages = _doc.Pages.ToList();
        var page = pages[fromIndex];
        pages.RemoveAt(fromIndex);
        pages.Insert(toIndex, page);
        _doc.SetPageOrder(pages);
        Invalidate();
        return this;
    }

    /// <summary>Concatenates several PDFs. With <paramref name="prefixFieldsWithIndex"/> fields are named "0.namn", "1.namn", …</summary>
    public static PdfFormDocument Merge(IEnumerable<byte[]> documents, bool prefixFieldsWithIndex = false)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var list = documents.ToList();
        if (list.Count == 0) throw new ArgumentException("At least one document is required.", nameof(documents));
        if (list.Any(d => d is null)) throw new ArgumentException("Documents must not contain null.", nameof(documents));
        var result = Load(list[0]);
        if (prefixFieldsWithIndex) result.PrefixFields("0");
        for (var i = 1; i < list.Count; i++)
            result.Append(list[i], fieldPrefix: prefixFieldsWithIndex ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        return result;
    }

    // ------------------------------------------------------------------ output

    /// <summary>Produces the finished PDF. The document instance can still be used afterwards.</summary>
    public byte[] Save(OutputMode mode = OutputMode.Flattened)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        try
        {
            return SaveCore(mode);
        }
        catch (Exception ex) when (ex is not PdfFormException and not OutOfMemoryException and not ArgumentException)
        {
            // the file is parsed lazily, so damage deep inside it can surface here rather than in Load
            throw new PdfFormException("Could not write PDF: " + ex.Message, ex);
        }
    }

    private byte[] SaveCore(OutputMode mode)
    {
        var acro = FieldTree.AcroForm(_doc);
        if (acro is not null)
        {
            Appearances.EnsureDefaultResources();
            // Fields changed through the setters already have fresh appearances. Untouched fields keep the template's own
            // appearance (special fonts, formatted values); they are only generated when the template has none, or when the
            // template asked viewers to regenerate everything.
            var all = _rebuildAllAppearances || acro.Bool("/NeedAppearances") == true;
            foreach (var f in Fields)
            {
                switch (f.Type)
                {
                    case FieldType.CheckBox or FieldType.RadioButton:
                        Appearances.Rebuild(f); // only creates state streams that are missing
                        break;
                    case FieldType.Text or FieldType.ComboBox or FieldType.ListBox:
                        if (all || AppearanceBuilder.LacksAppearance(f)) Appearances.Rebuild(f);
                        break;
                }
            }
            acro["/NeedAppearances"] = PdfBool.False;
            _rebuildAllAppearances = false;
        }

        var bytes = _doc.Save();
        // Reload so later edits start from a clean, compact object graph.
        _doc = PdfDocument.Load(bytes);
        Invalidate();

        switch (mode)
        {
            case OutputMode.Editable:
            {
                if (_flattenOnly.Count == 0) return bytes;
                var copy = PdfDocument.Load(bytes);
                FlattenSelected(copy);
                return copy.Save();
            }
            case OutputMode.ReadOnly:
            {
                var copy = PdfDocument.Load(bytes);
                FlattenSelected(copy);
                foreach (var f in FieldTree.Build(copy))
                    f.Dict["/Ff"] = new PdfNumber(f.Flags | (int)FieldFlag.ReadOnly);
                return copy.Save();
            }
            default:
            {
                var copy = PdfDocument.Load(bytes);
                Flattener.Flatten(copy);
                return copy.Save();
            }
        }
    }

    public void Save(string path, OutputMode mode = OutputMode.Flattened)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, Save(mode));
    }

    public void Save(Stream destination, OutputMode mode = OutputMode.Flattened)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var bytes = Save(mode);
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Releases the document. Every member throws <see cref="ObjectDisposedException"/> afterwards.</summary>
    public void Dispose()
    {
        _disposed = true;
        _fields = null;
        _appearances = null;
    }

    // ------------------------------------------------------------------ internals

    private List<FieldNode> Fields => _fields ??= FieldTree.Build(_doc);

    private AppearanceBuilder Appearances => _appearances ??= new AppearanceBuilder(_doc) { Strict = _throwOnUnsupported };

    private void Invalidate()
    {
        _fields = null;
        _appearances = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private FieldNode Find(string name, FieldType? expected = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);
        var f = Fields.FirstOrDefault(x => x.FullName == name) ?? throw new FieldNotFoundException(name);
        if (expected is not null && f.Type != expected)
            throw new PdfFormException($"Field '{name}' is {f.Type}, expected {expected}.");
        return f;
    }

    private static bool ParseBool(string? value, string fieldName)
    {
        if (value is null) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "ja" or "1" or "on" or "x" or "checked": return true;
            case "false" or "no" or "nej" or "0" or "off" or "" or "unchecked": return false;
        }
        throw new PdfFormException($"'{value}' is not a valid check box value for '{fieldName}'. Use true/false, ja/nej, 1/0 or one of the box's export values.");
    }

    private static void ValidatePrefix(string prefix, IEnumerable<string> existingNames)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Contains('.'))
            throw new PdfFormException("A field prefix must be non-empty and must not contain '.'.");
        if (existingNames.Any(n => n == prefix || n.StartsWith(prefix + ".", StringComparison.Ordinal)))
            throw new PdfFormException($"The field prefix '{prefix}' is already in use in this document. Use a different prefix for each appended document.");
    }

    private void FlattenSelected(PdfDocument copy)
    {
        if (_flattenOnly.Count == 0) return;
        var widgets = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var f in FieldTree.Build(copy))
            if (_flattenOnly.Contains(f.FullName))
                foreach (var w in f.Widgets) widgets.Add(w);
        Flattener.Flatten(copy, widgets);
    }

    private HashSet<PdfDictionary> CurrentRootFields()
    {
        var set = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var fields = FieldTree.AcroForm(_doc)?.Arr("/Fields");
        if (fields is not null) foreach (var d in fields.Dicts()) set.Add(d);
        return set;
    }

    /// <summary>Carries over the appended form's default fonts so its fields stay editable with their own font in viewers.</summary>
    private void MergeDefaultResources(PdfDocument other, Importer importer)
    {
        var otherAcro = other.Catalog.Dict("/AcroForm");
        if (otherAcro is null) return;
        var acro = _doc.Catalog.GetOrCreateDict("/AcroForm");
        var otherFonts = otherAcro.Dict("/DR")?.Dict("/Font");
        if (otherFonts is not null)
        {
            var fonts = acro.GetOrCreateDict("/DR").GetOrCreateDict("/Font");
            foreach (var kv in otherFonts.Entries.ToList())
                if (!fonts.ContainsKey(kv.Key)) fonts[kv.Key] = importer.CopyObject(kv.Value);
        }
        if (!acro.ContainsKey("/DA") && Resolve(otherAcro["/DA"]) is PdfString da)
            acro["/DA"] = new PdfString((byte[])da.Bytes.Clone());
        if (otherAcro.Bool("/NeedAppearances") == true) _rebuildAllAppearances = true;
    }

    /// <summary>After import, new widgets exist on pages but not in /AcroForm /Fields. Rebuild the root list.</summary>
    private void RebuildFieldsArray(HashSet<PdfDictionary> before, string? fieldPrefix, HashSet<string> existingNames)
    {
        var roots = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var kv in FieldTree.AnnotationPages(_doc))
        {
            var annot = kv.Key;
            if (!annot.IsWidget()) continue;
            var root = annot;
            var guard = 0;
            while (root.Parent() is { } p && guard++ < 256) root = p;
            if (before.Contains(root) || !seen.Add(root)) continue;
            roots.Add(root);
        }
        if (roots.Count == 0) return;

        var acro = _doc.Catalog.GetOrCreateDict("/AcroForm");
        var fields = acro.GetOrCreateArray("/Fields");

        if (!string.IsNullOrEmpty(fieldPrefix))
        {
            var group = new PdfDictionary();
            group["/T"] = PdfString.FromText(fieldPrefix);
            var kids = new PdfArray();
            group["/Kids"] = kids;
            var groupRef = _doc.RefFor(group);
            foreach (var r in roots)
            {
                kids.Add(_doc.RefFor(r));
                r["/Parent"] = groupRef;
            }
            fields.Add(groupRef);
            return;
        }

        foreach (var r in roots)
        {
            var name = r.Str("/T");
            if (name is not null && existingNames.Any(n => n == name || n.StartsWith(name + ".", StringComparison.Ordinal)))
            {
                var n = 2;
                while (existingNames.Any(e => e == $"{name}_{n}" || e.StartsWith($"{name}_{n}.", StringComparison.Ordinal))) n++;
                name = $"{name}_{n}";
                r["/T"] = PdfString.FromText(name);
            }
            if (name is not null) existingNames.Add(name);
            fields.Add(_doc.RefFor(r));
        }
    }
}
