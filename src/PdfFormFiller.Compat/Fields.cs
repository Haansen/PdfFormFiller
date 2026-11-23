using PdfFormFiller;

namespace PdfFormFiller.Compat.Fields;

/// <summary>
/// Base of all form fields, as in PdfFormFiller.Compat.Fields.PdfField. A field object is a live handle: it follows renames and
/// always reads current state from the document.
/// </summary>
public abstract class PdfField
{
    internal readonly PdfFormDocument Doc;
    internal readonly FieldType Kind;
    private string _fullName;

    internal PdfField(PdfFormDocument doc, FormFieldInfo info)
    {
        Doc = doc;
        Kind = info.Type;
        _fullName = info.Name;
    }

    /// <summary>Current description of the field, read from the document on every access.</summary>
    internal FormFieldInfo Info
        => Doc.GetFields().FirstOrDefault(f => f.Name == _fullName)
           ?? throw new InvalidOperationException($"The field '{_fullName}' no longer exists in the document (its page was removed or it was flattened).");

    /// <summary>Short name (last dotted component). Setting it renames the field in the document.</summary>
    public string Name
    {
        get => _fullName.Contains('.') ? _fullName[(_fullName.LastIndexOf('.') + 1)..] : _fullName;
        set
        {
            Doc.RenameField(_fullName, value);
            _fullName = _fullName.Contains('.') ? _fullName[..(_fullName.LastIndexOf('.') + 1)] + value : value;
        }
    }

    public string FullName => _fullName;
    public bool Required => Info.Required;

    public bool ReadOnly
    {
        get => Info.ReadOnly;
        set => Doc.SetReadOnly(_fullName, value);
    }

    /// <summary>Flatten just this field when the document is saved.</summary>
    public bool Flatten { get; set; }

    /// <summary>The "Export" flag (include in submit). Kept for source compatibility; has no effect on output.</summary>
    public bool Export { get; set; } = true;
}
