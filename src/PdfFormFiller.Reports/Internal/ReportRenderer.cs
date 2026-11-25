using System.Globalization;
using PdfFormFiller.Core;

namespace PdfFormFiller.Reports.Internal;

/// <summary>Lays out and draws a report: page head, facts, key figures, tables, page foot.</summary>
internal sealed class ReportRenderer
{
    private const double FootRoom = 24;       // above the bottom margin
    private const int MaxBookmarks = 5000;
    private const int StartNewTable = 2000;   // rows; see TableRenderer

    private readonly ReportDefinition _def;
    private readonly PdfBuilder _pdf;
    private readonly Palette _colors;
    private readonly TextFace _regular, _bold;
    private readonly ValueFormatter _formatter;
    private readonly PdfImage? _logo;
    private double _width, _height, _left, _right, _bottom;
    private PdfPageBuilder _page = null!;
    private double _y; // top of what comes next
    private int _bookmarks;

    private ReportRenderer(ReportDefinition def)
    {
        _def = def;
        _pdf = new PdfBuilder(def.Tagged);
        _colors = new Palette(def.Branding);
        (_regular, _bold) = TextFace.Create(_pdf, def.Branding);
        Culture = def.Culture ?? Cultures.Get("sv-SE");
        _formatter = new ValueFormatter(Culture, def.Texts);
        _logo = def.Branding.LogoData is { } logo ? _pdf.AddImage(logo) : null;
    }

    private CultureInfo Culture { get; }

    public static PdfBuilder Render(ReportDefinition def)
    {
        var r = new ReportRenderer(def);
        try { r.Run(); }
        catch (InvalidOperationException e) { throw new PdfFormException("The report could not be laid out: " + e.Message, e); }
        return r._pdf;
    }

    // ------------------------------------------------------------------ the document

    private void Run()
    {
        if (_def.Tables.Count == 0 && _def.Facts.Count == 0 && _def.KeyFigures.Count == 0)
            throw new PdfFormException("The report has no content. Add a table with Section, or use PdfReport.Create(items).");
        var title = _bold.Clean(_def.Title).Trim();
        var info = _pdf.Info;
        info.Title = title.Length > 0 ? title : null;
        info.Author = Plain(_def.Author ?? _def.Branding.CompanyName);
        info.Subject = Plain(_def.Subtitle);
        info.Language = _def.Language.Length > 0 ? _def.Language : null;
        info.CreationDate = _def.Timestamp;

        var tables = _def.Tables.Select(t => TableData.Read(t, _formatter, _regular, _bold, _def.Texts, _def.AttachCsv)).ToList();
        if (_def.Tagged)
        {
            var elements = tables.Sum(t => (long)t.Count * (t.Columns.Length + 1));
            if (elements > StructureBuilder.MaxElements * 0.95)
                throw new PdfFormException($"A report with {tables.Sum(t => t.Count):N0} rows is too large to be tagged for accessibility. Use Tagged(false), or make several reports.");
        }
        var metrics = TableMetrics.For(_def);
        var sizers = tables.Select(t => new ColumnSizer(t, _regular, _bold, metrics)).ToList();
        ChoosePage(sizers);

        AddPage(first: true);
        DrawHead(title);
        DrawFacts();
        DrawKeyFigures();

        var multiple = tables.Count > 1 || tables.Any(t => t.Definition.Title is not null);
        for (var i = 0; i < tables.Count; i++)
        {
            if (i > 0 && tables[i].Definition.PageBreak && !AtTop) AddPage();
            new TableRenderer(this, tables[i], sizers[i], multiple, i == 0).Draw();
        }
        DrawFeet();
        if (_def.AttachCsv) AttachCsv(tables, title);
    }

    private string? Plain(string? s)
    {
        var t = _regular.Clean(s).Trim();
        return t.Length > 0 ? t : null;
    }

    private void ChoosePage(List<ColumnSizer> sizers)
    {
        var (w, h) = (Math.Min(_def.Paper.Width, _def.Paper.Height), Math.Max(_def.Paper.Width, _def.Paper.Height));
        var m = _def.Margins;
        var landscape = _def.Orientation == PageOrientation.Landscape
                        || _def.Orientation == PageOrientation.Auto && sizers.Any(s => !s.FitsWell(w - m.Left - m.Right));
        (_width, _height) = landscape ? (h, w) : (w, h);
        (_left, _right, _bottom) = (m.Left, _width - m.Right, m.Bottom + FootRoom);
        if (_right - _left < 60 || _height - m.Top - _bottom < 120)
            throw new PdfFormException("The margins leave too little room on the paper. Use smaller margins or larger paper.");
    }

    private string PaperText => string.Create(CultureInfo.InvariantCulture, $"paper of {_width / PaperSize.Mm:0} x {_height / PaperSize.Mm:0} mm with these margins");

    private double Room => _right - _left;
    private double Remaining => _y - _bottom;
    private bool AtTop => _y >= _topOfBody - 0.01;
    private double _topOfBody;
    private ContentWriter C => _page.Content;

    private void AddPage(bool first = false)
    {
        _page?.Close();
        _page = _pdf.AddPage(_width, _height);
        _y = _height - _def.Margins.Top;
        if (!first) DrawRunningHead();
        _topOfBody = _y;
    }

    private void Need(double height)
    {
        if (height > Remaining && !AtTop) AddPage();
    }

    private StructId Structure(StructType type, StructId? parent = null) => _pdf.Structure.Add(parent ?? _pdf.Structure.Document, type);

    private void Text(StructId element, TextFace face, double size, PdfColor color, double x, double baseline, string text)
    {
        if (text.Length == 0) return;
        C.SetFillColor(color);
        C.BeginTagged(element);
        face.Draw(C, size, x, baseline, text);
        C.EndMarked();
    }

    private void Artifact(Action<ContentWriter> draw, ArtifactKind kind = ArtifactKind.Layout)
    {
        C.BeginArtifact(kind);
        draw(C);
        C.EndMarked();
    }

    private void Bookmark(ref OutlineItem? item, OutlineItem? parent, string title, double top, bool open = false)
    {
        item = null;
        if (++_bookmarks > MaxBookmarks) return;
        title = title.Length > 200 ? title[..200] : title;
        item = parent is null ? _pdf.AddOutline(title, _page, top) : parent.Add(title, _page, top);
        item.Open = open;
    }

    // ------------------------------------------------------------------ page head and foot

    private void DrawHead(string title)
    {
        const double stripe = 5;
        Artifact(c =>
        {
            c.SetFillColor(_colors.Primary);
            c.Rectangle(0, _height - stripe, _width, stripe);
            c.SetFillColor(_colors.Accent);
            c.Rectangle(0, _height - stripe, _left + 54, stripe);
        }, ArtifactKind.Pagination);

        var company = _bold.Clean(_def.Branding.CompanyName).Trim();
        var logoRight = _logo is not null && _def.Branding.LogoPlace == LogoPosition.Right;
        double logoW = 0, logoH = 0;
        if (_logo is not null)
        {
            var ratio = (double)_logo.Width / _logo.Height;
            logoH = 40;
            logoW = logoH * ratio;
            if (logoW > Math.Min(170, Room * 0.45)) { logoW = Math.Min(170, Room * 0.45); logoH = logoW / ratio; }
        }
        if (_logo is not null || company.Length > 0)
        {
            var h = Math.Max(logoH, company.Length > 0 ? 18 : 0);
            if (_logo is not null)
            {
                var x = logoRight ? _right - logoW : _left;
                var y = _y - h + (h - logoH) / 2;
                var figure = Structure(StructType.Figure);
                _pdf.Structure.SetAlt(figure, Plain(_def.Branding.LogoAlt) ?? (company.Length > 0 ? company : _def.Texts.Logo));
                _pdf.Structure.SetBox(figure, x, y, logoW, logoH);
                C.BeginTagged(figure);
                C.DrawImage(_logo, x, y, logoW, logoH);
                C.EndMarked();
            }
            if (company.Length > 0)
            {
                const double size = 12;
                var x = logoRight || _logo is null ? _left : _left + logoW + 12;
                var room = (logoRight ? _right - logoW - 12 : _right) - x;
                Text(Structure(StructType.P), _bold, size, _colors.Brand, x, _y - h / 2 - _bold.CapHeight * size / 2, _bold.Truncate(company, size, room));
            }
            _y -= h + 20;
        }
        else _y -= 4;

        if (title.Length > 0)
        {
            var h1 = Structure(StructType.H1);
            var lines = _bold.Wrap(title, 22, Room);
            if (lines.Count > 6) lines = lines.Take(5).Append(_bold.Truncate(string.Join(' ', lines.Skip(5)), 22, Room)).ToList();
            foreach (var line in lines)
            {
                _y -= 26;
                Text(h1, _bold, 22, _colors.Brand, _left, _y + 5, line);
            }
        }
        if (_regular.Clean(_def.Subtitle).Trim() is { Length: > 0 } subtitle)
        {
            var p = Structure(StructType.P);
            _y -= 2;
            foreach (var line in _regular.Wrap(subtitle, 11.5, Room).Take(6))
            {
                _y -= 16;
                Text(p, _regular, 11.5, _colors.Muted, _left, _y + 4, line);
            }
        }
        if (title.Length > 0)
        {
            _y -= 10;
            Artifact(c => { c.SetFillColor(_colors.Accent); c.RoundedRectangle(_left, _y, 44, 3, 1.5); });
            _y -= 16;
        }
        _topOfBody = _y;
    }

    private void DrawRunningHead()
    {
        var c = C;
        c.BeginArtifact(ArtifactKind.Pagination);
        c.SetFillColor(_colors.Primary);
        c.Rectangle(0, _height - 3, _width, 3);
        c.SetFillColor(_colors.Accent);
        c.Rectangle(0, _height - 3, _left + 54, 3);
        double x = _left, right = _right;
        const double h = 16;
        if (_logo is not null)
        {
            var lh = h;
            var lw = lh * _logo.Width / _logo.Height;
            if (lw > 80) { lw = 80; lh = lw * _logo.Height / _logo.Width; }
            var place = _def.Branding.LogoPlace == LogoPosition.Right ? _right - lw : _left;
            c.DrawImage(_logo, place, _y - h + (h - lh) / 2, lw, lh);
            if (_def.Branding.LogoPlace == LogoPosition.Right) right -= lw + 10; else x += lw + 10;
        }
        var baseline = _y - h / 2 - _bold.CapHeight * 9 / 2;
        var company = _bold.Clean(_def.Branding.CompanyName).Trim();
        var title = _bold.Clean(_def.Title).Trim();
        var main = title.Length > 0 ? title : company;
        var aside = title.Length > 0 ? company : "";
        if (_regular.Clean(_def.Subtitle).Trim() is { Length: > 0 } sub && title.Length > 0) aside = aside.Length > 0 ? sub + "  \u00B7  " + aside : sub;
        if (main.Length > 0)
        {
            main = _bold.Truncate(main, 9, (right - x) * (aside.Length > 0 ? 0.6 : 1));
            c.SetFillColor(_colors.Brand);
            _bold.Draw(c, 9, x, baseline, main);
            if (aside.Length > 0)
            {
                var room = right - x - _bold.Width(main, 9) - 16;
                aside = _regular.Truncate(aside, 8.5, room);
                c.SetFillColor(_colors.Muted);
                _regular.Draw(c, 8.5, right - _regular.Width(aside, 8.5), baseline, aside);
            }
        }
        if (main.Length > 0 || _logo is not null)
        {
            _y -= h + 6;
            c.SetStrokeColor(_colors.Line);
            c.SetLineWidth(0.6);
            c.Line(_left, _y, _right, _y);
            _y -= 12;
        }
        c.EndMarked();
    }

    private void DrawFeet()
    {
        _page.Close();
        var total = _pdf.Pages.Count;
        var footer = _regular.Clean(_def.Footer).Trim();
        var time = _def.Timestamp is { } t ? _regular.Clean(t.ToString("g", Culture)) : "";
        var digits = total.ToString(CultureInfo.InvariantCulture);
        foreach (var page in _pdf.Pages)
        {
            var c = page.Content;
            const double size = 7.5;
            var y = _def.Margins.Bottom + 1;
            c.BeginArtifact(ArtifactKind.Pagination);
            c.SetStrokeColor(_colors.Line);
            c.SetLineWidth(0.6);
            c.Line(_left, y + 11, _right, y + 11);
            var number = _regular.Clean($"{_def.Texts.Page} {(page.Index + 1).ToString(CultureInfo.InvariantCulture)} {_def.Texts.Of} {digits}");
            number = _regular.Truncate(number, size, Room);
            var x = _right - _regular.Width(number, size);
            c.SetFillColor(_colors.Muted);
            var left = _left;
            if (footer.Length > 0)
            {
                var text = _regular.Truncate(footer, size, Math.Max(0, x - left - 12 - (time.Length > 0 ? _regular.Width(time, size) + 18 : 0)));
                _regular.Draw(c, size, left, y, text);
            }
            if (time.Length > 0)
            {
                var tx = x - 18 - _regular.Width(time, size);
                if (footer.Length == 0) tx = left;
                if (tx >= left) _regular.Draw(c, size, tx, y, time);
            }
            _regular.Draw(c, size, x, y, number);
            c.EndMarked();
            page.Close();
        }
    }

    // ------------------------------------------------------------------ facts and key figures

    private void DrawFacts()
    {
        if (_def.Facts.Count == 0) return;
        const double pad = 11, label = 7.5, value = 9.5, gap = 18;
        var perRow = Math.Clamp((int)((Room - pad * 2 + gap) / (104 + gap)), 1, Math.Min(4, _def.Facts.Count));
        var w = (Room - pad * 2 - gap * (perRow - 1)) / perRow;
        var cells = _def.Facts.Select(f =>
            (Label: _regular.Truncate(_regular.Clean(f.Label).Trim(), label, w), Value: _bold.Wrap(_bold.Clean(Value(f.Label, f.Value), keepBreaks: true).Trim(), value, w).Take(4).ToList())).ToList();
        for (var i = 0; i < cells.Count;)
        {
            // as many rows as fit on the page share a panel
            var rows = new List<(int First, int Count, double Height)>();
            double h = pad * 2 - 9;
            var first = i;
            while (first < cells.Count)
            {
                var n = Math.Min(perRow, cells.Count - first);
                var rowHeight = 12 + cells.Skip(first).Take(n).Max(c => c.Value.Count) * 12.5 + 5;
                if (rows.Count > 0 && h + rowHeight > Remaining) break;
                if (rows.Count == 0) { Need(h + rowHeight); }
                rows.Add((first, n, rowHeight));
                h += rowHeight;
                first += n;
            }
            Artifact(c => { c.SetFillColor(_colors.Panel); c.RoundedRectangle(_left, _y - h, Room, h, 4); });
            var y = _y - pad + 2;
            foreach (var row in rows)
            {
                for (var k = 0; k < row.Count; k++)
                {
                    var x = _left + pad + k * (w + gap);
                    var p = Structure(StructType.P);
                    Text(p, _regular, label, _colors.Muted, x, y - 8, cells[row.First + k].Label);
                    for (var line = 0; line < cells[row.First + k].Value.Count; line++)
                        Text(p, _bold, value, Palette.Ink, x, y - 20.5 - line * 12.5, cells[row.First + k].Value[line]);
                }
                y -= row.Height;
            }
            _y -= h + 12;
            i = first;
            if (i < cells.Count) AddPage();
        }
    }

    private void DrawKeyFigures()
    {
        if (_def.KeyFigures.Count == 0) return;
        const double gap = 10, pad = 12;
        var perRow = Math.Clamp((int)((Room + gap) / (118 + gap)), 1, 6);
        var count = _def.KeyFigures.Count;
        if (perRow >= count) perRow = count;
        else while (perRow > 1 && count % perRow != 0 && count % (perRow - 1) == 0) perRow--; // even rows when one column fewer gives them
        var w = Math.Min((Room - gap * (perRow - 1)) / perRow, 260);
        var notes = _def.KeyFigures.Any(k => !string.IsNullOrEmpty(k.Note));
        var h = notes ? 60.0 : 48.0;
        for (var i = 0; i < count; i++)
        {
            var k = i % perRow;
            if (k == 0)
            {
                if (i > 0) _y -= h + gap;
                Need(h);
            }
            var x = _left + k * (w + gap);
            var (label, value, note) = _def.KeyFigures[i];
            Artifact(c =>
            {
                c.SetFillColor(PdfColor.White);
                c.SetStrokeColor(_colors.Line);
                c.SetLineWidth(0.8);
                c.RoundedRectangle(x, _y - h, w, h, 4, PaintMode.FillAndStroke);
                c.SetFillColor(_colors.Accent);
                c.RoundedRectangle(x + pad - 5, _y - h + 10, 2.4, h - 20, 1.2);
            });
            var tx = x + pad + 4;
            var room = w - pad * 2 - 2;
            var p = Structure(StructType.P);
            Text(p, _regular, 8, _colors.Muted, tx, _y - 17, _regular.Truncate(_regular.Clean(label).Trim(), 8, room));
            var text = _bold.Clean(Value(label, value)).Trim();
            var size = 17.0;
            while (size > 10 && _bold.Width(text, size) > room) size -= 0.5;
            Text(p, _bold, size, _colors.Brand, tx, _y - 37, _bold.Truncate(text, size, room));
            Text(p, _regular, 7.5, _colors.Muted, tx, _y - 50, _regular.Truncate(_regular.Clean(note).Trim(), 7.5, room));
        }
        _y -= h + 16;
    }

    private string Value(string label, object? value)
    {
        try { return _formatter.Text(value); }
        catch (Exception e) { throw new PdfFormException($"The value of '{label}' could not be written: {e.Message}", e); }
    }

    // ------------------------------------------------------------------ CSV

    private void AttachCsv(List<TableData> tables, string title)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tables.Count; i++)
        {
            var name = tables.Count > 1 ? tables[i].Definition.Title ?? title : title;
            if (_def.CsvFileName is { Length: > 0 } own)
            {
                name = own.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? own[..^4] : own;
                if (tables.Count > 1) name += " " + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            name = FileName(name);
            var unique = name;
            for (var n = 2; !used.Add(unique); n++) unique = name + " " + n.ToString(CultureInfo.InvariantCulture);
            _pdf.Attach(new PdfAttachment
            {
                FileName = unique + ".csv", Data = tables[i].Csv!, MimeType = "text/csv", Description = tables[i].Definition.Title ?? Plain(_def.Title),
                Relationship = AttachmentRelationship.Data, Date = _def.Timestamp,
            });
        }
    }

    private static string FileName(string s)
    {
        var chars = s.Select(c => char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned || "\\/:*?\"<>|".Contains(c) ? ' ' : c).ToArray();
        var name = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (name.Length > 80) name = name[..80].Trim();
        if (name.Length == 0) return "data";
        // device names, which Windows does not accept with any extension
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        var device = stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                     || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789\u00B9\u00B2\u00B3".Contains(stem[3]);
        return device ? "_" + name : name;
    }

    // ------------------------------------------------------------------ tables

    private enum BandKind { Group, Row, GroupTotal, Total, Empty }

    /// <summary>A horizontal part of a table: a row, a group heading or a line of totals.</summary>
    private sealed class Band
    {
        public required BandKind Kind { get; init; }
        public GroupNode? Group;
        public int Position;              // of a row
        public bool Shaded;
        public List<string>[] Cells = Array.Empty<List<string>>();
        public bool[]? Cut;               // cells where something was left out
        public int LabelSpan;             // totals: columns the label takes
        public double Height;
        public double Gap;                // space above, not at the top of a page
        public double Smallest;           // height of the part that must fit for the band to start on a page
        public bool KeepWithNext;
        public int Lines;
        public double LabelLine;          // totals: height of a label line of its own
    }

    /// <summary>
    /// Draws one table. Long tables are divided into several Table elements in the tag tree, at page breaks, because readers go
    /// through a whole element for every page they show.
    /// </summary>
    private sealed class TableRenderer
    {
        private readonly ReportRenderer _r;
        private readonly TableData _t;
        private readonly TableMetrics _m;
        private readonly double[] _x, _w;
        private readonly double _width;
        private readonly List<string>[] _headers;
        private readonly double _line, _headerLine, _headerHeight;
        private readonly bool _section, _first;
        private readonly Palette _p;
        private readonly TextFace _regular, _bold;
        private StructId _parent, _table, _body;
        private bool _tableOpen;
        private int _rowsInTable;
        private OutlineItem? _bookmark;
        private readonly OutlineItem?[] _groupBookmarks;

        public TableRenderer(ReportRenderer r, TableData t, ColumnSizer sizer, bool section, bool first)
        {
            (_r, _t, _section, _first, _p, _regular, _bold) = (r, t, section, first, r._colors, r._regular, r._bold);
            (_m, _w) = sizer.Solve(r.Room, r.PaperText);
            _x = new double[_w.Length];
            var x = r._left;
            for (var k = 0; k < _w.Length; k++) { _x[k] = x; x += _w[k]; }
            _width = x - r._left;
            _line = _m.Size * _regular.LineHeight;
            _headerLine = _m.HeaderSize * _bold.LineHeight;
            _headers = t.Columns.Select((c, k) => _bold.Wrap(c.Header, _m.HeaderSize, Inner(k))).ToArray();
            _headerHeight = _headers.Max(h => h.Count) * _headerLine + HeaderPad * 2;
            _groupBookmarks = new OutlineItem?[t.Definition.Groups.Count];
            if (Capacity < _line * 2 + _m.PadY * 2)
                throw new PdfFormException("The table head takes up the whole page. Use larger paper, smaller margins or shorter column headings.");
        }

        private double HeaderPad => _m.PadY + 1.6;
        private double PadLeft(int k) => ColumnSizer.PadLeft(_t, k, _m);
        private double Inner(int k) => Math.Max(1, _w[k] - _m.PadX - PadLeft(k));
        private bool Grouped => _t.Groups.Count > 0;
        private double GroupHeight => _m.Size * 1.1 * _bold.LineHeight + _m.PadY * 2 + 1;
        private double ContinuedHeight => Grouped ? _line + _m.PadY * 2 : 0;

        /// <summary>Room for rows on a page that only has this table.</summary>
        private double Capacity
        {
            get
            {
                var head = _r._def.Margins.Top + (_r._def.Title is not null || _r._def.Branding.CompanyName is not null || _r._logo is not null ? 34 : 0);
                return _r._height - head - _r._bottom - _headerHeight - ContinuedHeight;
            }
        }

        public void Draw()
        {
            var s = _r._pdf.Structure;
            _parent = _section && _t.Definition.Title is not null ? s.Add(s.Document, StructType.Sect) : s.Document;

            var bands = Bands().GetEnumerator();
            var ahead = new List<Band>();
            bool Fill(int n)
            {
                while (ahead.Count <= n && bands.MoveNext()) ahead.Add(bands.Current);
                return ahead.Count > n;
            }
            // the height that must fit for the first band in line to be drawn here: it, and what it must not be parted from
            double Required()
            {
                double h = 0;
                for (var i = 0; Fill(i); i++)
                {
                    if (!ahead[i].KeepWithNext) return h + ahead[i].Smallest;
                    h += ahead[i].Height;
                }
                return h;
            }

            DrawTitle(() => _headerHeight + Required());
            DrawHeader();

            while (Fill(0))
            {
                var b = ahead[0];
                var newPage = b.Kind == BandKind.Group && b.Group!.Definition.PageBreak && b.Group.Start > 0;
                var required = Required();
                if (newPage || required > _r.Remaining && (required <= Capacity || b.Smallest > _r.Remaining)) NextPage(b);
                if (b.Height <= _r.Remaining + 0.01) DrawBand(b, 0, b.Lines, true);
                else DrawInParts(b);
                ahead.RemoveAt(0);
            }
            _r._y -= 20;
        }

        private void DrawTitle(Func<double> following)
        {
            var title = _t.Definition.Title is { } own ? _bold.Clean(own).Trim() : "";
            var text = _regular.Clean(_t.Definition.Description, keepBreaks: true).Trim();
            var lines = text.Length > 0 ? _regular.Wrap(text, 9.5, _r.Room) : new List<string>();
            var titleLines = title.Length > 0 ? _bold.Wrap(title, 14, _r.Room).Take(4).ToList() : new List<string>();
            var h = titleLines.Count * 18.0 + (titleLines.Count > 0 ? 6 : 0) + (lines.Count > 0 ? lines.Count * 13 + 8 : 0);
            if (!_first && !_r.AtTop && titleLines.Count > 0) _r._y -= 8;
            var top = h + following();
            if (top > _r.Remaining && !_r.AtTop && (_r._page.Index > 0 || !_first || top < Capacity)) _r.AddPage();

            if (titleLines.Count > 0) _r.Bookmark(ref _bookmark, null, title, _r._y + 4, open: _t.Groups.Count <= 20);
            else if (_section && _r._def.Title is { } main) _r.Bookmark(ref _bookmark, null, _bold.Clean(main), _r._height);
            if (titleLines.Count > 0)
            {
                var h2 = _r.Structure(_r._def.Title is null && _first ? StructType.H1 : StructType.H2, _parent);
                foreach (var line in titleLines)
                {
                    _r._y -= 18;
                    _r.Text(h2, _bold, 14, _p.Brand, _r._left, _r._y + 4, line);
                }
                _r._y -= 6;
            }
            if (lines.Count == 0) return;
            var p = _r.Structure(StructType.P, _parent);
            foreach (var line in lines)
            {
                if (_r.Remaining < 13) _r.AddPage();
                _r._y -= 13;
                _r.Text(p, _regular, 9.5, Palette.Ink, _r._left, _r._y + 3, line);
            }
            _r._y -= 8;
        }

        // -------------------------------------------------------------- what the table consists of

        private IEnumerable<Band> Bands()
        {
            if (_t.Count == 0)
            {
                yield return new Band { Kind = BandKind.Empty, Height = 54, Smallest = 54 };
                yield break;
            }
            if (!Grouped)
                for (var i = 0; i < _t.Count; i++) yield return Row(i, i % 2 == 1, i == _t.Count - 1 && _t.Totals is not null);
            else
                foreach (var g in _t.Groups)
                    foreach (var b in GroupBands(g)) yield return b;
            if (_t.Totals is not null) yield return TotalBand(null, _t.Totals);
        }

        private IEnumerable<Band> GroupBands(GroupNode g)
        {
            var gap = g.Level == 0 && g.Start > 0 ? _m.Size * 0.8 : 0;
            yield return new Band { Kind = BandKind.Group, Group = g, Height = GroupHeight + gap, Smallest = GroupHeight + gap, Gap = gap, KeepWithNext = true };
            var lastOfAll = _t.Totals is not null && g.Start + g.Count == _t.Count;
            if (g.Children.Count == 0)
                for (var i = 0; i < g.Count; i++) yield return Row(g.Start + i, i % 2 == 1, i == g.Count - 1 && (g.Totals is not null || lastOfAll));
            else
                foreach (var child in g.Children)
                    foreach (var b in GroupBands(child))
                    {
                        // the last totals of a group belong with those of the group around it
                        if (b.Kind == BandKind.GroupTotal && b.Group == child && child == g.Children[^1] && g.Totals is not null) b.KeepWithNext = true;
                        yield return b;
                    }
            if (g.Totals is null) yield break;
            var total = TotalBand(g, g.Totals);
            total.KeepWithNext |= lastOfAll && g.Level == 0;
            yield return total;
        }

        private Band Row(int position, bool shaded, bool keep)
        {
            var item = _t.Item(position);
            var cells = new List<string>[_w.Length];
            bool[]? cut = null;
            var lines = 1;
            for (var k = 0; k < cells.Length; k++)
            {
                var c = _t.Columns[k];
                var text = c.Text[item];
                if (c.Chips?[item] is not null) cells[k] = new(1) { _regular.Truncate(text, _m.ChipSize, Math.Max(1, Inner(k) - _m.ChipExtra)) };
                else if (c.Bools is not null && c.Bools[item] >= 0) cells[k] = new(1) { text };
                else if (c.Definition.MaxLines == 1) cells[k] = new(1) { _regular.Truncate(text, _m.Size, Inner(k)) };
                else
                {
                    var wrapped = _regular.Wrap(text, _m.Size, Inner(k));
                    if (c.Definition.MaxLines is { } max && wrapped.Count > max)
                    {
                        wrapped[max - 1] = _regular.Truncate(wrapped[max - 1] + " " + wrapped[max], _m.Size, Inner(k));
                        if (!wrapped[max - 1].EndsWith('\u2026')) wrapped[max - 1] += "\u2026";
                        wrapped.RemoveRange(max, wrapped.Count - max);
                    }
                    cells[k] = wrapped;
                }
                if (cells[k].Count == 1 && !ReferenceEquals(cells[k][0], text) && cells[k][0] != text) (cut ??= new bool[cells.Length])[k] = true;
                lines = Math.Max(lines, cells[k].Count);
            }
            var h = lines * _line + _m.PadY * 2;
            return new Band
            {
                Kind = BandKind.Row, Position = position, Cells = cells, Cut = cut, Lines = lines, Height = h, Shaded = shaded && _r._def.Zebra, KeepWithNext = keep,
                Smallest = h <= Capacity ? h : Math.Min(lines, 3) * _line + _m.PadY * 2,
            };
        }

        private Band TotalBand(GroupNode? group, string[] totals)
        {
            var span = 0;
            while (span < totals.Length && totals[span].Length == 0 && _t.Columns[span].Definition.Aggregate == AggregateKind.None) span++;
            var label = group is null ? _bold.Clean(_r._def.Texts.Total) : _bold.Clean(_r._def.Texts.GroupTotal.Replace("{0}", group.Title));
            var cells = totals.Select((t, k) => k < span ? new List<string> { "" } : _bold.Wrap(t, _m.Size, Inner(k))).ToArray();
            var indent = group is null ? 0 : group.Level * 10;
            var lines = cells.Max(c => c.Count);
            double own = 0;
            var labelLines = new List<string> { label };
            if (span > 0)
            {
                labelLines = _bold.Wrap(label, _m.Size, Math.Max(20, Enumerable.Range(0, span).Sum(k => _w[k]) - _m.PadX * 2 - indent));
                cells[0] = labelLines;
                lines = Math.Max(lines, labelLines.Count);
            }
            else own = _line;
            var h = lines * _line + _m.PadY * 2 + own + 1;
            return new Band
            {
                Kind = group is null ? BandKind.Total : BandKind.GroupTotal, Group = group, Cells = cells, LabelSpan = span, Lines = lines, Height = h, LabelLine = own,
                Smallest = h <= Capacity ? h : Math.Min(lines, 3) * _line + _m.PadY * 2 + own,
            };
        }

        // -------------------------------------------------------------- pages

        private void NextPage(Band next)
        {
            _r.AddPage();
            if (_rowsInTable >= StartNewTable) _tableOpen = false;
            DrawHeader();
            // which groups go on here
            if (!Grouped || next.Kind is BandKind.Total or BandKind.Empty) return;
            var path = new List<GroupNode>();
            var level = next.Kind == BandKind.Row ? int.MaxValue : next.Kind == BandKind.Group ? next.Group!.Level - 1 : next.Group!.Level;
            var at = next.Kind == BandKind.Row ? next.Position : next.Group!.Start;
            for (var groups = _t.Groups; groups.Count > 0;)
            {
                var g = Find(groups, at);
                if (g.Level > level) break;
                path.Add(g);
                groups = g.Children;
            }
            if (path.Count == 0) return;
            var text = string.Join("  /  ", path.Select(g => g.Title)) + $" ({_bold.Clean(_r._def.Texts.Continued)})";
            var h = ContinuedHeight;
            var top = _r._y;
            _r.Artifact(c =>
            {
                c.SetFillColor(_p.Muted);
                _regular.Draw(c, _m.Size * 0.94, _r._left + _m.PadX, Baseline(top - _m.PadY, _line, _m.Size * 0.94, _regular), _regular.Truncate(text, _m.Size * 0.94, _width - _m.PadX * 2));
                c.SetStrokeColor(_p.Strong);
                c.SetLineWidth(0.6);
                c.Line(_r._left, top - h, _r._left + _width, top - h);
            }, ArtifactKind.Pagination);
            _r._y -= h;
        }

        private static GroupNode Find(List<GroupNode> groups, int position)
        {
            int lo = 0, hi = groups.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (groups[mid].Start <= position) lo = mid; else hi = mid - 1;
            }
            return groups[lo];
        }

        private void DrawInParts(Band b)
        {
            var from = 0;
            while (from < b.Lines)
            {
                var own = from == 0 ? b.LabelLine : 0;
                var fit = (int)Math.Floor((_r.Remaining - _m.PadY * 2 - own - 1) / _line + 1e-6);
                if (fit < 1)
                {
                    if (_r.AtTop) fit = 1; // cannot happen with the capacity checked; never loop
                    else { NextPage(b); continue; }
                }
                var to = Math.Min(b.Lines, from + fit);
                DrawBand(b, from, to, from == 0);
                from = to;
                if (from < b.Lines) NextPage(b);
            }
        }

        // -------------------------------------------------------------- drawing

        private static double Baseline(double top, double line, double size, TextFace face) => top - (line + face.CapHeight * size) / 2;

        private void DrawHeader()
        {
            var c = _r.C;
            var s = _r._pdf.Structure;
            var real = !_tableOpen;
            var top = _r._y;
            StructId row = default;
            if (real)
            {
                _table = s.Add(_parent, StructType.Table);
                row = s.Add(s.Add(_table, StructType.THead), StructType.TR);
                _body = s.Add(_table, StructType.TBody);
                _tableOpen = true;
                _rowsInTable = 0;
            }
            var color = _p.Filled ? _p.OnPrimary : _p.Brand;
            _r.Artifact(a =>
            {
                if (_p.Filled)
                {
                    a.SetFillColor(_p.Primary);
                    a.SaveState(); // only the upper corners are round
                    a.ClipRoundedRectangle(_r._left, top - _headerHeight - 8, _width, _headerHeight + 8, 3);
                    a.Rectangle(_r._left, top - _headerHeight, _width, _headerHeight);
                    a.RestoreState();
                    if (Colors.Contrast(_p.Primary, PdfColor.White) < 3) Rule(a, top - _headerHeight, _p.Brand, 1.2);
                }
                else
                {
                    Rule(a, top, _p.Brand, 1.4);
                    Rule(a, top - _headerHeight, _p.Brand, 0.7);
                }
            });
            c.SetFillColor(color);
            var lines = _headers.Max(h => h.Count);
            var last = 0;
            for (var k = 0; k < _headers.Length; k++)
            {
                var cell = real ? s.AddHeaderCell(row, HeaderScope.Column) : default;
                if (_t.Columns[k].Header.Length == 0) continue;
                if (real) c.BeginTagged(cell); else c.BeginArtifact(ArtifactKind.Pagination);
                // set at the bottom, next to the values
                var offset = (lines - _headers[k].Count) * _headerLine;
                DrawLines(c, _bold, _m.HeaderSize, k, _headers[k], 0, _headers[k].Count, top - HeaderPad - offset, _headerLine, _t.Columns[k].Align, k - last, _t.Columns[k].Header, false);
                last = k;
                c.EndMarked();
            }
            _r._y -= _headerHeight;
            _freshPage = true;
        }

        private void Rule(ContentWriter c, double y, PdfColor color, double width)
        {
            c.SetStrokeColor(color);
            c.SetLineWidth(width);
            c.Line(_r._left, y, _r._left + _width, y);
        }

        private bool _freshPage; // nothing but the head of the table drawn yet

        private void DrawBand(Band b, int from, int to, bool first)
        {
            switch (b.Kind)
            {
                case BandKind.Empty: DrawEmpty(b); break;
                case BandKind.Group: DrawGroup(b); break;
                case BandKind.Row: DrawRow(b, from, to); break;
                default: DrawTotals(b, from, to, first); break;
            }
            _freshPage = false;
        }

        private void DrawEmpty(Band b)
        {
            var text = _regular.Clean(_t.Definition.EmptyText ?? _r._def.Texts.Empty).Trim();
            text = _regular.Truncate(text, 10, _width - 20);
            var s = _r._pdf.Structure;
            var cell = s.AddCell(s.Add(_body, StructType.TR), _w.Length);
            var top = _r._y;
            _r.Artifact(c => Rule(c, top - b.Height, _p.Line, 0.8));
            _r.Text(cell, _regular, 10, _p.Muted, _r._left + (_width - _regular.Width(text, 10)) / 2, Baseline(top, b.Height, 10, _regular), text);
            _r._y -= b.Height;
        }

        private void DrawGroup(Band b)
        {
            var g = b.Group!;
            var c = _r.C;
            if (!_freshPage) _r._y -= b.Gap;
            var top = _r._y;
            var indent = g.Level * 10.0;
            var size = g.Level == 0 ? _m.Size * 1.1 : _m.Size;
            var h = GroupHeight;
            _r.Bookmark(ref _groupBookmarks[g.Level], g.Level == 0 ? _bookmark : _groupBookmarks[g.Level - 1], g.Title, top + 4);

            _r.Artifact(a =>
            {
                a.SetFillColor(g.Level == 0 ? _p.Band : _p.Panel);
                a.Rectangle(_r._left + indent, top - h, _width - indent, h);
                a.SetFillColor(_p.Accent);
                a.Rectangle(_r._left + indent, top - h, g.Level == 0 ? 3 : 1.6, h);
            });
            var rows = _regular.Clean((g.Count == 1 ? _r._def.Texts.OneRow : _r._def.Texts.Rows).Replace("{0}", g.Count.ToString("N0", _r.Culture)));
            var x = _r._left + indent + _m.PadX + 4;
            var right = _r._left + _width - _m.PadX;
            var small = _m.Size * 0.88;
            var rowsWidth = _regular.Width(rows, small);
            var room = right - x - rowsWidth - 14;
            var baseline = Baseline(top, h, size, _bold);
            var s = _r._pdf.Structure;
            var cell = s.AddHeaderCell(s.Add(_body, StructType.TR), HeaderScope.Row, _w.Length);
            c.BeginTagged(cell);
            if (g.Label.Length > 0)
            {
                var label = _regular.Truncate(g.Label, small, room * 0.4);
                c.SetFillColor(_p.Muted);
                _regular.Draw(c, small, x, baseline, label);
                var lw = _regular.Width(label, small) + 6;
                x += lw;
                room -= lw;
            }
            c.SetFillColor(_p.Brand);
            _bold.Draw(c, size, x, baseline, _bold.Truncate(g.Title, size, room));
            c.SetFillColor(_p.Muted);
            _regular.Draw(c, small, right - rowsWidth, baseline, rows);
            c.EndMarked();
            _r._y -= h;
        }

        private void DrawRow(Band b, int from, int to)
        {
            var item = _t.Item(b.Position);
            var c = _r.C;
            var s = _r._pdf.Structure;
            var top = _r._y;
            var h = (to - from) * _line + _m.PadY * 2;
            var columns = _t.Columns;

            c.BeginArtifact();
            if (b.Shaded)
            {
                c.SetFillColor(_p.Zebra);
                c.Rectangle(_r._left, top - h, _width, h);
            }
            if (to == b.Lines) Rule(c, top - h + 0.25, _p.Line, 0.5);
            if (from == 0)
                for (var k = 0; k < columns.Length; k++)
                {
                    if (columns[k].Chips?[item] is { } chip) DrawChip(c, k, b.Cells[k][0], chip, top);
                    else if (columns[k].Bools is { } bools && bools[item] >= 0) DrawTick(c, k, bools[item] == 1, top);
                }
            c.EndMarked();

            var row = from == 0 ? _rowElement = s.Add(_body, StructType.TR) : _rowElement;
            if (from == 0) { _cellElements ??= new StructId[columns.Length]; _rowsInTable++; }
            var last = 0;
            for (var k = 0; k < columns.Length; k++)
            {
                var d = columns[k];
                if (from == 0) _cellElements![k] = s.AddCell(row);
                var text = d.Text[item];
                if (text.Length == 0 || b.Cells[k].Count <= from) continue;
                var owner = _cellElements![k];
                var chip = d.Chips?[item];
                var link = d.Links?[item];
                if (link is not null) owner = s.Add(owner, StructType.Link);
                c.SetFillColor(chip?.Text ?? (link is not null ? LinkColor : Palette.Ink));
                c.BeginTagged(owner);
                if (chip is null && d.Bools is not null && d.Bools[item] >= 0)
                    _regular.DrawBlank(c, _m.Size, Start(k, 8 * _m.Size / 9, d.Align) + 4 * _m.Size / 9 - _regular.SpaceWidth * _m.Size / 2, Baseline(top - _m.PadY, _line, _m.Size, _regular), text, k - last);
                else if (chip is not null)
                {
                    var (x, _) = ChipPlace(k, b.Cells[k][0]);
                    var full = b.Cut is not null && b.Cut[k];
                    DrawLine(c, _regular, _m.ChipSize, x + _m.ChipPad + _m.ChipDot + _m.Size * 0.38, Baseline(top - _m.PadY, _line, _m.ChipSize, _regular), b.Cells[k][0], k - last, full ? text : null, false);
                }
                else
                {
                    var whole = from == 0 && to >= b.Cells[k].Count;
                    DrawLines(c, _regular, _m.Size, k, b.Cells[k], from, to, top - _m.PadY, _line, d.Align, k - last, whole ? text : null, d.Numeric);
                }
                c.EndMarked();
                if (link is not null) DrawLink(c, k, b, from, to, top, link, owner, text);
                last = k;
            }
            _r._y -= h;
        }

        private StructId _rowElement;
        private StructId[]? _cellElements;

        private PdfColor LinkColor => Colors.Darken(new PdfColor(0x1D, 0x5F, 0xA8), _p.Zebra, 5);

        private void DrawLink(ContentWriter c, int k, Band b, int from, int to, double top, string address, StructId element, string text)
        {
            var d = _t.Columns[k];
            var chip = d.Chips?[_t.Item(b.Position)] is not null;
            to = Math.Min(to, b.Cells[k].Count);
            for (var i = from; i < to; i++)
            {
                var t = b.Cells[k][i];
                if (t.Length == 0) continue;
                var w = chip ? ChipPlace(k, t).Width : _regular.Width(t, _m.Size);
                var x = chip ? ChipPlace(k, t).X : Start(k, w, d.Align);
                var y = top - _m.PadY - (i - from + 1) * _line;
                try { _r._page.AddLink(x, y, w, _line, address, text.Length > 200 ? text[..200].TrimEnd('\uD800', '\uDBFF') : text, element); }
                catch (PdfFormException) { return; } // an address that cannot be encoded: the text stays, without link
                if (chip) continue;
                c.BeginArtifact();
                c.SetStrokeColor(LinkColor);
                c.SetLineWidth(0.45);
                var baseline = Baseline(top - _m.PadY - (i - from) * _line, _line, _m.Size, _regular) - 1.3;
                c.Line(x, baseline, x + w, baseline);
                c.EndMarked();
            }
        }

        private (double X, double Width) ChipPlace(int k, string text)
        {
            var w = _regular.Width(text, _m.ChipSize) + _m.ChipExtra;
            return (Start(k, w, _t.Columns[k].Align), w);
        }

        private double Start(int k, double width, HAlign align) => align switch
        {
            HAlign.Right => _x[k] + _w[k] - _m.PadX - width,
            HAlign.Center => _x[k] + PadLeft(k) + (Inner(k) - width) / 2,
            _ => _x[k] + PadLeft(k),
        };

        private void DrawChip(ContentWriter c, int k, string text, ChipStyle chip, double top)
        {
            var (x, w) = ChipPlace(k, text);
            var h = _line + Math.Min(1.2, _m.PadY);
            var middle = top - _m.PadY - _line / 2;
            c.SetFillColor(chip.Background);
            c.RoundedRectangle(x, middle - h / 2, w, h, h / 2);
            c.SetFillColor(chip.Dot);
            c.RoundedRectangle(x + _m.ChipPad, middle - _m.ChipDot / 2, _m.ChipDot, _m.ChipDot, _m.ChipDot / 2);
        }

        private void DrawTick(ContentWriter c, int k, bool on, double top)
        {
            var u = _m.Size / 9; // drawn for 9 points
            var x = Start(k, 8 * u, _t.Columns[k].Align) + 4 * u;
            var y = top - _m.PadY - _line / 2;
            if (on)
            {
                c.SetStrokeColor(Tick);
                c.SetLineWidth(1.35 * u);
                // the two strokes overlap in the corner, so that it is sharp
                c.Line(x - 3.6 * u, y + 0.3 * u, x - 0.75 * u, y - 2.55 * u);
                c.Line(x - 1.7 * u, y - 2.5 * u, x + 3.9 * u, y + 3.1 * u);
            }
            else
            {
                c.SetStrokeColor(_p.Strong);
                c.SetLineWidth(1.1 * u);
                c.Line(x - 2.4 * u, y - 2.4 * u, x + 2.4 * u, y + 2.4 * u);
                c.Line(x - 2.4 * u, y + 2.4 * u, x + 2.4 * u, y - 2.4 * u);
            }
        }

        private static readonly PdfColor Tick = new(0x2E, 0x7D, 0x4F);

        private void DrawTotals(Band b, int from, int to, bool first)
        {
            var c = _r.C;
            var s = _r._pdf.Structure;
            var top = _r._y;
            var grand = b.Kind == BandKind.Total;
            var own = first ? b.LabelLine : 0;
            var h = (to - from) * _line + _m.PadY * 2 + own + 1;
            var indent = b.Group is { } g ? g.Level * 10.0 : 0;
            _r.Artifact(a =>
            {
                if (grand)
                {
                    a.SetFillColor(_p.Band);
                    a.Rectangle(_r._left, top - h, _width, h);
                }
                if (!first) return;
                a.SetStrokeColor(grand ? _p.Brand : _p.Strong);
                a.SetLineWidth(grand ? 1.3 : 0.8);
                a.Line(_r._left + indent, top, _r._left + _width, top);
            });
            var label = b.LabelLine > 0 ? _bold.Clean(grand ? _r._def.Texts.Total : _r._def.Texts.GroupTotal.Replace("{0}", b.Group!.Title)) : "";
            if (first)
            {
                var section = grand ? s.Add(_table, StructType.TFoot) : _body;
                if (label.Length > 0)
                {
                    var labelCell = s.AddHeaderCell(s.Add(section, StructType.TR), HeaderScope.Row, _w.Length);
                    _r.Text(labelCell, _bold, _m.Size, _p.Brand, _r._left + _m.PadX + indent, Baseline(top - _m.PadY - 0.5, _line, _m.Size, _bold), _bold.Truncate(label, _m.Size, _width - _m.PadX * 2 - indent));
                }
                _rowElement = s.Add(section, StructType.TR);
                _cellElements ??= new StructId[_w.Length];
                if (b.LabelSpan > 0) _cellElements[0] = s.AddHeaderCell(_rowElement, HeaderScope.Row, b.LabelSpan);
                for (var k = b.LabelSpan; k < _w.Length; k++) _cellElements[k] = s.AddCell(_rowElement);
            }
            var y = top - _m.PadY - 0.5 - own;
            c.SetFillColor(_p.Brand);
            var last = 0;
            for (var k = 0; k < _w.Length; k++)
            {
                if (k > 0 && k < b.LabelSpan) continue;
                var lines = b.Cells[k];
                if (lines.Count <= from || lines.Count == 1 && lines[0].Length == 0) continue;
                var isLabel = k == 0 && b.LabelSpan > 0;
                c.BeginTagged(_cellElements![k]);
                var whole = from == 0 && to >= lines.Count;
                DrawLines(c, _bold, _m.Size, k, lines, from, to, y, _line, isLabel ? HAlign.Left : _t.Columns[k].Align, k - last, whole ? string.Join(' ', lines) : null,
                    !isLabel, isLabel ? indent : 0);
                c.EndMarked();
                last = k;
            }
            _r._y -= h;
        }

        /// <summary>Lines [from, to) of a cell. <paramref name="text"/> is the whole text when these lines are all of it.</summary>
        private void DrawLines(ContentWriter c, TextFace face, double size, int k, List<string> lines, int from, int to, double top, double lineHeight, HAlign align, int tabs,
            string? text, bool number, double indent = 0)
        {
            to = Math.Min(to, lines.Count);
            double X(string s) => Start(k, align == HAlign.Left ? 0 : face.Width(s, size), align) + indent;
            double Y(int i) => Baseline(top - (i - from) * lineHeight, lineHeight, size, face);
            if (to - from == 1)
            {
                DrawLine(c, face, size, X(lines[from]), Y(from), lines[from], tabs, text is not null && !ReferenceEquals(text, lines[from]) && text != lines[from] ? text : null, number);
                return;
            }
            var actual = text is null ? string.Join(' ', lines.Skip(from).Take(to - from)) : text.Replace('\n', ' ');
            var start = from;
            while (start < to && lines[start].Length == 0) start++;
            if (start == to) return;
            var first = lines[start];
            var x = X(first);
            var rest = 0;
            if (actual.Length > MaxActualText)
            {
                face.Draw(c, size, x, Y(start), first, tabs, number);
                for (var i = start + 1; i < to; i++) face.Draw(c, size, X(lines[i]), Y(i), lines[i]);
                return;
            }
            // One text for all lines, so that a pasted cell stays in one piece. The TABs cannot be part of it (PDFium drops them),
            // so the first character is drawn in front, together with them.
            if (tabs > 0)
            {
                rest = StringInfo.GetNextTextElementLength(first);
                var head = first[..rest];
                face.Draw(c, size, x, Y(start), head, tabs);
                x += face.Width(head, size);
                actual = actual.TrimStart(' ').Length >= rest ? actual.TrimStart(' ')[rest..] : "";
            }
            c.BeginActualText(actual);
            face.Draw(c, size, x, Y(start), first[rest..]);
            for (var i = start + 1; i < to; i++) face.Draw(c, size, X(lines[i]), Y(i), lines[i]);
            c.EndMarked();
        }

        private const int MaxActualText = 2000;

        private static void DrawLine(ContentWriter c, TextFace face, double size, double x, double y, string shown, int tabs, string? actual, bool number)
        {
            if (actual is null || actual.Length > MaxActualText || shown.Length == 0) { face.Draw(c, size, x, y, shown, tabs, number); return; }
            var rest = 0;
            actual = actual.Replace('\n', ' ');
            if (tabs > 0)
            {
                rest = StringInfo.GetNextTextElementLength(shown);
                var head = shown[..rest];
                face.Draw(c, size, x, y, head, tabs);
                x += face.Width(head, size);
                actual = actual[Math.Min(rest, actual.Length)..];
            }
            if (rest == shown.Length) return;
            c.BeginActualText(actual);
            face.Draw(c, size, x, y, shown[rest..]);
            c.EndMarked();
        }
    }
}
