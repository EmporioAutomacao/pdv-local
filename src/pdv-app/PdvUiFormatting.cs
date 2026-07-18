using System.Globalization;
using System.Windows.Media;
using PdvLocal.Core;

namespace PdvLocal.App;

internal static class PdvUiFormatting
{
    public static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    public static decimal ParseMoney(string value, string fieldName)
    {
        if (TryParseMoney(value, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{fieldName} invalido.");
    }

    public static bool TryParseMoney(string value, out decimal parsed)
    {
        var trimmed = value.Trim();
        var commaIndex = trimmed.LastIndexOf(',');
        var dotIndex = trimmed.LastIndexOf('.');

        if (commaIndex >= 0 && commaIndex > dotIndex)
        {
            return decimal.TryParse(trimmed, NumberStyles.Number, BrazilianCulture, out parsed)
                || decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
                || decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
        }

        if (dotIndex >= 0 && dotIndex > commaIndex)
        {
            return decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)
                || decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
                || decimal.TryParse(trimmed, NumberStyles.Number, BrazilianCulture, out parsed);
        }

        return decimal.TryParse(trimmed, NumberStyles.Number, BrazilianCulture, out parsed)
            || decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
            || decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
    }

    public static string FormatDecimal(decimal value)
    {
        return value.ToString("0.00", BrazilianCulture);
    }

    public static string FormatMoney(decimal value)
    {
        return value.ToString("C", BrazilianCulture);
    }

    public static string Capitalize(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : BrazilianCulture.TextInfo.ToTitleCase(value);
    }

    public static Brush BrushFromHex(string color)
    {
        return (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    public static string EmptyAsDash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    public static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "-";
    }

    public static string DisplayCode(PdvProduct product)
    {
        return FirstNonEmpty(product.Barcode, product.Sku, product.ExternalKey, product.ProductId.ToString());
    }
}
