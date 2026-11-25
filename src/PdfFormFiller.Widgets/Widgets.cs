using PdfFormFiller;

namespace PdfFormFiller.Widgets;

/// <summary>PdfFormFiller.Widgets.PdfFormWidget look-alike: the form of a loaded document.</summary>
public class PdfFormWidget
{
    private readonly PdfDocument _doc;
    private PdfFormFieldWidgetCollection? _fields;

    internal PdfFormWidget(PdfDocument doc) => _doc = doc;

    /// <summary>All fields. <c>Fields</c> and <c>FieldsWidget</c> are the same collection.</summary>
    public PdfFormFieldWidgetCollection FieldsWidget => _fields ??= new PdfFormFieldWidgetCollection(_doc.State);
    public PdfFormFieldWidgetCollection Fields => FieldsWidget;

    /// <summary>Set to true to flatten the whole form on save.</summary>
    public bool IsFlatten { get; set; }

    /// <summary>Set to true to make every field read-only on save.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Appearances are always generated; kept for source compatibility.</summary>
    public bool NeedAppearance { get; set; } = true;

    /// <summary>A new document was loaded: the save-time switches belong to the old one.</summary>
    internal void Reset()
    {
        IsFlatten = false;
        ReadOnly = false;
    }

    /// <summary>Re-reads the field list (after pages were added or removed) so flags of vanished fields do not survive.</summary>
    internal void Resync() => _fields?.Refresh();

    /// <summary>Copies the per-field Flatten flags to the document. Always replaces what an earlier save left behind.</summary>
    internal void ApplyPendingFieldState()
    {
        _doc.Doc.ClearFlattenFields();
        if (_fields is null) return;
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
    private readonly FormState _state;
    private List<PdfField> _list = new();
    private int _generation;
    private IReadOnlyList<FormFieldInfo>? _syncedWith;

    internal PdfFormFieldWidgetCollection(FormState state)
    {
        _state = state;
        Sync();
    }

    internal void Refresh() => Sync();

    private List<PdfField> Sync()
    {
        var infos = _state.Infos; // throws when no document is loaded
        if (_generation != _state.Generation)
        {
            _list = new List<PdfField>(); // another document: field objects of the old one must not be carried over
            _generation = _state.Generation;
            _syncedWith = null;
        }
        if (ReferenceEquals(infos, _syncedWith)) return _list; // nothing changed since the last look
        _syncedWith = infos;
        var existing = new Dictionary<string, PdfField>();
        foreach (var f in _list) existing.TryAdd(f.FullName, f);
        var result = new List<PdfField>(infos.Count);
        foreach (var info in infos)
            result.Add(existing.TryGetValue(info.Name, out var w) && w.Kind == info.Type ? w : Wrap(info));
        _list = result;
        return _list;
    }

    private PdfField Wrap(FormFieldInfo info) => info.Type switch
    {
        FieldType.Text => new PdfTextBoxFieldWidget(_state, info),
        FieldType.CheckBox => new PdfCheckBoxWidgetFieldWidget(_state, info),
        FieldType.RadioButton => new PdfRadioButtonListFieldWidget(_state, info),
        FieldType.ComboBox => new PdfComboBoxWidgetFieldWidget(_state, info),
        FieldType.ListBox => new PdfListBoxWidgetFieldWidget(_state, info),
        FieldType.PushButton => new PdfButtonWidgetFieldWidget(_state, info),
        FieldType.Signature => new PdfSignatureFieldWidget(_state, info),
        _ => new PdfUnknownFieldWidget(_state, info),
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
    internal PdfTextBoxFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }

    public string Text
    {
        get => Doc.GetValue(FullName) ?? string.Empty;
        set => Write().SetText(FullName, value);
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
        => f.Write().SetTextAlignment(f.FullName, value switch
        {
            PdfTextAlignment.Center => PdfFormFiller.TextAlignment.Center,
            PdfTextAlignment.Right => PdfFormFiller.TextAlignment.Right,
            _ => PdfFormFiller.TextAlignment.Left,
        });
}

public class PdfCheckBoxWidgetFieldWidget : PdfField
{
    internal PdfCheckBoxWidgetFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }

    public bool Checked
    {
        get => (Doc.GetValue(FullName) ?? "Off") != "Off";
        set => Write().SetCheckBox(FullName, value);
    }
}

public class PdfListFieldItem
{
    /// <summary>The visible text of the item.</summary>
    public string Text { get; }

    /// <summary>The export value of the item, the one stored in the field.</summary>
    public string Value { get; }

    internal PdfListFieldItem(string value, string text) { Value = value; Text = text; }
}

public class PdfListFieldItemCollection : List<PdfListFieldItem>
{
    internal PdfListFieldItemCollection(FormFieldInfo info)
        : base(info.Options.Select((v, i) => new PdfListFieldItem(v, i < info.OptionTexts.Count ? info.OptionTexts[i] : v))) { }
}

/// <summary>Index handling shared by choice and radio fields.</summary>
internal static class Selection
{
    /// <summary>Index of the selected option, -1 when nothing is selected (also when the selected value is not an option).</summary>
    public static int Get(PdfField f)
    {
        var value = f.Doc.GetValue(f.FullName);
        return value is null ? -1 : f.Info.Options.ToList().IndexOf(value);
    }

    /// <summary>The export value for <paramref name="index"/>; null for -1 (clear). Anything else outside the list is rejected instead of clearing the field.</summary>
    public static string? Resolve(PdfField f, int index)
    {
        if (index == -1) return null;
        var options = f.Info.Options;
        if (index < 0 || index >= options.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Field '{f.FullName}' has {options.Count} items; use -1 to clear the selection.");
        return options[index];
    }
}

public abstract class PdfChoiceFieldWidget : PdfField
{
    internal PdfChoiceFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }

    public PdfListFieldItemCollection Items => new(Info);

    public PdfTextAlignment TextAlignment
    {
        get => Alignment.Get(this);
        set => Alignment.Set(this, value);
    }

    /// <summary>Index of the selected item, -1 for none. Setting -1 clears the selection; an index outside the list throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    public int SelectedIndex
    {
        get => Selection.Get(this);
        set
        {
            var export = Selection.Resolve(this, value); // before Write(): it reads the cache that Write() drops
            Write().SetChoice(FullName, export);
        }
    }

    public string SelectedValue
    {
        get => Doc.GetValue(FullName) ?? string.Empty;
        set => Write().SetChoice(FullName, value);
    }
}

public class PdfComboBoxWidgetFieldWidget : PdfChoiceFieldWidget
{
    internal PdfComboBoxWidgetFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }
}

public class PdfListBoxWidgetFieldWidget : PdfChoiceFieldWidget
{
    internal PdfListBoxWidgetFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }
}

public class PdfRadioButtonListFieldWidget : PdfField
{
    internal PdfRadioButtonListFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }

    public PdfListFieldItemCollection Items => new(Info);

    /// <summary>Index of the selected button, -1 for none. Setting -1 clears the selection; an index outside the list throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    public int SelectedIndex
    {
        get => Selection.Get(this);
        set
        {
            var export = Selection.Resolve(this, value);
            Write().SetRadio(FullName, export);
        }
    }

    public string SelectedValue
    {
        get => Doc.GetValue(FullName) ?? string.Empty;
        set => Write().SetRadio(FullName, value);
    }
}

public class PdfButtonWidgetFieldWidget : PdfField
{
    internal PdfButtonWidgetFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }
}

public class PdfSignatureFieldWidget : PdfField
{
    internal PdfSignatureFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }
}

public class PdfUnknownFieldWidget : PdfField
{
    internal PdfUnknownFieldWidget(FormState state, FormFieldInfo info) : base(state, info) { }
}
