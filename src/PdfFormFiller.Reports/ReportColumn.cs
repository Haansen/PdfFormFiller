using PdfFormFiller.Reports.Internal;

namespace PdfFormFiller.Reports;

/// <summary>Options of a column.</summary>
/// <typeparam name="T">Type of the items.</typeparam>
/// <typeparam name="TValue">Type of the column's value.</typeparam>
public sealed class ReportColumn<T, TValue>
{
    private readonly ColumnDefinition _def;

    internal ReportColumn(ColumnDefinition def) => _def = def;

    /// <summary>Heading of the column.</summary>
    public ReportColumn<T, TValue> Header(string text) { _def.Header = text ?? throw new ArgumentNullException(nameof(text)); return this; }

    // ------------------------------------------------------------------ text

    /// <summary>Format string: "N2", "yyyy-MM-dd", or with text around it: "{0:N2} kr".</summary>
    public ReportColumn<T, TValue> Format(string format) { _def.Format = format; return this; }

    /// <summary>Writes the value as any text. Totals are written with the same function when they have the column's type.</summary>
    public ReportColumn<T, TValue> Value(Func<TValue, string?> text) { _def.SetFormatter(text); return this; }

    /// <summary>Text for null. Default: an empty cell.</summary>
    public ReportColumn<T, TValue> NullText(string text) { _def.NullText = text; return this; }

    /// <summary>Texts for true and false. By default a tick or a cross is drawn, and the texts are what is copied and read aloud.</summary>
    /// <param name="yes">Text for true; default <see cref="ReportTexts.Yes"/>.</param>
    /// <param name="no">Text for false; default <see cref="ReportTexts.No"/>.</param>
    /// <param name="icon">False to write the texts instead of drawing.</param>
    public ReportColumn<T, TValue> Bool(string? yes = null, string? no = null, bool icon = true)
    {
        (_def.Yes, _def.No, _def.BoolIcon) = (yes, no, icon);
        return this;
    }

    /// <summary>Shows a value as a coloured label. Call once per value that shall have one, typically enum values.</summary>
    /// <param name="value">The value.</param>
    /// <param name="color">"#RRGGBB", for example one of <see cref="ReportColors"/>. Shades with readable text are made from it.</param>
    public ReportColumn<T, TValue> Chip(TValue value, string color)
    {
        _def.AddChip(value, Colors.Parse(color));
        return this;
    }

    /// <summary>Shows values as coloured labels.</summary>
    /// <param name="color">Gives the colour ("#RRGGBB") of a value, or null for plain text.</param>
    public ReportColumn<T, TValue> Chip(Func<TValue, string?> color)
    {
        ArgumentNullException.ThrowIfNull(color);
        _def.ChipColor = v => v is TValue t ? color(t) : null;
        return this;
    }

    /// <summary>Makes the value, a web address or an e-mail address, a link. Anything else is shown as plain text.</summary>
    public ReportColumn<T, TValue> Link() { _def.LinkValue = true; return this; }

    /// <summary>Makes the text a link.</summary>
    /// <param name="address">Gives the address (http, https, mailto or an e-mail address) for an item, or null for none. Anything else is shown as plain text.</param>
    public ReportColumn<T, TValue> Link(Func<T, string?> address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _def.Link = item => address((T)item);
        return this;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Left aligned.</summary>
    public ReportColumn<T, TValue> AlignLeft() { _def.Align = HAlign.Left; return this; }

    /// <summary>Centred.</summary>
    public ReportColumn<T, TValue> AlignCenter() { _def.Align = HAlign.Center; return this; }

    /// <summary>Right aligned; the default for numbers.</summary>
    public ReportColumn<T, TValue> AlignRight() { _def.Align = HAlign.Right; return this; }

    /// <summary>Fixed width in millimetres.</summary>
    public ReportColumn<T, TValue> Width(double millimetres) { _def.Width = Positive(millimetres) * PaperSize.Mm; return this; }

    /// <summary>Never narrower than this many millimetres.</summary>
    public ReportColumn<T, TValue> MinWidth(double millimetres) { _def.MinWidth = Positive(millimetres) * PaperSize.Mm; return this; }

    /// <summary>Never wider than this many millimetres.</summary>
    public ReportColumn<T, TValue> MaxWidth(double millimetres) { _def.MaxWidth = Positive(millimetres) * PaperSize.Mm; return this; }

    /// <summary>
    /// Share of the width left after the other columns. Two columns with 1 and 2 divide it in thirds. Columns without it get the width
    /// their content needs.
    /// </summary>
    public ReportColumn<T, TValue> RelativeWidth(double share) { _def.Share = Positive(share); return this; }

    /// <summary>Cuts long text with an ellipsis instead of wrapping it. The full text (up to 2,000 characters) is still what is read aloud, and copied in readers that support it.</summary>
    /// <param name="maxLines">Lines to show before cutting.</param>
    public ReportColumn<T, TValue> Ellipsis(int maxLines = 1)
    {
        if (maxLines < 1) throw new PdfFormException("Ellipsis needs at least one line.");
        _def.MaxLines = maxLines;
        return this;
    }

    private static double Positive(double v) => v > 0 && v < 10000 ? v : throw new PdfFormException("A column width must be a positive number.");

    // ------------------------------------------------------------------ totals

    /// <summary>Total of the column, per group and for the whole table. Numbers and TimeSpan.</summary>
    public ReportColumn<T, TValue> Sum() => Aggregate(AggregateKind.Sum);

    /// <summary>Average of the values that are not null. Numbers and TimeSpan.</summary>
    public ReportColumn<T, TValue> Average() => Aggregate(AggregateKind.Average);

    /// <summary>Number of values that are not null.</summary>
    public ReportColumn<T, TValue> Count() => Aggregate(AggregateKind.Count);

    /// <summary>Smallest value.</summary>
    public ReportColumn<T, TValue> Min() => Aggregate(AggregateKind.Min);

    /// <summary>Largest value.</summary>
    public ReportColumn<T, TValue> Max() => Aggregate(AggregateKind.Max);

    /// <summary>Total of your own.</summary>
    /// <param name="calculate">
    /// Gets the values of the group, or of the whole table, that are not null. The result is written like the column's values; a
    /// string is written as it is.
    /// </param>
    public ReportColumn<T, TValue> Total(Func<IReadOnlyList<TValue>, object?> calculate)
    {
        ArgumentNullException.ThrowIfNull(calculate);
        _def.CustomTotal = values => calculate(values.Select(v => (TValue)v!).ToList());
        return Aggregate(AggregateKind.Custom);
    }

    private ReportColumn<T, TValue> Aggregate(AggregateKind kind)
    {
        _def.SetAggregate(kind);
        return this;
    }
}
