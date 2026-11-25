using PdfFormFiller.Core;

namespace PdfFormFiller.Reports.Internal;

/// <summary>Colour arithmetic. Contrast as defined by WCAG 2.</summary>
internal static class Colors
{
    public static PdfColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try { return PdfColor.Parse(text); }
        catch (FormatException e) { throw new PdfFormException(e.Message, e); }
    }

    /// <summary><paramref name="amount"/> 0 gives a, 1 gives b.</summary>
    public static PdfColor Mix(PdfColor a, PdfColor b, double amount)
    {
        byte C(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return new PdfColor(C(a.R, b.R), C(a.G, b.G), C(a.B, b.B));
    }

    private static double Luminance(PdfColor c)
    {
        static double L(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    public static double Contrast(PdfColor a, PdfColor b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>The colour, darkened as little as needed to be readable on a light background.</summary>
    public static PdfColor Darken(PdfColor c, PdfColor background, double contrast)
    {
        for (var i = 0; i <= 20; i++)
        {
            var d = Mix(c, PdfColor.Black, i / 20.0);
            if (Contrast(d, background) >= contrast) return d;
        }
        return PdfColor.Black;
    }

    /// <summary>White, near-black or black: the first with a contrast of 4.5. White or black always has it.</summary>
    public static PdfColor On(PdfColor background)
        => Contrast(PdfColor.White, background) >= 4.5 ? PdfColor.White : Contrast(Palette.Ink, background) >= 4.5 ? Palette.Ink : PdfColor.Black;
}

/// <summary>Background and text of a label.</summary>
internal sealed class ChipStyle
{
    public ChipStyle(PdfColor color)
    {
        Background = Colors.Mix(PdfColor.White, color, 0.14);
        Text = Colors.Darken(color, Background, 5);
        Dot = Colors.Contrast(color, Background) >= 1.6 ? color : Text;
    }

    public PdfColor Background { get; }
    public PdfColor Text { get; }
    public PdfColor Dot { get; }
}

/// <summary>All colours of a report, derived from the two brand colours.</summary>
internal sealed class Palette
{
    public static readonly PdfColor Ink = new(0x1C, 0x23, 0x2C);

    public Palette(ReportBranding b)
    {
        Primary = Colors.Parse(b.PrimaryColor ?? b.ThemeValue.Primary);
        Accent = Colors.Parse(b.AccentColor ?? (b.PrimaryColor is null ? b.ThemeValue.Accent : b.PrimaryColor));
        Filled = b.ThemeValue.Filled;
        Brand = Colors.Darken(Primary, PdfColor.White, 5.5);
        OnPrimary = Colors.On(Primary);
        // very light or very dark brand colours still give visible, quiet tints
        var tint = Colors.Contrast(Primary, PdfColor.White) < 2 ? Brand : Primary;
        Zebra = Colors.Mix(PdfColor.White, tint, 0.035);
        Band = Colors.Mix(PdfColor.White, tint, 0.11);
        Panel = Colors.Mix(PdfColor.White, tint, 0.05);
        Line = Colors.Mix(new PdfColor(0xDD, 0xE1, 0xE6), tint, 0.06);
        Strong = Colors.Mix(new PdfColor(0x8A, 0x93, 0x9E), tint, 0.25);
        Muted = Colors.Darken(new PdfColor(0x5C, 0x66, 0x73), Band, 4.8);
    }

    public PdfColor Primary { get; }
    public PdfColor Accent { get; }
    /// <summary>Whether the table head has a filled background.</summary>
    public bool Filled { get; }
    /// <summary>The primary colour as text on white.</summary>
    public PdfColor Brand { get; }
    public PdfColor OnPrimary { get; }
    public PdfColor Zebra { get; }
    public PdfColor Band { get; }
    public PdfColor Panel { get; }
    public PdfColor Line { get; }
    public PdfColor Strong { get; }
    public PdfColor Muted { get; }
}
