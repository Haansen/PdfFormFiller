using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace PdfFormFiller.Reports.Internal;

internal static class Cultures
{
    /// <summary>A culture by name. Swedish also works where .NET runs without culture data (InvariantGlobalization).</summary>
    public static CultureInfo Get(string name)
    {
        CultureInfo? c = null;
        try { c = CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException e) when (name != "sv-SE")
        {
            throw new PdfFormException($"There is no culture named '{name}' on this computer.", e);
        }
        catch (CultureNotFoundException) { }
        if (name != "sv-SE" || c is not null && c.NumberFormat.NumberDecimalSeparator == ",") return c!;
        var s = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        var n = s.NumberFormat;
        n.NumberDecimalSeparator = n.CurrencyDecimalSeparator = n.PercentDecimalSeparator = ",";
        n.NumberGroupSeparator = n.CurrencyGroupSeparator = n.PercentGroupSeparator = "\u00A0";
        n.CurrencySymbol = "kr";
        n.CurrencyPositivePattern = n.PercentPositivePattern = 3;
        n.CurrencyNegativePattern = 8;
        n.PercentPositivePattern = 0;
        n.PercentNegativePattern = 0;
        n.NegativeSign = "\u2212";
        var d = s.DateTimeFormat;
        d.ShortDatePattern = "yyyy-MM-dd";
        d.LongDatePattern = "'den 'd MMMM yyyy";
        d.ShortTimePattern = "HH:mm";
        d.LongTimePattern = "HH:mm:ss";
        d.DateSeparator = "-";
        s.TextInfo.ListSeparator = ";";
        return CultureInfo.ReadOnly(s);
    }
}

/// <summary>Turns values into text.</summary>
internal sealed class ValueFormatter
{
    private const int MaxElements = 500;
    private const int MaxDepth = 8;

    private readonly CultureInfo _culture;
    private readonly ReportTexts _texts;
    private static readonly ConcurrentDictionary<Type, EnumLabels> Enums = new();

    public ValueFormatter(CultureInfo culture, ReportTexts texts) => (_culture, _texts) = (culture, texts);

    /// <summary>Text of a column's value (not null).</summary>
    public string Cell(object value, ColumnDefinition column)
    {
        if (column.Formatter is { } f) return f(value) ?? "";
        if (value is bool b) return b ? column.Yes ?? _texts.Yes : column.No ?? _texts.No;
        return Text(value, column.Format);
    }

    public string Text(object? value, string? format = null) => Text(value, format, 0);

    private string Text(object? value, string? format, int depth)
    {
        if (value is null) return "";
        if (format is not null)
        {
            if (format.Contains("{0")) return string.Format(_culture, format, value is Enum ev ? EnumText(ev) : value);
            if (value is IFormattable and not Enum) return ((IFormattable)value).ToString(format, _culture);
        }
        switch (value)
        {
            case string s: return s;
            case bool b: return b ? _texts.Yes : _texts.No;
            case Enum e: return EnumText(e);
            case decimal d: return d.ToString("N2", _culture);
            case double d: return d.ToString(Math.Abs(d) is >= 1e15 and < double.PositiveInfinity ? "G6" : "N2", _culture);
            case float d: return d.ToString(Math.Abs(d) is >= 1e15f and < float.PositiveInfinity ? "G6" : "N2", _culture);
            case Half d: return d.ToString("N2", _culture);
            case DateTime d: return d.ToString(d.TimeOfDay == TimeSpan.Zero ? "d" : "g", _culture);
            case DateTimeOffset d: return d.ToString(d.TimeOfDay == TimeSpan.Zero ? "d" : "g", _culture);
            case DateOnly d: return d.ToString("d", _culture);
            case TimeOnly t: return t.ToString("t", _culture);
            case TimeSpan t: return Duration(t);
            case IFormattable f: return f.ToString(null, _culture) ?? "";
            case IEnumerable list:
                if (depth >= MaxDepth) return "\u2026";
                var sb = new StringBuilder();
                var n = 0;
                foreach (var x in list)
                {
                    if (n++ > 0) sb.Append(", ");
                    if (n > MaxElements || sb.Length > 100_000) { sb.Append('\u2026'); break; }
                    sb.Append(Text(x, format is not null && !format.Contains("{0") ? format : null, depth + 1));
                }
                return sb.ToString();
            default: return value.ToString() ?? "";
        }
    }

    /// <summary>Hours and minutes, seconds when there are any: "26:30", "0:00:05".</summary>
    private string Duration(TimeSpan t)
    {
        var ticks = Math.Abs((decimal)t.Ticks);
        var seconds = decimal.Truncate(ticks / TimeSpan.TicksPerSecond);
        var text = string.Create(CultureInfo.InvariantCulture, $"{decimal.Truncate(seconds / 3600)}:{decimal.Truncate(seconds / 60) % 60:00}");
        if (seconds % 60 != 0) text += string.Create(CultureInfo.InvariantCulture, $":{seconds % 60:00}");
        return t < TimeSpan.Zero ? _culture.NumberFormat.NegativeSign + text : text;
    }

    // ------------------------------------------------------------------ enum

    private string EnumText(Enum value) => Enums.GetOrAdd(value.GetType(), t => new EnumLabels(t)).Text(value);

    private sealed class EnumLabels
    {
        private readonly Dictionary<Enum, FieldInfo> _fields = new();
        private readonly bool _flags;
        private readonly Type _type;

        public EnumLabels(Type type)
        {
            _type = type;
            _flags = type.IsDefined(typeof(FlagsAttribute), false);
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.MetadataToken))
                _fields.TryAdd((Enum)f.GetValue(null)!, f);
        }

        public string Text(Enum value)
        {
            if (_fields.TryGetValue(value, out var field)) return Label(field);
            if (_flags)
            {
                var bits = Bits(value);
                var labels = new List<string>();
                // larger combinations first, so that a named combination is used instead of its parts
                foreach (var (v, f) in _fields.Select(kv => (Bits(kv.Key), kv.Value)).Where(x => x.Item1 != 0).OrderByDescending(x => BitOperations.PopCount(x.Item1)))
                {
                    if ((bits & v) != v) continue;
                    bits &= ~v;
                    labels.Add(Label(f));
                }
                if (bits == 0 && labels.Count > 0) return string.Join(", ", _fields.Values.Select(Label).Where(labels.Contains));
            }
            return value.ToString("D"); // not a member of the enum
        }

        private ulong Bits(Enum value) => unchecked(Type.GetTypeCode(_type) == TypeCode.UInt64 ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : (ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));

        // not cached: resources may depend on the current UI culture
        private static string Label(FieldInfo f)
            => f.GetCustomAttribute<DisplayAttribute>()?.GetName() is { Length: > 0 } d ? d
                : f.GetCustomAttribute<DescriptionAttribute>()?.Description is { Length: > 0 } t ? t
                : f.GetCustomAttribute<EnumMemberAttribute>()?.Value is { Length: > 0 } m ? m
                : f.Name;
    }

    // ------------------------------------------------------------------ CSV

    private const string FormulaStart = "=+-@\t\r\n\uFF1D\uFF0B\uFF0D\uFF20\u2212|%";

    /// <summary>Raw value for a CSV file: numbers without grouping, ISO dates. <paramref name="text"/> is what the report shows.</summary>
    public string Csv(object? value, string text)
    {
        switch (value)
        {
            case null: return text.Length == 0 ? "" : Guard(text);
            case sbyte or byte or short or ushort or int or uint or long or ulong or nint or nuint or Int128 or UInt128 or BigInteger:
                return ((IFormattable)value).ToString("D", CultureInfo.InvariantCulture);
            case decimal or double or float or Half:
                var s = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
                return Quote(s.Replace(".", _culture.NumberFormat.NumberDecimalSeparator));
            case DateTime d: return d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case DateTimeOffset d: return d.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture);
            case DateOnly d: return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly t: return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            case TimeSpan t: // total hours, which spreadsheets read as a duration
                var abs = Math.Abs((decimal)t.Ticks) / TimeSpan.TicksPerSecond;
                return string.Create(CultureInfo.InvariantCulture, $"{(t < TimeSpan.Zero ? "-" : "")}{decimal.Truncate(abs / 3600):00}:{decimal.Truncate(abs / 60) % 60:00}:{decimal.Truncate(abs) % 60:00}");
            case string str: return Guard(str);
            default: return Guard(text);
        }
    }

    public string ListSeparator => _culture.TextInfo.ListSeparator;

    public string CsvHeader(string text) => Guard(text);

    private string Guard(string s)
    {
        if (s.Contains('\0')) s = s.Replace("\0", "");
        return Quote(s.TrimStart(' ') is { Length: > 0 } t && FormulaStart.Contains(t[0]) ? "'" + s : s);
    }

    private string Quote(string s)
        => s.IndexOfAny(QuoteChars) >= 0 || s.Contains(_culture.TextInfo.ListSeparator) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static readonly char[] QuoteChars = { '"', '\r', '\n', '\t' };
}

/// <summary>Totals.</summary>
internal static class Aggregates
{
    private static bool IsExact(Type t) => t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint)
                                           || t == typeof(long) || t == typeof(ulong) || t == typeof(decimal) || t == typeof(nint) || t == typeof(nuint);

    private static bool IsFloat(Type t) => t == typeof(double) || t == typeof(float) || t == typeof(Half);

    private static bool IsBig(Type t) => t == typeof(BigInteger) || t == typeof(Int128) || t == typeof(UInt128);

    public static bool CanAdd(Type t) => IsExact(t) || IsFloat(t) || IsBig(t) || t == typeof(TimeSpan);

    public static bool IsNumber(Type t) => IsExact(t) || IsFloat(t) || IsBig(t);

    /// <summary>The total of values that are not null; null when there are none (a sum of nothing is 0).</summary>
    public static object? Calculate(ColumnDefinition c, IReadOnlyList<object?> values)
    {
        switch (c.Aggregate)
        {
            case AggregateKind.Count: return values.Count;
            case AggregateKind.Custom: return c.CustomTotal!(values);
            case AggregateKind.Min or AggregateKind.Max:
                object? best = null;
                foreach (var v in values)
                {
                    if (best is null) { best = v; continue; }
                    var order = v is string s && best is string b ? string.Compare(s, b, StringComparison.CurrentCulture) : Comparer<object>.Default.Compare(v!, best);
                    if (c.Aggregate == AggregateKind.Min ? order < 0 : order > 0) best = v;
                }
                return best;
        }
        var type = values.Count > 0 ? values[0]!.GetType() : c.Core;
        if (values.Count == 0 && (c.Aggregate == AggregateKind.Average || !CanAdd(type))) return null;
        var average = c.Aggregate == AggregateKind.Average;
        if (type == typeof(TimeSpan))
        {
            long ticks = 0;
            foreach (var v in values) ticks = checked(ticks + ((TimeSpan)v!).Ticks);
            return TimeSpan.FromTicks(average ? ticks / values.Count : ticks);
        }
        if (IsExact(type))
        {
            decimal sum = 0;
            foreach (var v in values) sum += v is nint n ? n : v is nuint u ? (decimal)(ulong)u : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
            return average ? sum / values.Count : sum;
        }
        if (IsFloat(type))
        {
            double sum = 0;
            foreach (var v in values) sum += v is Half h ? (double)h : Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return average ? sum / values.Count : sum;
        }
        if (IsBig(type))
        {
            var sum = BigInteger.Zero;
            foreach (var v in values) sum += v switch { Int128 i => i, UInt128 u => u, BigInteger b => b, _ => throw new InvalidCastException() };
            return average ? (object)((double)sum / values.Count) : sum;
        }
        throw new InvalidOperationException($"{type.Name} has no {c.Aggregate.ToString().ToLowerInvariant()}");
    }

    /// <summary>A total as text, written like the column's values where that is possible.</summary>
    public static string Text(ColumnDefinition c, object? total, ValueFormatter formatter, ReportTexts texts)
    {
        if (total is null) return "";
        if (c.Aggregate == AggregateKind.Count) return texts.Count + " " + formatter.Text(total, "N0");
        if (total is string s) return s;
        string text;
        if (c.Formatter is not null && (c.FormatterAccepts(total) ? total : Back(total, c)) is { } same) text = c.Formatter(same) ?? "";
        else
        {
            // a sum of whole numbers is kept as decimal, and written as the whole number it is
            var value = total is decimal d && c.Core != typeof(decimal) && IsExact(c.Core) && c.Aggregate != AggregateKind.Average ? decimal.Truncate(d) : total;
            try
            {
                text = c.Formatter is null && c.Format is not null ? formatter.Text(value, c.Format)
                    : value is decimal w && c.Aggregate == AggregateKind.Sum && c.Core != typeof(decimal) && IsExact(c.Core) ? formatter.Text(new BigInteger(w))
                    : formatter.Text(value);
            }
            catch (FormatException) when (value is decimal) // "D" and "X" are for whole numbers only
            {
                text = c.Aggregate == AggregateKind.Sum ? formatter.Text(new BigInteger((decimal)value), c.Format) : formatter.Text(value);
            }
        }
        return c.Aggregate switch
        {
            AggregateKind.Average => texts.Average + " " + text,
            AggregateKind.Min => texts.Min + " " + text,
            AggregateKind.Max => texts.Max + " " + text,
            _ => text,
        };
    }

    /// <summary>The total in the column's own type, when nothing is lost.</summary>
    private static object? Back(object total, ColumnDefinition c)
    {
        if (total is not decimal d || !IsExact(c.Core) || decimal.Truncate(d) != d && c.Core != typeof(decimal)) return null;
        try { var v = Convert.ChangeType(d, c.Core, CultureInfo.InvariantCulture); return c.FormatterAccepts(v) ? v : null; }
        catch (Exception e) when (e is OverflowException or InvalidCastException) { return null; }
    }
}
