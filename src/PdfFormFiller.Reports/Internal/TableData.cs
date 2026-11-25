using System.Text;

namespace PdfFormFiller.Reports.Internal;

/// <summary>A group of rows: rows [Start, Start + Count) in <see cref="TableData.Order"/>.</summary>
internal sealed class GroupNode
{
    public required int Level { get; init; }
    public required GroupDefinition Definition { get; init; }
    /// <summary>What is grouped by, "Department"; may be empty.</summary>
    public required string Label { get; init; }
    public required string Title { get; init; }
    public int Start, Count;
    public List<GroupNode> Children { get; } = new();
    /// <summary>One text per column; null when the group has no totals.</summary>
    public string[]? Totals;
    internal List<int> Rows = new();
}

/// <summary>The cells of one column, one entry per item.</summary>
internal sealed class ColumnData
{
    public required ColumnDefinition Definition { get; init; }
    public required string Header { get; init; }
    public required List<string> Text { get; init; }
    public List<ChipStyle?>? Chips;
    public List<string?>? Links;
    /// <summary>1 true, 0 false, -1 neither; null when no icons are drawn.</summary>
    public List<sbyte>? Bools;
    public List<object?>? Values; // for totals
    public HAlign Align;
    public bool Numeric;
    /// <summary>Never broken over lines: numbers, dates, labels.</summary>
    public bool Solid;
}

/// <summary>The content of a table, read and formatted once.</summary>
internal sealed class TableData
{
    public required TableDefinition Definition { get; init; }
    public required ColumnData[] Columns { get; init; }
    public required int Count { get; init; }
    /// <summary>Items in the order they are shown; null when that is the order they came in.</summary>
    public int[]? Order { get; private set; }
    public List<GroupNode> Groups { get; private set; } = new();
    public string[]? Totals { get; private set; }
    public int GroupCount { get; private set; }
    public byte[]? Csv { get; private set; }

    public int Item(int position) => Order is null ? position : Order[position];

    public static TableData Read(TableDefinition def, ValueFormatter formatter, TextFace regular, TextFace bold, ReportTexts texts, bool csv)
    {
        var columns = def.Columns;
        if (columns.Count == 0)
        {
            if (MemberPath.IsSimple(def.ItemType)) columns = new() { ColumnDefinition.Itself(def.ItemType, texts.Value) };
            else
            {
                var hidden = new HashSet<string>(def.Excluded);
                foreach (var g in def.Groups) if (g.Column.Path is { } p) hidden.Add(p);
                columns = MemberPath.Automatic(def.ItemType, hidden).Select(ColumnDefinition.From).ToList();
            }
            if (columns.Count == 0)
                throw new PdfFormException($"{def.ItemType.Name} has no simple public properties to show. Choose columns with Column, or use PdfReport.Create(type, items) when the items are of a more specific type.");
        }

        var rows = def.Rows;
        var data = columns.Select(c =>
        {
            return new ColumnData
            {
                Definition = c,
                Header = bold.Clean(c.Display, keepBreaks: true).Trim(),
                Text = new List<string>(rows.Count),
                Chips = c.HasChips ? new(rows.Count) : null,
                Links = c.Link is not null || c.LinkValue ? new(rows.Count) : null,
                Bools = c.Core == typeof(bool) && c.BoolIcon && c.Formatter is null && c.Format is null ? new(rows.Count) : null,
                Values = c.Aggregate != AggregateKind.None ? new(rows.Count) : null,
            };
        }).ToArray();
        var chips = new Dictionary<Core.PdfColor, ChipStyle>();
        var shared = new Dictionary<string, string>(); // equal short texts are kept once
        var keys = def.Groups.Select(_ => new object?[rows.Count]).ToArray();
        var numbers = new bool[data.Length];
        var others = new bool[data.Length];
        var sb = csv ? new StringBuilder() : null;
        var groupColumns = def.Groups.Select(g => g.Column).Where(g => g.Path is null || columns.All(c => c.Path != g.Path)).ToList();
        string Separator() => formatter.ListSeparator;
        if (sb is not null)
            sb.AppendJoin(Separator(), groupColumns.Concat(columns).Select(c => formatter.CsvHeader(c.Display))).Append("\r\n");

        for (var r = 0; r < rows.Count; r++)
        {
            var item = rows[r];
            for (var g = 0; g < keys.Length; g++) keys[g][r] = Value(def.Groups[g].Column, item, r);
            if (sb is not null)
                foreach (var g in groupColumns)
                {
                    var v = keys[def.Groups.FindIndex(x => x.Column == g)][r];
                    sb.Append(formatter.Csv(v, v is null ? "" : Text(g, v, r))).Append(Separator());
                }
            for (var k = 0; k < data.Length; k++)
            {
                var d = data[k];
                var c = d.Definition;
                var value = Value(c, item, r);
                var raw = value is null ? c.Formatter is not null && c.FormatsNull && item is not null ? Run(c, r, () => c.Formatter(null)) ?? c.NullText ?? "" : c.NullText ?? ""
                    : Text(c, value, r);
                var text = regular.Clean(raw, keepBreaks: true).Trim('\n').TrimEnd(' ');
                if (text.Length is > 0 and <= 40)
                {
                    if (shared.TryGetValue(text, out var same)) text = same;
                    else if (shared.Count < 50_000) shared.Add(text, text);
                }
                d.Text.Add(text);
                if (value is not null)
                {
                    if (Aggregates.IsNumber(value.GetType())) numbers[k] = true;
                    else others[k] = true;
                }
                if (d.Values is not null)
                {
                    if (c.Aggregate is AggregateKind.Sum or AggregateKind.Average && value is not null && !Aggregates.CanAdd(value.GetType()))
                        throw new PdfFormException($"Column '{c.Display}', row {r + 1}: a {value.GetType().Name} cannot be added up.");
                    d.Values.Add(value);
                }
                d.Bools?.Add(value is bool b ? b ? (sbyte)1 : (sbyte)0 : (sbyte)-1);
                if (d.Chips is not null)
                {
                    ChipStyle? style = null;
                    if (value is not null && text.Length > 0 && Run(c, r, () => c.ChipFor(value)) is { } color && !chips.TryGetValue(color, out style)) chips[color] = style = new ChipStyle(color);
                    d.Chips.Add(style);
                }
                if (d.Links is not null)
                    d.Links.Add(text.Length == 0 || item is null ? null : Address(c.Link is not null ? Run(c, r, () => c.Link(item)) : value is Uri u ? u.OriginalString : raw));
                if (sb is not null)
                {
                    if (k > 0) sb.Append(Separator());
                    sb.Append(formatter.Csv(value, raw));
                }
            }
            sb?.Append("\r\n");
        }

        for (var k = 0; k < data.Length; k++)
        {
            var d = data[k];
            var c = d.Definition;
            var t = c.Core;
            d.Numeric = Aggregates.IsNumber(t) || numbers[k] && !others[k];
            d.Solid = d.Numeric || d.Chips is not null || t == typeof(bool) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan);
            // written with a function of the caller's, a number may be any text; only short ones are kept together
            if (c.Formatter is not null && d.Chips is null) d.Solid &= d.Text.All(x => x.Length <= 24);
            d.Align = c.Align ?? (d.Numeric || t == typeof(TimeSpan) ? HAlign.Right : d.Bools is not null ? HAlign.Center : HAlign.Left);
        }

        var table = new TableData { Definition = def, Columns = data, Count = rows.Count };
        if (sb is not null)
        {
            var body = Encoding.UTF8.GetBytes(sb.ToString());
            table.Csv = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
        }
        if (keys.Length > 0 && rows.Count > 0) table.Group(keys, formatter, bold, texts);
        var all = data.Any(d => d.Values is not null);
        if (all && rows.Count > 0) table.Totals = table.Total(formatter, bold, texts, 0, rows.Count);
        if (all) table.TotalGroups(table.Groups, formatter, bold, texts);
        foreach (var d in data) d.Values = null;
        return table;

        object? Value(ColumnDefinition c, object? item, int row) => item is null ? null : Run(c, row, () => c.Get(item));
        string Text(ColumnDefinition c, object value, int row) => Run(c, row, () => formatter.Cell(value, c));
    }

    private static TResult Run<TResult>(ColumnDefinition c, int row, Func<TResult> f)
    {
        try { return f(); }
        catch (Exception e) { throw new PdfFormException($"Column '{c.Display}', row {row + 1}: {e.Message}", e); }
    }



    /// <summary>An address that can be a link, otherwise null.</summary>
    private static string? Address(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 2000 || s.Any(c => char.IsControl(c) || char.IsSurrogate(c))) return null;
        if (!s.Contains(':') && s.Count(c => c == '@') == 1 && s[0] != '@' && s[^1] != '@' && !s.Contains(' ')) s = "mailto:" + s;
        else if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" || u.Scheme == "mailto" && s.All(char.IsAscii)) ? s : null;
    }

    // ------------------------------------------------------------------ groups and totals

    private void Group(object?[][] keys, ValueFormatter formatter, TextFace bold, ReportTexts texts)
    {
        var all = new GroupNode { Level = -1, Definition = Definition.Groups[0], Label = "", Title = "" };
        for (var i = 0; i < Count; i++) all.Rows.Add(i);
        var order = new List<int>(Count);
        Divide(all);
        Order = order.ToArray();
        Groups = all.Children;

        void Divide(GroupNode parent)
        {
            var level = parent.Level + 1;
            if (level == keys.Length) { parent.Start = order.Count; parent.Count = parent.Rows.Count; order.AddRange(parent.Rows); parent.Rows = null!; return; }
            var def = Definition.Groups[level];
            var c = def.Column;
            var label = bold.Clean(c.Header ?? (c.Path is null ? "" : c.Name));
            var found = new Dictionary<Key, GroupNode>();
            foreach (var row in parent.Rows)
            {
                var value = keys[level][row];
                try
                {
                    if (!found.TryGetValue(new Key(value), out var node))
                    {
                        var title = value is null && c.Formatter is not null && c.FormatsNull ? c.Formatter(null) : value is null ? c.NullText : formatter.Cell(value, c);
                        title = bold.Clean(title).Trim();
                        found[new Key(value)] = node = new GroupNode { Level = level, Definition = def, Label = label, Title = title.Length == 0 ? bold.Clean(texts.NoGroup) : title };
                        parent.Children.Add(node);
                        GroupCount++;
                    }
                    node.Rows.Add(row);
                }
                catch (Exception e) { throw new PdfFormException($"Grouping by '{c.Display}', row {row + 1}: {e.Message}", e); }
            }
            parent.Start = order.Count;
            foreach (var child in parent.Children) Divide(child);
            parent.Count = order.Count - parent.Start;
            parent.Rows = null!;
        }
    }

    private readonly record struct Key(object? Value)
    {
        public bool Equals(Key other) => object.Equals(Value, other.Value);
        public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    }

    private void TotalGroups(List<GroupNode> groups, ValueFormatter formatter, TextFace bold, ReportTexts texts)
    {
        foreach (var g in groups)
        {
            if (!g.Definition.NoTotals) g.Totals = Total(formatter, bold, texts, g.Start, g.Count);
            TotalGroups(g.Children, formatter, bold, texts);
        }
    }

    private string[] Total(ValueFormatter formatter, TextFace bold, ReportTexts texts, int start, int count)
    {
        var result = new string[Columns.Length];
        for (var k = 0; k < Columns.Length; k++)
        {
            result[k] = "";
            if (Columns[k].Values is not { } values) continue;
            var c = Columns[k].Definition;
            var part = new List<object?>(count);
            for (var i = start; i < start + count; i++) if (values[Item(i)] is { } v) part.Add(v);
            try { result[k] = bold.Clean(Aggregates.Text(c, Aggregates.Calculate(c, part), formatter, texts)).Trim(); }
            catch (Exception e) { throw new PdfFormException($"The total of column '{c.Display}' could not be made: {e.Message}", e); }
        }
        return result;
    }
}
