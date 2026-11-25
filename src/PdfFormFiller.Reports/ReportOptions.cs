namespace PdfFormFiller.Reports;

/// <summary>Direction of the pages.</summary>
public enum PageOrientation
{
    /// <summary>Portrait, or landscape when the columns would be cramped.</summary>
    Auto,
    /// <summary>Portrait.</summary>
    Portrait,
    /// <summary>Landscape.</summary>
    Landscape,
}

/// <summary>How tightly rows are set.</summary>
public enum ReportDensity
{
    /// <summary>Many rows per page.</summary>
    Compact,
    /// <summary>Default.</summary>
    Normal,
    /// <summary>Generous spacing.</summary>
    Comfortable,
}

/// <summary>Where the logo is placed.</summary>
public enum LogoPosition
{
    /// <summary>Top left, before the company name.</summary>
    Left,
    /// <summary>Top right.</summary>
    Right,
}

/// <summary>Size of the paper.</summary>
public sealed class PaperSize
{
    internal const double Mm = 72 / 25.4;

    private PaperSize(double width, double height) => (Width, Height) = (width * Mm, height * Mm);

    /// <summary>210 x 297 mm.</summary>
    public static PaperSize A4 { get; } = new(210, 297);
    /// <summary>297 x 420 mm.</summary>
    public static PaperSize A3 { get; } = new(297, 420);
    /// <summary>148 x 210 mm.</summary>
    public static PaperSize A5 { get; } = new(148, 210);
    /// <summary>8.5 x 11 inches.</summary>
    public static PaperSize Letter { get; } = new(215.9, 279.4);

    /// <summary>Any size in millimetres, 50 to 5000 on each side. The orientation applies to custom sizes too.</summary>
    public static PaperSize Custom(double widthMm, double heightMm)
        => widthMm is >= 50 and <= 5000 && heightMm is >= 50 and <= 5000 ? new PaperSize(widthMm, heightMm)
            : throw new PdfFormException("Paper must be between 50 and 5000 mm on each side.");

    internal double Width { get; }
    internal double Height { get; }
}

/// <summary>Colours that go well together in labels. Any "#RRGGBB" can be used instead.</summary>
public static class ReportColors
{
    /// <summary>Positive, done.</summary>
    public const string Green = "#2E7D4F";
    /// <summary>Information, ongoing.</summary>
    public const string Blue = "#2566A8";
    /// <summary>Attention.</summary>
    public const string Amber = "#B7791F";
    /// <summary>Problem, cancelled.</summary>
    public const string Red = "#B83A3A";
    /// <summary>Neutral, inactive.</summary>
    public const string Grey = "#66707C";
    /// <summary>Special.</summary>
    public const string Purple = "#6B4C9A";
    /// <summary>New.</summary>
    public const string Teal = "#1F7A7A";
}

/// <summary>Ready-made looks. Colours set on <see cref="ReportBranding"/> take precedence, regardless of the order.</summary>
public sealed class ReportTheme
{
    private ReportTheme(string primary, string accent, bool filled) => (Primary, Accent, Filled) = (primary, accent, filled);

    internal string Primary { get; }
    internal string Accent { get; }
    internal bool Filled { get; }

    /// <summary>Deep blue and amber. The default.</summary>
    public static ReportTheme Nordic { get; } = new("#1E3A5F", "#D9943B", true);
    /// <summary>Dark green and brass.</summary>
    public static ReportTheme Forest { get; } = new("#1F4D3A", "#C2A14D", true);
    /// <summary>Near-black and coral.</summary>
    public static ReportTheme Graphite { get; } = new("#2B3038", "#D0594F", true);
    /// <summary>Petrol and sand.</summary>
    public static ReportTheme Ocean { get; } = new("#0E5561", "#D8B27A", true);
    /// <summary>Plum and rose.</summary>
    public static ReportTheme Plum { get; } = new("#4B2746", "#C9808A", true);
    /// <summary>Rules instead of a filled heading; saves ink.</summary>
    public static ReportTheme Paper { get; } = new("#2A2A2A", "#8A8A8A", false);
}

/// <summary>Logo, company name, colours, theme and fonts of a report.</summary>
public sealed class ReportBranding
{
    internal byte[]? LogoData { get; private set; }
    internal LogoPosition LogoPlace { get; private set; }
    internal string? LogoAlt { get; private set; }
    internal string? CompanyName { get; private set; }
    internal string? PrimaryColor { get; private set; }
    internal string? AccentColor { get; private set; }
    internal ReportTheme ThemeValue { get; private set; } = ReportTheme.Nordic;
    internal byte[]? Regular { get; private set; }
    internal byte[]? Bold { get; private set; }

    /// <summary>Logo in the page head.</summary>
    /// <param name="image">A PNG or JPEG file. Transparency is kept.</param>
    /// <param name="position">Left or right.</param>
    /// <param name="altText">Described to those who cannot see it; by default the company name.</param>
    public ReportBranding Logo(byte[] image, LogoPosition position = LogoPosition.Left, string? altText = null)
    {
        (LogoData, LogoPlace, LogoAlt) = (image ?? throw new ArgumentNullException(nameof(image)), position, altText);
        return this;
    }

    /// <summary>Company name in the page head.</summary>
    public ReportBranding Company(string name) { CompanyName = name; return this; }

    /// <summary>Main colour, "#RRGGBB". Text colours with enough contrast are calculated from it.</summary>
    public ReportBranding Primary(string color) { Internal.Colors.Parse(color); PrimaryColor = color; return this; }

    /// <summary>Colour of small decorations, "#RRGGBB".</summary>
    public ReportBranding Accent(string color) { Internal.Colors.Parse(color); AccentColor = color; return this; }

    /// <summary>A ready-made look.</summary>
    public ReportBranding Theme(ReportTheme theme) { ThemeValue = theme ?? throw new ArgumentNullException(nameof(theme)); return this; }

    /// <summary>
    /// Fonts to use instead of Liberation Sans. Characters they lack are taken from Liberation Sans.
    /// </summary>
    /// <param name="regular">A TrueType font file (.ttf) whose licence allows embedding.</param>
    /// <param name="bold">The bold version; without it the regular font is used for headings too.</param>
    public ReportBranding Fonts(byte[] regular, byte[]? bold = null)
    {
        (Regular, Bold) = (regular ?? throw new ArgumentNullException(nameof(regular)), bold);
        return this;
    }
}

/// <summary>Texts the report writes by itself. The defaults are Swedish; change the properties, or start from <see cref="English"/>.</summary>
public sealed class ReportTexts
{
    /// <summary>Before the page number.</summary>
    public string Page { get; set; } = "Sida";
    /// <summary>Between page number and number of pages.</summary>
    public string Of { get; set; } = "av";
    /// <summary>Label of the totals of the whole table.</summary>
    public string Total { get; set; } = "Totalt";
    /// <summary>Label of the totals of a group; {0} is the group.</summary>
    public string GroupTotal { get; set; } = "Totalt {0}";
    /// <summary>Before an average.</summary>
    public string Average { get; set; } = "Medel";
    /// <summary>Before a count.</summary>
    public string Count { get; set; } = "Antal";
    /// <summary>Before a smallest value.</summary>
    public string Min { get; set; } = "Min";
    /// <summary>Before a largest value.</summary>
    public string Max { get; set; } = "Max";
    /// <summary>When a table has no rows.</summary>
    public string Empty { get; set; } = "Inga rader";
    /// <summary>Number of rows in a group, for exactly one; {0} is the number.</summary>
    public string OneRow { get; set; } = "{0} rad";
    /// <summary>Number of rows in a group; {0} is the number.</summary>
    public string Rows { get; set; } = "{0} rader";
    /// <summary>After the name of a group that continues from the page before.</summary>
    public string Continued { get; set; } = "forts.";
    /// <summary>Group of rows that have no value to group by.</summary>
    public string NoGroup { get; set; } = "(saknas)";
    /// <summary>True.</summary>
    public string Yes { get; set; } = "Ja";
    /// <summary>False.</summary>
    public string No { get; set; } = "Nej";
    /// <summary>Heading of the only column when the items are plain values.</summary>
    public string Value { get; set; } = "Värde";
    /// <summary>Alternative text of a logo without any of its own when there is no company name.</summary>
    public string Logo { get; set; } = "Logotyp";

    /// <summary>English texts.</summary>
    public static ReportTexts English() => new()
    {
        Page = "Page", Of = "of", Total = "Total", GroupTotal = "Total {0}", Average = "Average", Count = "Count", Min = "Min", Max = "Max",
        Empty = "No rows", OneRow = "{0} row", Rows = "{0} rows", Continued = "cont.", NoGroup = "(none)", Yes = "Yes", No = "No",
        Value = "Value", Logo = "Logo",
    };
}
