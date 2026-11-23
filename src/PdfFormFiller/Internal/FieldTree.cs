using PdfFormFiller.Core;

namespace PdfFormFiller.Internal;

/// <summary>A terminal form field (one that carries a value) together with its widget annotations.</summary>
internal sealed class FieldNode
{
    public required string FullName { get; init; }
    public required PdfDictionary Dict { get; init; }
    public required List<PdfDictionary> Widgets { get; init; }
    public required FieldType Type { get; init; }
    public required int Flags { get; init; }

    public bool Has(FieldFlag f) => (Flags & (int)f) != 0;

    /// <summary>Choice options as (export, display).</summary>
    public List<(string Export, string Display)> Options()
    {
        var list = new List<(string, string)>();
        if (Dict.Inherited("/Opt") is not PdfArray opt) return list;
        foreach (var raw in opt.Items)
        {
            switch (PdfExt.Resolve(raw))
            {
                case PdfString s: list.Add((s.Text, s.Text)); break;
                case PdfArray pair when pair.Count >= 2:
                    var e = (PdfExt.Resolve(pair[0]) as PdfString)?.Text ?? string.Empty;
                    var d = (PdfExt.Resolve(pair[1]) as PdfString)?.Text ?? e;
                    list.Add((e, d));
                    break;
                case PdfArray single when single.Count == 1:
                    var v = (PdfExt.Resolve(single[0]) as PdfString)?.Text ?? string.Empty;
                    list.Add((v, v));
                    break;
            }
        }
        return list;
    }

    public string? ValueString()
        => Dict.Inherited("/V") switch
        {
            PdfString s => s.Text,
            PdfName n => n.Bare,
            PdfArray a when a.Count > 0 => (PdfExt.Resolve(a[0]) as PdfString)?.Text,
            _ => null,
        };

    public DefaultAppearance DA(PdfDictionary? acroForm)
    {
        var da = (Dict.Inherited("/DA") as PdfString)?.Text ?? acroForm?.Str("/DA");
        return DefaultAppearance.Parse(da);
    }

    public int Quadding => (Dict.Inherited("/Q") as PdfNumber)?.IntValue ?? 0;

    /// <summary>
    /// The "on" state name for a button widget. Falls back to the widget's index within the group for radios
    /// (so each option stays distinct) or "Yes" for check boxes when the PDF carries no appearance states.
    /// </summary>
    public string StateName(PdfDictionary widget)
    {
        var s = FieldTree.OnStateOrNull(widget);
        if (s is not null) return s;
        if (Type == FieldType.RadioButton)
        {
            var idx = Widgets.FindIndex(w => ReferenceEquals(w, widget));
            return (idx < 0 ? 0 : idx).ToString();
        }
        return "Yes";
    }

    /// <summary>Export value of a radio widget: /Opt entry when present, otherwise the state name.</summary>
    public string ExportValue(PdfDictionary widget)
    {
        var state = StateName(widget);
        var opt = Options();
        if (opt.Count > 0 && int.TryParse(state, out var i) && i >= 0 && i < opt.Count) return opt[i].Export;
        return state;
    }
}

[Flags]
internal enum FieldFlag
{
    ReadOnly = 1 << 0,
    Required = 1 << 1,
    Multiline = 1 << 12,
    Password = 1 << 13,
    NoToggleToOff = 1 << 14,
    Radio = 1 << 15,
    PushButton = 1 << 16,
    Combo = 1 << 17,
    Edit = 1 << 18,
    MultiSelect = 1 << 21,
    Comb = 1 << 24,
    RadiosInUnison = 1 << 25,
}

internal static class FieldTree
{
    public static PdfDictionary? AcroForm(PdfDocument doc) => doc.Catalog.Dict("/AcroForm");

    public static List<FieldNode> Build(PdfDocument doc)
    {
        var result = new List<FieldNode>();
        var fields = AcroForm(doc)?.Arr("/Fields");
        if (fields is null) return result;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var root in fields.Dicts()) Walk(root, null, result, seen, 0);
        return result;
    }

    /// <summary>Real forms nest a handful of levels; the limit only stops hostile files from exhausting the stack.</summary>
    private const int MaxFieldDepth = 256;

    private static void Walk(PdfDictionary node, string? prefix, List<FieldNode> result, HashSet<PdfDictionary> seen, int depth)
    {
        if (depth > MaxFieldDepth || !seen.Add(node)) return;
        var partial = node.Str("/T");
        var name = partial is null ? prefix : prefix is null ? partial : prefix + "." + partial;

        var kids = node.Arr("/Kids")?.Dicts().ToList() ?? new List<PdfDictionary>();
        var childFields = kids.Where(k => k.ContainsKey("/T")).ToList();
        var widgetKids = kids.Where(k => !k.ContainsKey("/T") && k.IsWidget()).ToList();

        if (childFields.Count > 0)
        {
            foreach (var c in childFields) Walk(c, name, result, seen, depth + 1);
            return;
        }

        var ft = node.Inherited("/FT") as PdfName;
        if (ft is null) return;

        var widgets = node.IsWidget() ? new List<PdfDictionary> { node } : widgetKids;
        var flags = (node.Inherited("/Ff") as PdfNumber)?.IntValue ?? 0;
        result.Add(new FieldNode
        {
            FullName = name ?? string.Empty,
            Dict = node,
            Widgets = widgets,
            Type = Classify(ft.Value, flags),
            Flags = flags,
        });
    }

    public static FieldType Classify(string ft, int flags) => ft switch
    {
        "/Tx" => FieldType.Text,
        "/Btn" when (flags & (int)FieldFlag.PushButton) != 0 => FieldType.PushButton,
        "/Btn" when (flags & (int)FieldFlag.Radio) != 0 => FieldType.RadioButton,
        "/Btn" => FieldType.CheckBox,
        "/Ch" when (flags & (int)FieldFlag.Combo) != 0 => FieldType.ComboBox,
        "/Ch" => FieldType.ListBox,
        "/Sig" => FieldType.Signature,
        _ => FieldType.Unknown,
    };

    /// <summary>The "on" appearance state name (anything in /AP /N other than /Off), or null when the widget has no states.</summary>
    public static string? OnStateOrNull(PdfDictionary widget)
    {
        foreach (var sub in new[] { "/N", "/D" })
        {
            var states = widget.Dict("/AP")?.Dict(sub);
            if (states is null || states is PdfStream) continue;
            foreach (var key in states.Keys)
                if (key != "/Off") return key[1..];
        }
        return null;
    }

    /// <summary>Map annotation dictionary → page index for every annotation on every page.</summary>
    public static Dictionary<PdfDictionary, int> AnnotationPages(PdfDocument doc)
    {
        var map = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var p = 0; p < doc.Pages.Count; p++)
        {
            var annots = doc.Pages[p].Arr("/Annots");
            if (annots is null) continue;
            foreach (var a in annots.Dicts()) map.TryAdd(a, p);
        }
        return map;
    }

    /// <summary>Removes widgets that are no longer on any page, and fields left without widgets. Call after page removal or merge.</summary>
    public static void PruneOrphans(PdfDocument doc)
    {
        var fields = AcroForm(doc)?.Arr("/Fields");
        if (fields is null) return;
        var live = AnnotationPages(doc);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var i = fields.Count - 1; i >= 0; i--)
        {
            if (PdfExt.Resolve(fields[i]) is not PdfDictionary root) { fields.RemoveAt(i); continue; }
            if (!Prune(root, live, seen, 0)) fields.RemoveAt(i);
        }
    }

    private static bool Prune(PdfDictionary node, Dictionary<PdfDictionary, int> live, HashSet<PdfDictionary> seen, int depth)
    {
        if (depth > MaxFieldDepth) return false;
        if (!seen.Add(node)) return true;
        var kids = node.Arr("/Kids");
        if (kids is null) return !node.IsWidget() || live.ContainsKey(node);
        for (var i = kids.Count - 1; i >= 0; i--)
        {
            if (PdfExt.Resolve(kids[i]) is not PdfDictionary kid) { kids.RemoveAt(i); continue; }
            if (!Prune(kid, live, seen, depth + 1)) kids.RemoveAt(i);
        }
        return kids.Count > 0;
    }
}
