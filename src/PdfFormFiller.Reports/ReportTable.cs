using System.Collections;
using System.Linq.Expressions;
using PdfFormFiller.Reports.Internal;

namespace PdfFormFiller.Reports;

/// <summary>A table in a report: its columns and grouping.</summary>
public sealed class ReportTable<T>
{
    internal ReportTable(Type itemType, IEnumerable items) => Definition = new TableDefinition(itemType, items);

    internal TableDefinition Definition { get; }

    /// <summary>
    /// Adds a column. A property (<c>o => o.Customer.Name</c>) gets its heading from [Display], [DisplayName] or its name, and a
    /// null on the way gives an empty cell. Other expressions (<c>o => o.Price * o.Quantity</c>) need a <see cref="ReportColumn{T, TValue}.Header"/>.
    /// </summary>
    /// <exception cref="PdfFormException">A computed column has no heading.</exception>
    public ReportTable<T> Column<TValue>(Expression<Func<T, TValue>> value, Action<ReportColumn<T, TValue>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var def = ColumnDefinition.From(value, Definition.ItemType);
        configure?.Invoke(new ReportColumn<T, TValue>(def));
        if (def.Path is null && def.Header is null)
            throw new PdfFormException($"The column '{def.Name}' is computed and needs a heading: .Column({value}, c => c.Header(\"...\")).");
        Definition.Columns.Add(def);
        return this;
    }

    /// <summary>Adds a column by property name or dotted path, for example "Customer.Name". Upper and lower case are the same unless that is ambiguous.</summary>
    /// <exception cref="PdfFormException">There is no such property; the message lists those that exist.</exception>
    public ReportTable<T> Column(string property, Action<ReportColumn<T, object?>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        var def = ColumnDefinition.From(MemberPath.Resolve(Definition.ItemType, property));
        configure?.Invoke(new ReportColumn<T, object?>(def));
        Definition.Columns.Add(def);
        return this;
    }

    /// <summary>Leaves out properties when columns are chosen automatically, that is, when there are no <c>Column</c> calls.</summary>
    public ReportTable<T> Exclude(params Expression<Func<T, object?>>[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        foreach (var p in properties)
            Definition.Excluded.Add(MemberPath.TryFrom(p)?.Name ?? throw new PdfFormException($"Exclude needs a property, not {p}."));
        return this;
    }

    /// <inheritdoc cref="Exclude(Expression{Func{T, object}}[])"/>
    public ReportTable<T> Exclude(params string[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        foreach (var p in properties) Definition.Excluded.Add(MemberPath.Resolve(Definition.ItemType, p).Name);
        return this;
    }

    /// <summary>
    /// Divides the rows into groups with a heading, and totals for columns that have them. Call again for groups within groups.
    /// Groups come in the order they first occur; rows keep their order within a group.
    /// </summary>
    public ReportTable<T> GroupBy<TValue>(Expression<Func<T, TValue>> value, Action<ReportGroup<TValue>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var def = ColumnDefinition.From(value, Definition.ItemType);
        return AddGroup(def, configure);
    }

    /// <inheritdoc cref="GroupBy{TValue}(Expression{Func{T, TValue}}, Action{ReportGroup{TValue}})"/>
    /// <param name="property">Property name or dotted path.</param>
    /// <param name="configure">Heading, format and page breaks.</param>
    public ReportTable<T> GroupBy(string property, Action<ReportGroup<object?>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        return AddGroup(ColumnDefinition.From(MemberPath.Resolve(Definition.ItemType, property)), configure);
    }

    private ReportTable<T> AddGroup<TValue>(ColumnDefinition def, Action<ReportGroup<TValue>>? configure)
    {
        var group = new GroupDefinition(def);
        configure?.Invoke(new ReportGroup<TValue>(group));
        Definition.Groups.Add(group);
        return this;
    }

    /// <summary>A paragraph between the heading and the table.</summary>
    public ReportTable<T> Description(string text) { Definition.Description = text; return this; }

    /// <summary>Starts the section on a new page.</summary>
    public ReportTable<T> PageBreak() { Definition.PageBreak = true; return this; }

    /// <summary>Shown when there are no rows. Default <see cref="ReportTexts.Empty"/>.</summary>
    public ReportTable<T> EmptyText(string text) { Definition.EmptyText = text; return this; }
}

/// <summary>Options of a grouping.</summary>
public sealed class ReportGroup<TValue>
{
    private readonly GroupDefinition _def;

    internal ReportGroup(GroupDefinition def) => _def = def;

    /// <summary>What is grouped by, written before the value in the group heading. An empty text leaves it out.</summary>
    public ReportGroup<TValue> Header(string text) { _def.Column.Header = text; return this; }

    /// <summary>Format string such as "yyyy-MM" or "{0} kr".</summary>
    public ReportGroup<TValue> Format(string format) { _def.Column.Format = format; return this; }

    /// <summary>Writes the value as any text.</summary>
    public ReportGroup<TValue> Value(Func<TValue, string?> text) { _def.Column.SetFormatter(text); return this; }

    /// <summary>Starts every group on a new page.</summary>
    public ReportGroup<TValue> PageBreak() { _def.PageBreak = true; return this; }

    /// <summary>Leaves out the totals of these groups.</summary>
    public ReportGroup<TValue> NoTotals() { _def.NoTotals = true; return this; }
}
