using PdfFormFiller;

namespace PdfFormFiller.Widgets;

/// <summary>
/// Base of all form fields, as in PdfFormFiller.Widgets.PdfField. A field object is a live handle: it follows renames and
/// always reads current state from the document.
/// </summary>
public abstract class PdfField
{
    internal readonly FormState State;
    internal readonly FieldType Kind;
    private readonly int _generation;
    private string _fullName;

    internal PdfField(FormState state, FormFieldInfo info)
    {
        State = state;
        _generation = state.Generation;
        Kind = info.Type;
        _fullName = info.Name;
    }

    /// <summary>The document, for reading. Throws when the document was closed or another one was loaded since this field was created.</summary>
    internal PdfFormDocument Doc
    {
        get
        {
            if (State.Generation != _generation)
                throw new InvalidOperationException($"The field '{_fullName}' belongs to a document that was closed or replaced by LoadFrom*.");
            return State.Doc;
        }
    }

    /// <summary>The document, for changing it. Drops the cached field descriptions so the next read sees the change.</summary>
    internal PdfFormDocument Write()
    {
        var doc = Doc;
        State.Invalidate();
        return doc;
    }

    /// <summary>Current description of the field. Descriptions are cached until the next change made through this API, so reads stay live without rebuilding every field on every property access.</summary>
    internal FormFieldInfo Info
    {
        get
        {
            _ = Doc; // generation check
            return State.Find(_fullName)
                   ?? throw new InvalidOperationException($"The field '{_fullName}' no longer exists in the document (its page was removed or it was flattened).");
        }
    }

    /// <summary>Short name (last dotted component). Setting it renames the field in the document.</summary>
    public string Name
    {
        get => _fullName.Contains('.') ? _fullName[(_fullName.LastIndexOf('.') + 1)..] : _fullName;
        set
        {
            Write().RenameField(_fullName, value);
            _fullName = _fullName.Contains('.') ? _fullName[..(_fullName.LastIndexOf('.') + 1)] + value : value;
        }
    }

    public string FullName => _fullName;
    public bool Required => Info.Required;

    public bool ReadOnly
    {
        get => Info.ReadOnly;
        set => Write().SetReadOnly(_fullName, value);
    }

    /// <summary>Flatten just this field when the document is saved.</summary>
    public bool Flatten { get; set; }

    /// <summary>The "Export" flag (include in submit). Kept for source compatibility; has no effect on output.</summary>
    public bool Export { get; set; } = true;
}
