using System.Globalization;

namespace PdfFormFiller.Reports.Internal;

/// <summary>Text sizes and spacing of a table.</summary>
internal readonly record struct TableMetrics(double Size, double PadX, double PadY)
{
    public double HeaderSize => Math.Max(Size * 0.92, Math.Min(Size, 6));
    public double ChipSize => Size * 0.92;
    public double ChipPad => Size * 0.62;
    public double ChipDot => Size * 0.36;
    /// <summary>Width a label needs besides its text.</summary>
    public double ChipExtra => ChipPad * 2 + ChipDot + Size * 0.38;

    public TableMetrics Scaled(double s) => new(Size * s, Math.Max(2.5, PadX * s), PadY * (0.5 + s / 2));

    public static TableMetrics For(ReportDefinition def)
    {
        var (size, x, y) = def.Density switch
        {
            ReportDensity.Compact => (8.0, 4.5, 1.9),
            ReportDensity.Comfortable => (9.5, 7.0, 5.6),
            _ => (9.0, 5.8, 3.5),
        };
        if (def.FontSize is not { } own) return new TableMetrics(size, x, y);
        return new TableMetrics(own, x * own / size, y * own / size);
    }
}

/// <summary>Column widths of one table.</summary>
internal sealed class ColumnSizer
{
    private const double SmallestSize = 6;
    private const double LongestWord = 88;  // a longer word may be broken rather than widen its column
    private const double LongestSolid = 190; // likewise a number
    private const double Comfortable = 120; // text wraps well in columns of this width

    private readonly record struct Need(double Natural, double Minimum, double Chip, double HeaderNatural, double HeaderMinimum);

    private readonly TableData _table;
    private readonly TableMetrics _base;
    private readonly Need[] _needs; // at size 1

    public ColumnSizer(TableData table, TextFace regular, TextFace bold, TableMetrics metrics)
    {
        _table = table;
        _base = metrics;
        _needs = table.Columns.Select((c, k) =>
        {
            double natural = 0, minimum = 0, chip = 0;
            string? previous = null;
            var i = 0;
            foreach (var text in c.Text)
            {
                var at = i++;
                if (ReferenceEquals(text, previous)) continue; // runs of equal texts are common
                var m = regular.Measure(text);
                if (c.Chips?[at] is not null) chip = Math.Max(chip, m.Natural);
                else if (c.Bools is not null && c.Bools[at] >= 0) minimum = Math.Max(minimum, 1);
                else { natural = Math.Max(natural, m.Natural); minimum = Math.Max(minimum, m.Minimum); }
                previous = text;
            }
            foreach (var totals in Totals(table))
            {
                var m = bold.Measure(totals[k]);
                natural = Math.Max(natural, m.Natural);
                minimum = Math.Max(minimum, c.Solid ? m.Natural : m.Minimum);
            }
            var header = bold.Measure(c.Header);
            return new Need(natural, minimum, chip, header.Natural, header.Minimum);
        }).ToArray();
    }

    private static IEnumerable<string[]> Totals(TableData table)
    {
        if (table.Totals is not null) yield return table.Totals;
        var stack = new Stack<GroupNode>(table.Groups);
        while (stack.TryPop(out var g))
        {
            if (g.Totals is not null) yield return g.Totals;
            foreach (var child in g.Children) stack.Push(child);
        }
    }

    /// <summary>Space before a cell's content; more where left aligned text follows right aligned numbers, so that they do not run together.</summary>
    public static double PadLeft(TableData table, int k, TableMetrics m)
        => k > 0 && table.Columns[k - 1].Align == HAlign.Right && table.Columns[k].Align != HAlign.Right ? m.PadX * 2.2 : m.PadX;

    private (double Minimum, double Natural) Range(int k, TableMetrics m)
    {
        var c = _table.Columns[k];
        var d = c.Definition;
        var n = _needs[k];
        var pad = m.PadX + PadLeft(_table, k, m);
        if (d.Width is { } fix) return (fix, fix);
        var chip = n.Chip > 0 ? n.Chip * m.ChipSize + m.ChipExtra : 0;
        var natural = Math.Max(n.Natural * m.Size, chip);
        var minimum = c.Solid ? Math.Max(Math.Min(n.Natural * m.Size, LongestSolid), Math.Min(chip, LongestWord * 1.3)) : Math.Min(n.Minimum * m.Size, LongestWord);
        if (d.MaxLines is 1) minimum = Math.Min(minimum, 60);
        natural = Math.Max(natural, n.HeaderNatural * m.HeaderSize) + pad;
        minimum = Math.Max(minimum, Math.Min(n.HeaderMinimum * m.HeaderSize, LongestWord)) + pad;
        if (d.MaxWidth is { } max) (natural, minimum) = (Math.Min(natural, max), Math.Min(minimum, max));
        if (d.MinWidth is { } min) (natural, minimum) = (Math.Max(natural, min), Math.Max(minimum, min));
        return (minimum, Math.Max(natural, minimum));
    }

    /// <summary>Whether the table looks good in this width: full text size, and nothing squeezed.</summary>
    public bool FitsWell(double available)
    {
        double sum = 0;
        for (var k = 0; k < _needs.Length; k++)
        {
            var (minimum, natural) = Range(k, _base);
            sum += Math.Clamp(Comfortable, minimum, natural);
        }
        return sum <= available + 0.01;
    }

    /// <summary>Widths that fill the available width, with text made smaller if needed.</summary>
    /// <exception cref="PdfFormException">There is no way to make them fit.</exception>
    public (TableMetrics Metrics, double[] Widths) Solve(double available, string paper)
    {
        double needed = 0;
        for (var step = 0; ; step++)
        {
            var scale = 1 - step * 0.04;
            if (step > 0 && _base.Size * scale < SmallestSize) break;
            var m = step == 0 ? _base : _base.Scaled(scale);
            var ranges = Enumerable.Range(0, _needs.Length).Select(k => Range(k, m)).ToArray();
            needed = ranges.Sum(r => r.Minimum);
            if (needed <= available + 0.01) return (m, Distribute(ranges, available));
        }
        var inv = CultureInfo.InvariantCulture;
        throw new PdfFormException(string.Create(inv, $"The {_needs.Length} columns{(_table.Definition.Title is { } t ? $" of '{t}'" : "")} need at least {needed / PaperSize.Mm:0} mm, but there is room for {available / PaperSize.Mm:0} mm on {paper}. ")
                                   + "Use landscape or larger paper, smaller margins or compact density, show fewer columns, or limit wide ones with MaxWidth, Width or Ellipsis.");
    }

    private double[] Distribute((double Minimum, double Natural)[] r, double available)
    {
        var columns = _table.Columns;
        var n = r.Length;
        var upper = new double[n];
        double shares = 0;
        for (var k = 0; k < n; k++)
        {
            upper[k] = r[k].Natural;
            if (columns[k].Definition is { Share: { } s, Width: null }) { shares += s; upper[k] = r[k].Minimum; }
        }
        double[] w;
        if (upper.Sum() <= available) w = upper;
        else
        {
            // squeeze: every column between its minimum and what it would like, the widest giving up the most. Short texts are kept
            // on one line if that leaves enough for the long ones.
            var lower = new double[n];
            for (var k = 0; k < n; k++) lower[k] = upper[k] <= 105 ? upper[k] : r[k].Minimum;
            var enough = 0.0;
            for (var k = 0; k < n; k++) enough += upper[k] <= 105 ? upper[k] : Math.Max(r[k].Minimum, Math.Min(upper[k], Comfortable));
            if (enough > available) for (var k = 0; k < n; k++) lower[k] = r[k].Minimum;
            w = Level(k => (lower[k], upper[k]), k => Math.Pow(Math.Min(upper[k], 700), 0.7), available, n);
        }
        var left = available - w.Sum();
        if (left <= 0.01) return w;

        if (shares > 0)
        {
            var rest = available - Enumerable.Range(0, n).Where(k => !Shared(k)).Sum(k => w[k]);
            var divided = Level(k => Shared(k) ? (r[k].Minimum, columns[k].Definition.MaxWidth ?? 1e6) : (w[k], w[k]), k => columns[k].Definition.Share ?? 0, available, n);
            return rest > 0 ? divided : w;
        }
        // stretch to the full width, in proportion; labels and ticks gain least from more room
        var start = w;
        return Level(k => (start[k], columns[k].Definition.Width ?? Math.Max(start[k], columns[k].Definition.MaxWidth ?? 1e6)),
            k => start[k], available, n);

        bool Shared(int k) => columns[k].Definition is { Share: not null, Width: null };
    }

    /// <summary>Widths level x weight, limited to each column's range, with the level that makes them add up to the total (or the nearest possible).</summary>
    private static double[] Level(Func<int, (double Low, double High)> range, Func<int, double> weight, double total, int n)
    {
        double[] At(double level) => Enumerable.Range(0, n).Select(k =>
        {
            var (low, high) = range(k);
            return Math.Min(Math.Max(level * weight(k), low), Math.Max(low, high));
        }).ToArray();
        double lo = 0, hi = 1;
        while (At(hi).Sum() < total && hi < 1e9) hi *= 2;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (At(mid).Sum() <= total) lo = mid; else hi = mid;
        }
        return At(lo);
    }
}
