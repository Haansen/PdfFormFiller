using System.Text;
using PdfFormFiller.Core;
using static PdfFormFiller.Internal.PdfExt;

namespace PdfFormFiller.Internal;

/// <summary>
/// Generates /AP /N appearance streams for widgets. Text uses the standard (non-embedded) Helvetica font with
/// WinAnsiEncoding, which covers Swedish and Western European characters in every PDF viewer.
/// </summary>
internal sealed class AppearanceBuilder
{
    private readonly PdfDocument _doc;
    private readonly PdfDictionary? _acro;
    private PdfDictionary? _helv;
    private PdfDictionary? _zadb;

    private const double Pad = 2;

    public AppearanceBuilder(PdfDocument doc)
    {
        _doc = doc;
        _acro = FieldTree.AcroForm(doc);
    }

    /// <summary>Throw instead of drawing '?' when a character exists in neither Helvetica (WinAnsi) nor the embedded font.</summary>
    public bool Strict { get; set; }

    public PdfDictionary Helv => _helv ??= StandardFont("/Helvetica", true);
    public PdfDictionary ZaDb => _zadb ??= StandardFont("/ZapfDingbats", false);

    private PdfDictionary StandardFont(string baseFont, bool winAnsi)
    {
        var f = new PdfDictionary();
        f["/Type"] = new PdfName("/Font");
        f["/Subtype"] = new PdfName("/Type1");
        f["/BaseFont"] = new PdfName(baseFont);
        if (winAnsi) f["/Encoding"] = new PdfName("/WinAnsiEncoding");
        _doc.RefFor(f);
        return f;
    }

    /// <summary>Makes sure the AcroForm /DR has /Helv and /ZaDb so viewers can regenerate appearances in Editable mode.</summary>
    public void EnsureDefaultResources()
    {
        if (_acro is null) return;
        var dr = _acro.GetOrCreateDict("/DR");
        var fonts = dr.GetOrCreateDict("/Font");
        if (!fonts.ContainsKey("/Helv")) fonts.SetRef("/Helv", Helv, _doc);
        if (!fonts.ContainsKey("/ZaDb")) fonts.SetRef("/ZaDb", ZaDb, _doc);
        if (!_acro.ContainsKey("/DA")) _acro["/DA"] = PdfString.FromText("/Helv 0 Tf 0 g");
    }

    // ------------------------------------------------------------------ public entry points

    /// <summary>True when a text/choice widget has no normal appearance stream, i.e. a viewer would show it empty.</summary>
    public static bool LacksAppearance(FieldNode field)
        => field.Widgets.Any(w => w.Dict("/AP")?.Dict("/N") is not PdfStream);

    public void Rebuild(FieldNode field)
    {
        switch (field.Type)
        {
            case FieldType.Text:
                foreach (var w in field.Widgets) SetNormal(w, BuildText(field, w));
                break;
            case FieldType.Signature:
            case FieldType.PushButton:
            case FieldType.Unknown:
                break;
            case FieldType.ComboBox:
                foreach (var w in field.Widgets) SetNormal(w, BuildCombo(field, w));
                break;
            case FieldType.ListBox:
                foreach (var w in field.Widgets) SetNormal(w, BuildListBox(field, w));
                break;
            case FieldType.CheckBox:
            case FieldType.RadioButton:
                foreach (var w in field.Widgets) EnsureButtonStates(field, w);
                break;
        }
    }

    // ------------------------------------------------------------------ text

    private PdfStream BuildText(FieldNode field, PdfDictionary widget)
    {
        var (w, h) = Size(widget);
        var sb = new StringBuilder();
        DrawBackgroundAndBorder(sb, widget, w, h);
        var bw = BorderWidth(widget);
        var da = field.DA(_acro);
        var text = Clean(field.ValueString() ?? string.Empty, field.FullName, out var uni);
        if (field.Has(FieldFlag.Password)) { text = new string('*', text.Length); uni = false; }
        var font = FontRes(uni);
        var maxLen = (field.Dict.Inherited("/MaxLen") as PdfNumber)?.IntValue ?? 0;

        sb.Append("/Tx BMC\nq\n");
        sb.Append($"{Fmt(bw)} {Fmt(bw)} {Fmt(w - 2 * bw)} {Fmt(h - 2 * bw)} re W n\n");
        if (text.Length > 0)
        {
            sb.Append("BT\n");
            var innerW = w - 2 * (bw + Pad);
            var innerH = h - 2 * (bw + Pad);
            if (field.Has(FieldFlag.Multiline))
            {
                var fs = da.FontSize > 0 ? da.FontSize : AutoMultiline(text, innerW, innerH);
                var lines = TextMeasurer.Wrap(text, fs, innerW);
                sb.Append($"{font} {Fmt(fs)} Tf {da.ColorOp}\n");
                var y = h - bw - Pad - fs * TextMeasurer.Ascent;
                var first = true;
                double prevX = 0;
                foreach (var line in lines)
                {
                    var x = AlignX(field.Quadding, bw + Pad, innerW, TextMeasurer.Width(line, fs));
                    if (first) { sb.Append($"{Fmt(x)} {Fmt(y)} Td\n"); first = false; }
                    else sb.Append($"{Fmt(x - prevX)} {Fmt(-fs * TextMeasurer.LineHeight)} Td\n");
                    prevX = x;
                    sb.Append(Show(line, uni)).Append(" Tj\n");
                }
            }
            else if (field.Has(FieldFlag.Comb) && maxLen > 0)
            {
                // comb field: MaxLen equally wide cells, one character centred in each
                text = text.Replace("\r", string.Empty).Replace("\n", string.Empty);
                if (text.Length > maxLen) text = text[..maxLen];
                var cell = w / maxLen;
                var fs = da.FontSize > 0 ? da.FontSize : AutoSingleLine("W", cell - 2, innerH);
                var y = (h - fs * TextMeasurer.CapHeight) / 2.0;
                sb.Append($"{font} {Fmt(fs)} Tf {da.ColorOp}\n");
                double prevX = 0, prevY = 0;
                for (var i = 0; i < text.Length; i++)
                {
                    var ch = text[i].ToString();
                    var x = cell * i + (cell - TextMeasurer.Width(ch, fs)) / 2.0;
                    sb.Append($"{Fmt(x - prevX)} {Fmt(y - prevY)} Td {Show(ch, uni)} Tj\n");
                    prevX = x; prevY = y;
                }
            }
            else
            {
                text = text.Replace("\r", " ").Replace("\n", " ");
                var fs = da.FontSize > 0 ? da.FontSize : AutoSingleLine(text, innerW, innerH);
                var tw = TextMeasurer.Width(text, fs);
                var x = AlignX(field.Quadding, bw + Pad, innerW, tw);
                var y = (h - fs * TextMeasurer.CapHeight) / 2.0;
                sb.Append($"{font} {Fmt(fs)} Tf {da.ColorOp}\n{Fmt(x)} {Fmt(y)} Td\n");
                sb.Append(Show(text, uni)).Append(" Tj\n");
            }
            sb.Append("ET\n");
        }
        sb.Append("Q\nEMC\n");
        return MakeForm(sb.ToString(), w, h, widget, needsHelv: !uni, needsZaDb: false, needsUni: uni);
    }

    private static double AlignX(int q, double left, double innerW, double textW) => q switch
    {
        1 => left + (innerW - textW) / 2.0,
        2 => left + innerW - textW,
        _ => left,
    };

    private static double AutoSingleLine(string text, double innerW, double innerH)
    {
        var fs = innerH / (TextMeasurer.Ascent + TextMeasurer.Descent);
        fs = Math.Min(fs, 12 * 4); // keep sane for tall boxes
        var tw = TextMeasurer.Width(text, fs);
        if (tw > innerW && tw > 0) fs *= innerW / tw;
        return Math.Max(4, Math.Floor(fs * 10) / 10);
    }

    private static double AutoMultiline(string text, double innerW, double innerH)
    {
        for (var fs = 12.0; fs >= 4; fs -= 0.5)
        {
            var lines = TextMeasurer.Wrap(text, fs, innerW);
            if (lines.Count * fs * TextMeasurer.LineHeight <= innerH) return fs;
        }
        return 4;
    }

    // ------------------------------------------------------------------ choice

    private PdfStream BuildCombo(FieldNode field, PdfDictionary widget)
    {
        var (w, h) = Size(widget);
        var sb = new StringBuilder();
        DrawBackgroundAndBorder(sb, widget, w, h);
        var bw = BorderWidth(widget);
        var da = field.DA(_acro);
        var value = field.ValueString() ?? string.Empty;
        var display = field.Options().FirstOrDefault(o => o.Export == value).Display ?? value;
        display = Clean(display, field.FullName, out var uni).Replace("\r", " ").Replace("\n", " ");

        sb.Append("/Tx BMC\nq\n");
        sb.Append($"{Fmt(bw)} {Fmt(bw)} {Fmt(w - 2 * bw)} {Fmt(h - 2 * bw)} re W n\n");
        if (display.Length > 0)
        {
            var innerW = w - 2 * (bw + Pad);
            var innerH = h - 2 * (bw + Pad);
            var fs = da.FontSize > 0 ? da.FontSize : AutoSingleLine(display, innerW, innerH);
            var x = AlignX(field.Quadding, bw + Pad, innerW, TextMeasurer.Width(display, fs));
            var y = (h - fs * TextMeasurer.CapHeight) / 2.0;
            sb.Append($"BT\n{FontRes(uni)} {Fmt(fs)} Tf {da.ColorOp}\n{Fmt(x)} {Fmt(y)} Td\n{Show(display, uni)} Tj\nET\n");
        }
        sb.Append("Q\nEMC\n");
        return MakeForm(sb.ToString(), w, h, widget, needsHelv: !uni, needsZaDb: false, needsUni: uni);
    }

    private PdfStream BuildListBox(FieldNode field, PdfDictionary widget)
    {
        var (w, h) = Size(widget);
        var sb = new StringBuilder();
        DrawBackgroundAndBorder(sb, widget, w, h);
        var bw = BorderWidth(widget);
        var da = field.DA(_acro);
        var fs = da.FontSize > 0 ? da.FontSize : 12;
        var lineH = fs * TextMeasurer.LineHeight;
        var selected = SelectedValues(field);
        var options = field.Options();
        var texts = new List<string>();
        var uni = false;
        foreach (var (_, disp) in options)
        {
            texts.Add(Clean(disp, field.FullName, out var u).Replace("\r", " ").Replace("\n", " "));
            uni |= u;
        }
        var topIndex = Math.Max(0, (field.Dict.Inherited("/TI") as PdfNumber)?.IntValue ?? 0);

        sb.Append("/Tx BMC\nq\n");
        sb.Append($"{Fmt(bw)} {Fmt(bw)} {Fmt(w - 2 * bw)} {Fmt(h - 2 * bw)} re W n\n");
        var y = h - bw - Pad;
        for (var i = topIndex; i < options.Count && y - lineH > -lineH; i++)
        {
            var export = options[i].Export;
            if (selected.Contains(export))
            {
                sb.Append($"0.6 0.75 0.85 rg {Fmt(bw)} {Fmt(y - lineH)} {Fmt(w - 2 * bw)} {Fmt(lineH)} re f\n");
            }
            sb.Append($"BT\n{FontRes(uni)} {Fmt(fs)} Tf {da.ColorOp}\n{Fmt(bw + Pad)} {Fmt(y - fs * TextMeasurer.Ascent)} Td\n{Show(texts[i], uni)} Tj\nET\n");
            y -= lineH;
        }
        sb.Append("Q\nEMC\n");
        return MakeForm(sb.ToString(), w, h, widget, needsHelv: !uni, needsZaDb: false, needsUni: uni);
    }

    private static HashSet<string> SelectedValues(FieldNode field)
    {
        var set = new HashSet<string>();
        switch (field.Dict.Inherited("/V"))
        {
            case PdfString s: set.Add(s.Text); break;
            case PdfArray a:
                foreach (var it in a.Items) if (Resolve(it) is PdfString s2) set.Add(s2.Text);
                break;
        }
        return set;
    }

    // ------------------------------------------------------------------ check box / radio

    /// <summary>Creates /AP /N/&lt;on&gt; and /AP /N/Off streams only when the widget has none (designer streams are kept).</summary>
    private void EnsureButtonStates(FieldNode field, PdfDictionary widget)
    {
        var ap = widget.GetOrCreateDict("/AP");
        var n = ap.Dict("/N");
        if (n is not null && n is not PdfStream && n.Count > 0) return; // has state streams already
        n = new PdfDictionary();
        ap["/N"] = n;

        var on = field.StateName(widget);
        var (w, h) = Size(widget);
        var isRadio = field.Type == FieldType.RadioButton;
        var caption = widget.Dict("/MK")?.Str("/CA");
        var glyph = !string.IsNullOrEmpty(caption) ? caption[0] : isRadio ? 'l' : '4';

        var off = new StringBuilder();
        DrawBackgroundAndBorder(off, widget, w, h, circle: isRadio);
        var onSb = new StringBuilder(off.ToString());
        var da = field.DA(_acro);
        var size = Math.Min(w, h) * 0.8;
        var gw = ZapfWidth(glyph) * size;
        var x = (w - gw) / 2.0;
        var y = (h - size * 0.7) / 2.0;
        onSb.Append($"q BT /ZaDb {Fmt(size)} Tf {da.ColorOp} {Fmt(x)} {Fmt(y)} Td ({glyph}) Tj ET Q\n");

        var onForm = MakeForm(onSb.ToString(), w, h, widget, needsHelv: false, needsZaDb: true);
        var offForm = MakeForm(off.ToString(), w, h, widget, needsHelv: false, needsZaDb: false);
        n.SetRef("/" + on, onForm, _doc);
        n.SetRef("/Off", offForm, _doc);
    }

    private static double ZapfWidth(char c) => c switch
    {
        '4' => 0.756, '8' => 0.776, 'l' => 0.791, 'n' => 0.761, 'u' => 0.759, 'H' => 0.816, _ => 0.78,
    };

    // ------------------------------------------------------------------ shared drawing

    private static (double w, double h) Size(PdfDictionary widget)
    {
        var (x0, y0, x1, y1) = widget.Rect();
        return (Math.Max(0.01, x1 - x0), Math.Max(0.01, y1 - y0));
    }

    private static double BorderWidth(PdfDictionary widget)
    {
        var bs = widget.Dict("/BS");
        if (bs is not null) return bs.Real("/W") ?? 1;
        var border = widget.Arr("/Border");
        if (border is not null && border.Count >= 3) return border.RealAt(2);
        return 1;
    }

    private static void DrawBackgroundAndBorder(StringBuilder sb, PdfDictionary widget, double w, double h, bool circle = false)
    {
        var mk = widget.Dict("/MK");
        if (mk is null) return;
        var bw = BorderWidth(widget);
        var bg = ColorOp(mk.Arr("/BG"), fill: true);
        var bc = ColorOp(mk.Arr("/BC"), fill: false);
        if (circle)
        {
            var r = Math.Min(w, h) / 2.0;
            if (bg is not null) sb.Append($"q {bg} {Circle(w / 2, h / 2, r)} f Q\n");
            if (bc is not null && bw > 0) sb.Append($"q {bc} {Fmt(bw)} w {Circle(w / 2, h / 2, r - bw / 2)} S Q\n");
            return;
        }
        if (bg is not null) sb.Append($"q {bg} 0 0 {Fmt(w)} {Fmt(h)} re f Q\n");
        if (bc is not null && bw > 0)
            sb.Append($"q {bc} {Fmt(bw)} w {Fmt(bw / 2)} {Fmt(bw / 2)} {Fmt(w - bw)} {Fmt(h - bw)} re S Q\n");
    }

    private static string Circle(double cx, double cy, double r)
    {
        const double k = 0.5523;
        var kr = k * r;
        return $"{Fmt(cx + r)} {Fmt(cy)} m " +
               $"{Fmt(cx + r)} {Fmt(cy + kr)} {Fmt(cx + kr)} {Fmt(cy + r)} {Fmt(cx)} {Fmt(cy + r)} c " +
               $"{Fmt(cx - kr)} {Fmt(cy + r)} {Fmt(cx - r)} {Fmt(cy + kr)} {Fmt(cx - r)} {Fmt(cy)} c " +
               $"{Fmt(cx - r)} {Fmt(cy - kr)} {Fmt(cx - kr)} {Fmt(cy - r)} {Fmt(cx)} {Fmt(cy - r)} c " +
               $"{Fmt(cx + kr)} {Fmt(cy - r)} {Fmt(cx + r)} {Fmt(cy - kr)} {Fmt(cx + r)} {Fmt(cy)} c h";
    }

    private static string? ColorOp(PdfArray? c, bool fill)
    {
        if (c is null) return null;
        var n = c.Count;
        var parts = Enumerable.Range(0, n).Select(i => Fmt(c.RealAt(i)));
        var op = n switch { 1 => fill ? "g" : "G", 3 => fill ? "rg" : "RG", 4 => fill ? "k" : "K", _ => null };
        return op is null ? null : string.Join(' ', parts) + " " + op;
    }

    private PdfStream MakeForm(string content, double w, double h, PdfDictionary widget, bool needsHelv, bool needsZaDb, bool needsUni = false)
    {
        var form = new PdfStream(Array.Empty<byte>());
        form["/Type"] = new PdfName("/XObject");
        form["/Subtype"] = new PdfName("/Form");
        form["/FormType"] = new PdfNumber(1);
        form["/BBox"] = NumberArray(0, 0, w, h);

        var rotate = widget.Dict("/MK")?.Int("/R") ?? 0;
        var matrix = ((rotate % 360 + 360) % 360) switch
        {
            90 => new double[] { 0, 1, -1, 0, w, 0 },
            180 => new double[] { -1, 0, 0, -1, w, h },
            270 => new double[] { 0, -1, 1, 0, 0, h },
            _ => null,
        };
        if (matrix is not null) form["/Matrix"] = NumberArray(matrix);

        var res = new PdfDictionary();
        var fonts = new PdfDictionary();
        if (needsHelv) fonts.SetRef("/Helv", Helv, _doc);
        if (needsZaDb) fonts.SetRef("/ZaDb", ZaDb, _doc);
        if (needsUni) fonts.SetRef("/PfU", UnicodeFont, _doc);
        res["/Font"] = fonts;
        res["/ProcSet"] = new PdfArray(new PdfName("/PDF"), new PdfName("/Text"));
        form["/Resources"] = res;

        form.SetData(Encoding.Latin1.GetBytes(content), compress: false);
        _doc.RefFor(form);
        return form;
    }

    // ------------------------------------------------------------------ text encoding / fonts

    /// <summary>
    /// Normalises a value for drawing and decides the font: Helvetica when every character is WinAnsi, otherwise the
    /// embedded Unicode font. Characters neither font has become '?' (or throw when <see cref="Strict"/>).
    /// </summary>
    private string Clean(string raw, string fieldName, out bool unicode)
    {
        unicode = false;
        var sb = new StringBuilder(raw.Length);
        List<char>? missing = null;
        var metrics = TextMeasurer.Metrics;
        foreach (var ch in raw)
        {
            var c = ch == '\t' ? ' ' : ch;
            if (c is '\n' or '\r') { sb.Append(c); continue; }
            if (char.IsControl(c)) continue;
            if (WinAnsi.CanEncode(c)) { sb.Append(c); continue; }
            if (metrics.HasGlyph(c)) { unicode = true; sb.Append(c); continue; }
            (missing ??= new List<char>()).Add(c);
            sb.Append('?');
        }
        if (missing is not null && Strict)
            throw new PdfFormException(
                $"Field '{fieldName}' contains characters that cannot be drawn: {string.Join(" ", missing.Distinct().Take(10).Select(c => $"U+{(int)c:X4}"))}.");
        return sb.ToString();
    }

    private static string FontRes(bool unicode) => unicode ? "/PfU" : "/Helv";

    /// <summary>String operand for Tj: WinAnsi literal for Helvetica, hex glyph ids for the Unicode font.</summary>
    private static string Show(string s, bool unicode)
    {
        if (!unicode) return WinAnsi.Literal(s);
        var m = TextMeasurer.Metrics;
        var sb = new StringBuilder(s.Length * 4 + 2);
        sb.Append('<');
        foreach (var c in s) sb.Append(m.GlyphOf(c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('>');
        return sb.ToString();
    }

    private PdfDictionary? _unicodeFont;

    /// <summary>Embedded Liberation Sans as a Type0/Identity-H font with a ToUnicode map. Created once per document, only when needed.</summary>
    private PdfDictionary UnicodeFont => _unicodeFont ??= BuildUnicodeFont();

    private PdfDictionary BuildUnicodeFont()
    {
        var m = TextMeasurer.Metrics;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double Scale(int v) => Math.Round(v * 1000.0 / m.UnitsPerEm);

        var file = new PdfStream(Array.Empty<byte>());
        file.SetData(m.Data, compress: true);
        file["/Length1"] = new PdfNumber(m.Data.Length);

        var desc = new PdfDictionary();
        desc["/Type"] = new PdfName("/FontDescriptor");
        desc["/FontName"] = new PdfName("/LiberationSans");
        desc["/Flags"] = new PdfNumber(32);
        desc["/FontBBox"] = NumberArray(Scale(m.BBox[0]), Scale(m.BBox[1]), Scale(m.BBox[2]), Scale(m.BBox[3]));
        desc["/ItalicAngle"] = new PdfNumber(0);
        desc["/Ascent"] = new PdfNumber(Scale(m.Ascender));
        desc["/Descent"] = new PdfNumber(Scale(m.Descender));
        desc["/CapHeight"] = new PdfNumber(Math.Round(TextMeasurer.CapHeight * 1000));
        desc["/StemV"] = new PdfNumber(80);
        desc.SetRef("/FontFile2", file, _doc);

        var widths = new PdfArray();
        for (var g = 0; g < m.GlyphCount; g++) widths.Add(new PdfNumber(m.GlyphWidth1000(g)));

        var sysInfo = new PdfDictionary();
        sysInfo["/Registry"] = PdfString.FromText("Adobe");
        sysInfo["/Ordering"] = PdfString.FromText("Identity");
        sysInfo["/Supplement"] = new PdfNumber(0);

        var cid = new PdfDictionary();
        cid["/Type"] = new PdfName("/Font");
        cid["/Subtype"] = new PdfName("/CIDFontType2");
        cid["/BaseFont"] = new PdfName("/LiberationSans");
        cid["/CIDSystemInfo"] = sysInfo;
        cid.SetRef("/FontDescriptor", desc, _doc);
        cid["/CIDToGIDMap"] = new PdfName("/Identity");
        cid["/DW"] = new PdfNumber(1000);
        cid["/W"] = new PdfArray(new PdfNumber(0), widths);

        // ToUnicode so text can be searched and copied from the finished PDF
        var cmap = new StringBuilder();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var pairs = new List<(int Glyph, int Char)>();
        var seen = new HashSet<int>();
        foreach (var kv in m.CharToGlyph)
            if (seen.Add(kv.Value)) pairs.Add((kv.Value, kv.Key));
        for (var i = 0; i < pairs.Count; i += 100)
        {
            var n = Math.Min(100, pairs.Count - i);
            cmap.Append(n.ToString(inv)).Append(" beginbfchar\n");
            for (var k = i; k < i + n; k++)
                cmap.Append('<').Append(pairs[k].Glyph.ToString("X4", inv)).Append("> <").Append(pairs[k].Char.ToString("X4", inv)).Append(">\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var toUnicode = new PdfStream(Array.Empty<byte>());
        toUnicode.SetData(Encoding.ASCII.GetBytes(cmap.ToString()), compress: true);

        var font = new PdfDictionary();
        font["/Type"] = new PdfName("/Font");
        font["/Subtype"] = new PdfName("/Type0");
        font["/BaseFont"] = new PdfName("/LiberationSans");
        font["/Encoding"] = new PdfName("/Identity-H");
        font["/DescendantFonts"] = new PdfArray(_doc.RefFor(cid));
        font.SetRef("/ToUnicode", toUnicode, _doc);
        _doc.RefFor(font);
        return font;
    }

    private void SetNormal(PdfDictionary widget, PdfStream form)
    {
        var ap = widget.GetOrCreateDict("/AP");
        ap.SetRef("/N", form, _doc);
        ap.Remove("/D"); // stale "down" appearance would show the old value while clicking
        ap.Remove("/R");
    }
}
