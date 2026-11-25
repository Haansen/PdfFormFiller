using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using PdfFormFiller.Core;

namespace PdfFormFiller.Reports.Internal;

internal enum HAlign : byte { Left, Center, Right }

internal enum AggregateKind : byte { None, Sum, Average, Count, Min, Max, Custom }

/// <summary>The whole report as configured by the caller.</summary>
internal sealed class ReportDefinition
{
    public string? Title, Subtitle, Footer, Author, CsvFileName;
    public ReportBranding Branding { get; } = new();
    public List<(string Label, object? Value)> Facts { get; } = new();
    public List<(string Label, object? Value, string? Note)> KeyFigures { get; } = new();
    public List<TableDefinition> Tables { get; } = new();
    public PaperSize Paper = PaperSize.A4;
    public PageOrientation Orientation;
    public (double Top, double Right, double Bottom, double Left) Margins = (15 * PaperSize.Mm, 15 * PaperSize.Mm, 15 * PaperSize.Mm, 15 * PaperSize.Mm);
    public ReportDensity Density = ReportDensity.Normal;
    public double? FontSize;
    public bool Zebra = true;
    public DateTimeOffset? Timestamp;
    public CultureInfo? Culture;
    public string Language = "sv-SE";
    public ReportTexts Texts = new();
    public bool Tagged = true;
    public bool AttachCsv;
}

internal sealed class TableDefinition
{
    private readonly IEnumerable _items;
    private List<object?>? _rows;

    public TableDefinition(Type itemType, IEnumerable items) => (ItemType, _items) = (itemType, items);

    public Type ItemType { get; }
    public string? Title, Description, EmptyText;
    public bool PageBreak;
    public List<ColumnDefinition> Columns { get; } = new();
    public HashSet<string> Excluded { get; } = new();
    public List<GroupDefinition> Groups { get; } = new();

    /// <summary>The items. The sequence is read the first time and kept, so it is only ever enumerated once.</summary>
    public List<object?> Rows
    {
        get
        {
            if (_rows is not null) return _rows;
            var rows = new List<object?>(_items is ICollection c ? c.Count : 16);
            try
            {
                foreach (var item in _items)
                {
                    if (item is not null && !ItemType.IsInstanceOfType(item))
                        throw new PdfFormException($"Row {rows.Count + 1} is a {item.GetType().Name}, not a {ItemType.Name}.");
                    rows.Add(item);
                }
            }
            catch (Exception e) when (e is not PdfFormException)
            {
                throw new PdfFormException($"The items could not be read (after {rows.Count} rows): {e.Message}", e);
            }
            return _rows = rows;
        }
    }
}

internal sealed class GroupDefinition
{
    public GroupDefinition(ColumnDefinition column) => Column = column;

    public ColumnDefinition Column { get; }
    public bool PageBreak, NoTotals;
}

internal sealed class ColumnDefinition
{
    private ColumnDefinition(string name, Type type, Func<object, object?> get)
    {
        Name = name;
        Type = type;
        Core = Nullable.GetUnderlyingType(type) ?? type;
        Get = get;
    }

    /// <summary>For messages: the property path, the expression, or the heading once there is one.</summary>
    public string Name { get; }
    /// <summary>Property path; null for computed columns.</summary>
    public string? Path { get; private init; }
    public Type Type { get; }
    /// <summary><see cref="Type"/> without Nullable.</summary>
    public Type Core { get; }
    /// <summary>Reads the value from an item (not null).</summary>
    public Func<object, object?> Get { get; }

    public string? Header, Format, NullText, Yes, No;
    public Func<object?, string?>? Formatter { get; private set; }
    public bool BoolIcon = true, LinkValue;
    public Func<object, string?>? Link;
    public Func<object?, string?>? ChipColor;
    private Dictionary<object, PdfColor>? _chips;
    public HAlign? Align;
    public double? Width, MinWidth, MaxWidth, Share;
    public int? MaxLines;
    public AggregateKind Aggregate { get; private set; }
    public Func<IReadOnlyList<object?>, object?>? CustomTotal;

    public string Display => Header ?? Name;
    public bool HasChips => _chips is not null || ChipColor is not null;

    public static ColumnDefinition From(MemberPath path) => new(path.Name, path.Type, path.Getter)
    {
        Path = path.Name, Header = path.Header, Format = path.Format, NullText = path.NullText,
    };

    public static ColumnDefinition From<T, TValue>(Expression<Func<T, TValue>> value, Type itemType)
    {
        if (MemberPath.TryFrom(value) is { } path) return From(path);
        if (value.Body == value.Parameters[0] && MemberPath.IsSimple(itemType))
            return new ColumnDefinition("value", itemType, o => o) { Path = "" };
        var f = value.Compile();
        return new ColumnDefinition(value.Body.ToString(), typeof(TValue), o => f((T)o));
    }

    /// <summary>The only column of a table of plain values.</summary>
    public static ColumnDefinition Itself(Type type, string header) => new("value", type, o => o) { Path = "", Header = header };

    public void SetFormatter<TValue>(Func<TValue, string?> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var acceptsNull = default(TValue) is null;
        Formatter = v => v is TValue t ? text(t) : v is null && acceptsNull ? text(default!) : throw new InvalidCastException();
        FormatterAccepts = v => v is TValue;
        FormatsNull = acceptsNull;
    }

    /// <summary>Whether the formatter takes a value of this type (totals may have another type than the column).</summary>
    public Func<object?, bool> FormatterAccepts { get; private set; } = _ => false;
    public bool FormatsNull { get; private set; }

    public void AddChip(object? value, PdfColor color)
    {
        if (value is null) throw new PdfFormException($"Column '{Display}': a label needs a value that is not null.");
        (_chips ??= new())[value] = color;
    }

    public PdfColor? ChipFor(object value)
    {
        if (_chips is not null && _chips.TryGetValue(value, out var c)) return c;
        return ChipColor?.Invoke(value) is { } text ? Colors.Parse(text) : null;
    }

    public void SetAggregate(AggregateKind kind)
    {
        var known = Core != typeof(object);
        if (kind is AggregateKind.Sum or AggregateKind.Average && known && !Aggregates.CanAdd(Core))
            throw new PdfFormException($"Column '{Display}' holds {Core.Name}, which has no {kind.ToString().ToLowerInvariant()}. Only numbers and TimeSpan have.");
        if (kind is AggregateKind.Min or AggregateKind.Max && known && !typeof(IComparable).IsAssignableFrom(Core))
            throw new PdfFormException($"Column '{Display}' holds {Core.Name}, which cannot be compared.");
        Aggregate = kind;
    }
}
