using System.Collections;
using System.Linq.Expressions;
using PdfFormFiller.Reports.Internal;

namespace PdfFormFiller.Reports;

/// <summary>
/// Starting point for reports: table PDFs made from lists of objects.
/// <code>
/// byte[] pdf = PdfReport.Create(orders)
///     .Title("Orderrapport", "November 2026")
///     .Column(o => o.Customer)
///     .Column(o => o.Amount, c => c.Format("N2").Sum())
///     .GroupBy(o => o.Department)
///     .Save();
/// </code>
/// An instance of this class (from <see cref="Create()"/>) is a report without a table of its own; add tables with
/// <c>Section</c>. Configuration and saving are not thread-safe on one instance, but any number of reports can be made at the same time.
/// </summary>
public sealed class PdfReport : ReportBuilder<PdfReport>
{
    private PdfReport() : base(null) { }

    /// <summary>A report whose table shows one row per item. Without any <c>Column</c> calls all simple properties are shown.</summary>
    /// <param name="items">A list, an array, a query or any other sequence. It is read once, at the first save.</param>
    public static PdfReport<T> Create<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PdfReport<T>(new ReportTable<T>(typeof(T), items));
    }

    /// <summary>A report for a type that is not known until run time. Choose columns by property name or dotted path.</summary>
    /// <param name="itemType">The type whose properties can be chosen.</param>
    /// <param name="items">Items of that type (or derived from it). Read once, at the first save.</param>
    public static PdfReport<object> Create(Type itemType, IEnumerable items)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        ArgumentNullException.ThrowIfNull(items);
        return new PdfReport<object>(new ReportTable<object>(itemType, items));
    }

    /// <summary>A report that only consists of sections.</summary>
    public static PdfReport Create() => new();
}

/// <summary>A report with a main table showing items of type <typeparamref name="T"/>. Get one from <c>PdfReport.Create</c>.</summary>
public sealed class PdfReport<T> : ReportBuilder<PdfReport<T>>
{
    private readonly ReportTable<T> _table;

    internal PdfReport(ReportTable<T> table) : base(table.Definition) => _table = table;

    /// <inheritdoc cref="ReportTable{T}.Column{TValue}(Expression{Func{T, TValue}}, Action{ReportColumn{T, TValue}})"/>
    public PdfReport<T> Column<TValue>(Expression<Func<T, TValue>> value, Action<ReportColumn<T, TValue>>? configure = null)
    {
        _table.Column(value, configure);
        return this;
    }

    /// <inheritdoc cref="ReportTable{T}.Column(string, Action{ReportColumn{T, object}})"/>
    public PdfReport<T> Column(string property, Action<ReportColumn<T, object?>>? configure = null)
    {
        _table.Column(property, configure);
        return this;
    }

    /// <inheritdoc cref="ReportTable{T}.Exclude(Expression{Func{T, object}}[])"/>
    public PdfReport<T> Exclude(params Expression<Func<T, object?>>[] properties) { _table.Exclude(properties); return this; }

    /// <inheritdoc cref="ReportTable{T}.Exclude(string[])"/>
    public PdfReport<T> Exclude(params string[] properties) { _table.Exclude(properties); return this; }

    /// <inheritdoc cref="ReportTable{T}.GroupBy{TValue}(Expression{Func{T, TValue}}, Action{ReportGroup{TValue}})"/>
    public PdfReport<T> GroupBy<TValue>(Expression<Func<T, TValue>> value, Action<ReportGroup<TValue>>? configure = null)
    {
        _table.GroupBy(value, configure);
        return this;
    }

    /// <inheritdoc cref="ReportTable{T}.GroupBy(string, Action{ReportGroup{object}})"/>
    public PdfReport<T> GroupBy(string property, Action<ReportGroup<object?>>? configure = null)
    {
        _table.GroupBy(property, configure);
        return this;
    }

    /// <inheritdoc cref="ReportTable{T}.EmptyText(string)"/>
    public PdfReport<T> EmptyText(string text) { _table.EmptyText(text); return this; }
}

/// <summary>Everything that concerns the whole document. Used through <see cref="PdfReport{T}"/> and <see cref="PdfReport"/>.</summary>
/// <typeparam name="TSelf">The concrete report type, so that calls can be chained.</typeparam>
public abstract class ReportBuilder<TSelf> where TSelf : ReportBuilder<TSelf>
{
    private readonly ReportDefinition _def = new();

    internal ReportBuilder(TableDefinition? table)
    {
        if (table is not null) _def.Tables.Add(table);
    }

    private TSelf Self => (TSelf)this;

    /// <summary>Heading on the first page, also the document's title. The subtitle is typically a period or a selection.</summary>
    public TSelf Title(string title, string? subtitle = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        (_def.Title, _def.Subtitle) = (title, subtitle);
        return Self;
    }

    /// <summary>Logo, company name, colours, theme and fonts.</summary>
    public TSelf Branding(Action<ReportBranding> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_def.Branding);
        return Self;
    }

    /// <summary>A fact shown below the title, for example a filter or a period. Values are formatted as in the table.</summary>
    public TSelf Summary(string label, object? value)
    {
        ArgumentNullException.ThrowIfNull(label);
        _def.Facts.Add((label, value));
        return Self;
    }

    /// <summary>A key figure, shown as a card below the title.</summary>
    /// <param name="label">What the figure is.</param>
    /// <param name="value">The figure; formatted as in the table, or pass finished text.</param>
    /// <param name="note">Small text below the figure, for example a comparison.</param>
    public TSelf KeyFigure(string label, object? value, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        _def.KeyFigures.Add((label, value, note));
        return Self;
    }

    /// <summary>Paper and orientation. Default: A4, landscape only when the columns need it.</summary>
    public TSelf Paper(PaperSize size, PageOrientation orientation = PageOrientation.Auto)
    {
        ArgumentNullException.ThrowIfNull(size);
        (_def.Paper, _def.Orientation) = (size, orientation);
        return Self;
    }

    /// <summary>Landscape pages.</summary>
    public TSelf Landscape() { _def.Orientation = PageOrientation.Landscape; return Self; }

    /// <summary>Portrait pages.</summary>
    public TSelf Portrait() { _def.Orientation = PageOrientation.Portrait; return Self; }

    /// <summary>The same margin on all four sides, in millimetres. Default 15.</summary>
    public TSelf Margins(double all) => Margins(all, all, all, all);

    /// <summary>Margins in millimetres.</summary>
    public TSelf Margins(double top, double right, double bottom, double left)
    {
        foreach (var m in new[] { top, right, bottom, left })
            if (!(m >= 0 && m <= 1000)) throw new PdfFormException("A margin must be between 0 and 1000 mm.");
        _def.Margins = (top * PaperSize.Mm, right * PaperSize.Mm, bottom * PaperSize.Mm, left * PaperSize.Mm);
        return Self;
    }

    /// <summary>How tightly rows are set.</summary>
    public TSelf Density(ReportDensity density) { _def.Density = density; return Self; }

    /// <summary>Text size of the rows in points, 5 to 24. Default depends on the density. Tables that do not fit are set smaller, at least 6 points.</summary>
    public TSelf FontSize(double points)
    {
        if (!(points >= 5 && points <= 24)) throw new PdfFormException("The font size must be between 5 and 24 points.");
        _def.FontSize = points;
        return Self;
    }

    /// <summary>Shade every other row (default) or not.</summary>
    public TSelf Zebra(bool on = true) { _def.Zebra = on; return Self; }

    /// <summary>Text at the bottom left of every page.</summary>
    public TSelf Footer(string text) { _def.Footer = text ?? throw new ArgumentNullException(nameof(text)); return Self; }

    /// <summary>
    /// Time shown in the footer and stored in the document. With it, the same input gives identical files; without it nothing
    /// is shown and the document gets the time of saving.
    /// </summary>
    public TSelf Timestamp(DateTimeOffset time) { _def.Timestamp = time; return Self; }

    /// <summary>Author in the document properties. Default: the company name.</summary>
    public TSelf Author(string author) { _def.Author = author; return Self; }

    /// <summary>How numbers and dates are written, and the language of the document. Default sv-SE.</summary>
    /// <param name="name">For example "en-GB".</param>
    public TSelf Culture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _def.Culture = Cultures.Get(name);
        _def.Language = name;
        return Self;
    }

    /// <inheritdoc cref="Culture(string)"/>
    public TSelf Culture(System.Globalization.CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        (_def.Culture, _def.Language) = (culture, culture.Name);
        return Self;
    }

    /// <summary>Texts the report writes by itself. Swedish by default.</summary>
    public TSelf Texts(ReportTexts texts) { _def.Texts = texts ?? throw new ArgumentNullException(nameof(texts)); return Self; }

    /// <summary>
    /// Whether the document is tagged for accessibility (default). Turn off for very large reports: files become about half the size
    /// and Firefox shows long tables faster.
    /// </summary>
    public TSelf Tagged(bool on = true) { _def.Tagged = on; return Self; }

    /// <summary>
    /// Attaches the data as CSV files (one per table), ready for Excel: UTF-8 with BOM, the culture's list separator, unformatted
    /// numbers. Text that Excel would take for a formula is preceded by an apostrophe.
    /// </summary>
    /// <param name="fileName">Name of the file; by default made from the title.</param>
    public TSelf AttachCsv(string? fileName = null)
    {
        (_def.AttachCsv, _def.CsvFileName) = (true, fileName);
        return Self;
    }

    /// <summary>Adds another table with a heading of its own after what has been added so far.</summary>
    /// <param name="title">Heading, also shown as a bookmark.</param>
    /// <param name="items">Read once, at the first save.</param>
    /// <param name="configure">Columns and grouping; leave out to show all simple properties.</param>
    public TSelf Section<TItem>(string title, IEnumerable<TItem> items, Action<ReportTable<TItem>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return AddSection(title, new ReportTable<TItem>(typeof(TItem), items), configure);
    }

    /// <summary>Adds another table for a type that is not known until run time.</summary>
    /// <inheritdoc cref="Section{TItem}(string, IEnumerable{TItem}, Action{ReportTable{TItem}})"/>
    public TSelf Section(string title, Type itemType, IEnumerable items, Action<ReportTable<object>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        ArgumentNullException.ThrowIfNull(items);
        return AddSection(title, new ReportTable<object>(itemType, items), configure);
    }

    private TSelf AddSection<TItem>(string title, ReportTable<TItem> table, Action<ReportTable<TItem>>? configure)
    {
        ArgumentNullException.ThrowIfNull(title);
        table.Definition.Title = title;
        configure?.Invoke(table);
        _def.Tables.Add(table.Definition);
        return Self;
    }

    /// <summary>The finished PDF.</summary>
    /// <exception cref="PdfFormException">The report cannot be made; the message tells why and what to do.</exception>
    public byte[] Save() => ReportRenderer.Render(_def).Save();

    /// <summary>Writes the finished PDF to a stream, which is left open and need not be seekable.</summary>
    /// <inheritdoc cref="Save()"/>
    public void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ReportRenderer.Render(_def).Save(destination);
    }

    /// <summary>Writes the finished PDF to a file, replacing it if it exists. Nothing is written when the report cannot be made.</summary>
    /// <inheritdoc cref="Save()"/>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var pdf = ReportRenderer.Render(_def);
        using var file = File.Create(path);
        pdf.Save(file);
    }
}
