using System.Globalization;

namespace PdfFormFiller.Internal;

/// <summary>Parsed /DA string (e.g. "/Helv 9 Tf 0 g"). Only finite numbers ever reach the generated content stream.</summary>
internal sealed record DefaultAppearance(double FontSize, string ColorOp)
{
    public static readonly DefaultAppearance Default = new(0, "0 g");

    /// <summary>Sizes above this are treated as damaged and replaced by automatic sizing.</summary>
    private const double MaxFontSize = 1000;

    public static DefaultAppearance Parse(string? da)
    {
        if (string.IsNullOrWhiteSpace(da)) return Default;
        var tokens = da.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        double size = 0;
        var color = "0 g";
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "Tf" when i >= 1:
                    if (!TryNumber(tokens[i - 1], out size)) size = 0;
                    break;
                case "g" when i >= 1 && Numbers(tokens, i, 1) is { } g:
                    color = $"{g} g"; break;
                case "rg" when i >= 3 && Numbers(tokens, i, 3) is { } rgb:
                    color = $"{rgb} rg"; break;
                case "k" when i >= 4 && Numbers(tokens, i, 4) is { } cmyk:
                    color = $"{cmyk} k"; break;
            }
        }
        if (size < 0 || !double.IsFinite(size) || size > MaxFontSize) size = 0;
        return new DefaultAppearance(size, color);
    }

    private static bool TryNumber(string token, out double value)
        => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>The <paramref name="count"/> operands before <paramref name="opIndex"/> as normalised numbers, or null if any is not a number.</summary>
    private static string? Numbers(string[] tokens, int opIndex, int count)
    {
        var parts = new string[count];
        for (var k = 0; k < count; k++)
        {
            if (!TryNumber(tokens[opIndex - count + k], out var v)) return null;
            parts[k] = PdfExt.Fmt(Math.Clamp(v, 0, 1));
        }
        return string.Join(' ', parts);
    }
}
