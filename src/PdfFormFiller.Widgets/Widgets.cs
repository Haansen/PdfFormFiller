using PdfFormFiller;

namespace PdfFormFiller.Widgets;

/// <summary>PdfFormFiller.Widgets.PdfFormWidget look-alike: the form of a loaded document.</summary>
public class PdfFormWidget
{
    private readonly PdfDocument _doc;
    private PdfFormFieldWidgetCollection? _fields;

    internal PdfFormWidget(PdfDocument doc) => _doc = doc;

    /// <summary>All fields. <c>Fields</c> and <c>FieldsWidget</c> are the same collection.</summary>
    public PdfFormFieldWidgetCollection FieldsWidget => _fields ??= new PdfFormFieldWidgetCollection(_doc.Doc);
    public PdfFormFieldWidgetCollection Fields => FieldsWidget;

    /// <summary>Set to true to flatten the whole form on save.</summary>
    public bool IsFlatten { get; set; }

    /// <summary>Set to true to make every field read-only on save.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Appearances are always generated; kept for source compatibility.</summary>
    public bool NeedAppearance { get; set; } = true;

    internal void ApplyPendingFieldState()
    {
        if (_fields is null) return;
        _doc.Doc.ClearFlattenFields();
        var toFlatten = _fields.List.Where(f => f.Flatten).Select(f => f.FullName).ToArray();
        if (toFlatten.Length > 0) _doc.Doc.FlattenFields(toFlatten);
    }
}

/// <summary>
/// Live view of the document's fields. It re-synchronises with the document on every access, so fields added by
/// AppendPage or removed with a page show up immediately, while existing field objects (and their Flatten flags) are kept.
/// </summary>
public class PdfFormFieldWidgetCollection : IEnumerable<PdfField>
{
    private readonly PdfFormDocument _doc;
    private List<PdfField> _list = new();

    internal PdfFormFieldWidgetCollection(PdfFormDocument doc)
    {
        _doc = doc;
        Sync();
    }

    private List<PdfField> Sync()
    {
        var existing = new Dictionary<string, PdfField>();
        foreach (var f in _list) existing.TryAdd(f.FullName, f);
        var result = new List<PdfField>();
        foreach (var info in _doc.GetFields())
            result.Add(existing.TryGetValue(info.Name, out var w) && w.Kind == info.Type ? w : Wrap(info));
        _list = result;
        return _list;
    }

    private PdfField Wrap(FormFieldInfo info) => info.Type switch
    {
        FieldType.Text => new PdfTextBoxFieldWidget(_doc, info),
        FieldType.CheckBox => new PdfCheckBoxWidgetFieldWidget(_doc, info),
        FieldType.RadioButton => new PdfRadioButtonListFieldWidget(_doc, info),
        FieldType.ComboBox => new PdfComboBoxWidgetFieldWidget(_doc, info),
        FieldType.ListBox => new PdfListBoxWidgetFieldWidget(_doc, info),
        FieldType.PushButton => new PdfButtonWidgetFieldWidget(_doc, info),
        FieldType.Signature => new PdfSignatureFieldWidget(_doc, info),
        _ => new PdfUnknownFieldWidget(_doc, info),
    };

    public int Count => Sync().Count;
    public PdfField this[int index] => Sync()[index];

    /// <summary>Lookup by full name, then by short name. Null when missing.</summary>
    public PdfField? this[string name]
    {
        get
        {
            var list = Sync();
            return list.FirstOrDefault(f => f.FullName == name) ?? list.FirstOrDefault(f => f.Name == name);
        }
    }

    /// <summary>Snapshot of the current fields. Safe to index in a loop.</summary>
    public IList<PdfField> List => Sync().ToList();

    public IEnumerator<PdfField> GetEnumerator() => Sync().ToList().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public class PdfTextBoxFieldWidget : PdfField
{
    internal PdfTextBoxFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }

    public string Text
    {
        get => Doc.GetValue(FullName) ?? string.Empty;
        set => Doc.SetText(FullName, value);
    }

    public bool Multiline => Info.MultiLine;

    public PdfTextAlignment TextAlignment
    {
        get => Alignment.Get(this);
        set => Alignment.Set(this, value);
    }
}

internal static class Alignment
{
    public static PdfTextAlignment Get(PdfField f) => (PdfTextAlignment)(int)f.Doc.GetTextAlignment(f.FullName);

    public static void Set(PdfField f, PdfTextAlignment value)
        => f.Doc.SetTextAlignment(f.FullName, value switch
        {
            PdfTextAlignment.Center => PdfFormFiller.TextAlignment.Center,
            PdfTextAlignment.Right => PdfFormFiller.TextAlignment.Right,
            _ => PdfFormFiller.TextAlignment.Left,
        });
}

public class PdfCheckBoxWidgetFieldWidget : PdfField
{
    internal PdfCheckBoxWidgetFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }

    public bool Checked
    {
        get => (Doc.GetValue(FullName) ?? "Off") != "Off";
        set => Doc.SetCheckBox(FullName, value);
    }
}

public class PdfListFieldItem
{
    public string Text { get; }
    public string Value { get; }
    internal PdfListFieldItem(string value, string text) { Value = value; Text = text; }
}

public class PdfListFieldItemCollection : List<PdfListFieldItem>
{
    internal PdfListFieldItemCollection(IEnumerable<string> values) : base(values.Select(v => new PdfListFieldItem(v, v))) { }
}

public abstract class PdfChoiceFieldWidget : PdfField
{
    internal PdfChoiceFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }

    public PdfListFieldItemCollection Items => new(Info.Options);

    public PdfTextAlignment TextAlignment
    {
        get => Alignment.Get(this);
        set => Alignment.Set(this, value);
    }

    public int SelectedIndex
    {
        get => Info.Options.ToList().IndexOf(Doc.GetValue(FullName) ?? string.Empty);
        set => Doc.SetChoice(FullName, value >= 0 && value < Info.Options.Count ? Info.Options[value] : null);
    }

    public string SelectedValue
    {
        get => Doc.GetValue(FullName) ?? string.Empty;
        set => Doc.SetChoice(FullName, value);
    }
}

public class PdfComboBoxWidgetFieldWidget : PdfChoiceFieldWidget
{
    internal PdfComboBoxWidgetFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }
}

public class PdfListBoxWidgetFieldWidget : PdfChoiceFieldWidget
{
    internal PdfListBoxWidgetFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }
}

public class PdfRadioButtonListFieldWidget : PdfField
{
    internal PdfRadioButtonListFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }

    public PdfListFieldItemCollection Items => new(Info.Options);

    public int SelectedIndex
    {
        get => Info.Options.ToList().IndexOf(Doc.GetValue(FullName) ?? string.Empty);
        set => Doc.SetRadio(FullName, value >= 0 && value < Info.Options.Count ? Info.Options[value] : null);
    }

    public string SelectedValue
    {
        get => Doc.GetValue(FullName) ?? string.Empty;
        set => Doc.SetRadio(FullName, value);
    }
}

public class PdfButtonWidgetFieldWidget : PdfField
{
    internal PdfButtonWidgetFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }
}

public class PdfSignatureFieldWidget : PdfField
{
    internal PdfSignatureFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }
}

public class PdfUnknownFieldWidget : PdfField
{
    internal PdfUnknownFieldWidget(PdfFormDocument doc, FormFieldInfo info) : base(doc, info) { }
}
