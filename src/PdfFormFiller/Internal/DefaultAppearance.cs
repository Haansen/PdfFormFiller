using System.Globalization;

namespace PdfFormFiller.Internal;

/// <summary>Parsed /DA string (e.g. "/Helv 9 Tf 0 g").</summary>
internal sealed record DefaultAppearance(double FontSize, string ColorOp)
{
    public static readonly DefaultAppearance Default = new(0, "0 g");

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
                    double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out size);
                    break;
                case "g" when i >= 1:
                    color = $"{tokens[i - 1]} g"; break;
                case "rg" when i >= 3:
                    color = $"{tokens[i - 3]} {tokens[i - 2]} {tokens[i - 1]} rg"; break;
                case "k" when i >= 4:
                    color = $"{tokens[i - 4]} {tokens[i - 3]} {tokens[i - 2]} {tokens[i - 1]} k"; break;
            }
        }
        if (size < 0 || double.IsNaN(size)) size = 0;
        return new DefaultAppearance(size, color);
    }
}
