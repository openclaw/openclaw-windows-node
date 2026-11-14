using System.Globalization;

namespace OpenClaw.Shared.Themes;

/// <summary>
/// Parses the closed CSS color grammar that Gateway theme definitions accept (upstream
/// <c>requireColor</c> in <c>packages/gateway-protocol/src/theme.ts</c>) and converts it to sRGB:
/// hex, <c>transparent</c>/<c>black</c>/<c>white</c>, legacy and modern <c>rgb()</c>/<c>hsl()</c>,
/// <c>lab()</c>, <c>lch()</c>, <c>oklab()</c>, <c>oklch()</c> and <c>color()</c> in the CSS predefined spaces.
/// Out-of-gamut results are clipped per channel.
/// <example><code>
/// if (CssColor.TryParse("oklch(0.7 0.1 200)", out var color)) Use(color.ToHex());
/// </code></example>
/// </summary>
public static class CssColor
{
    public static ThemeColor Parse(string value) =>
        TryParse(value, out var color) ? color : throw new FormatException($"'{value}' is not a supported CSS color.");

    public static bool TryParse(string? value, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120)
            return false;
        var text = value.Trim().ToLowerInvariant();
        switch (text)
        {
            case "transparent":
                color = ThemeColor.Transparent;
                return true;
            case "black":
                color = ThemeColor.Black;
                return true;
            case "white":
                color = ThemeColor.White;
                return true;
        }
        if (text[0] == '#')
            return ThemeColor.TryFromHex(text, out color);

        var open = text.IndexOf('(');
        if (open <= 0 || text[^1] != ')')
            return false;
        var name = text[..open].Trim();
        var inner = text[(open + 1)..^1].Trim();
        if (inner.Length == 0)
            return false;

        try
        {
            return inner.Contains(',')
                ? TryParseLegacy(name, inner, out color)
                : TryParseModern(name, inner, out color);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseLegacy(string name, string inner, out ThemeColor color)
    {
        color = default;
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (3 or 4) || name is not ("rgb" or "rgba" or "hsl" or "hsla"))
            return false;
        double alpha = 1;
        if (parts.Length == 4 && !TryAlpha(parts[3], out alpha))
            return false;
        // Legacy syntax needs all-number or all-percent rgb channels and percent hsl saturation/lightness.
        if (name.StartsWith("rgb", StringComparison.Ordinal))
        {
            var percents = parts.Take(3).Count(part => part.EndsWith('%'));
            return percents is 0 or 3 && TryRgb(parts[0], parts[1], parts[2], alpha, out color);
        }
        return parts[1].EndsWith('%') && parts[2].EndsWith('%') &&
            TryHsl(parts[0], parts[1], parts[2], alpha, out color);
    }

    private static bool TryParseModern(string name, string inner, out ThemeColor color)
    {
        color = default;
        var slash = inner.IndexOf('/');
        double alpha = 1;
        if (slash >= 0)
        {
            if (!TryAlpha(inner[(slash + 1)..].Trim(), out alpha))
                return false;
            inner = inner[..slash];
        }
        var tokens = inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (name == "color")
        {
            if (tokens.Length != 4)
                return false;
            if (!TryUnit(tokens[1], out var c0) || !TryUnit(tokens[2], out var c1) || !TryUnit(tokens[3], out var c2))
                return false;
            return TryPredefined(tokens[0], c0, c1, c2, alpha, out color);
        }

        if (tokens.Length != 3)
            return false;
        switch (name)
        {
            case "rgb" or "rgba":
                return TryRgb(tokens[0], tokens[1], tokens[2], alpha, out color);
            case "hsl" or "hsla":
                return TryHsl(tokens[0], tokens[1], tokens[2], alpha, out color);
            case "lab":
            {
                if (!TryScaled(tokens[0], 100, out var l) || !TryScaled(tokens[1], 125, out var a) || !TryScaled(tokens[2], 125, out var b))
                    return false;
                color = FromLinearSrgb(XyzD65ToLinearSrgb(D50ToD65(LabToXyzD50(l, a, b))), alpha);
                return true;
            }
            case "lch":
            {
                if (!TryScaled(tokens[0], 100, out var l) || !TryScaled(tokens[1], 150, out var c) || !TryHue(tokens[2], out var h))
                    return false;
                var (a, b) = Polar(c, h);
                color = FromLinearSrgb(XyzD65ToLinearSrgb(D50ToD65(LabToXyzD50(l, a, b))), alpha);
                return true;
            }
            case "oklab":
            {
                if (!TryScaled(tokens[0], 1, out var l) || !TryScaled(tokens[1], 0.4, out var a) || !TryScaled(tokens[2], 0.4, out var b))
                    return false;
                color = FromLinearSrgb(OklabToLinearSrgb(l, a, b), alpha);
                return true;
            }
            case "oklch":
            {
                if (!TryScaled(tokens[0], 1, out var l) || !TryScaled(tokens[1], 0.4, out var c) || !TryHue(tokens[2], out var h))
                    return false;
                var (a, b) = Polar(c, h);
                color = FromLinearSrgb(OklabToLinearSrgb(l, a, b), alpha);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryRgb(string r, string g, string b, double alpha, out ThemeColor color)
    {
        color = default;
        static bool Channel(string token, out double unit)
        {
            unit = 0;
            if (!TryNumber(token, out var v, out var unitSuffix) || unitSuffix is not ("" or "%"))
                return false;
            unit = unitSuffix == "%" ? v / 100 : v / 255;
            return true;
        }
        if (!Channel(r, out var rr) || !Channel(g, out var gg) || !Channel(b, out var bb))
            return false;
        color = FromSrgb(rr, gg, bb, alpha);
        return true;
    }

    private static bool TryHsl(string hue, string saturation, string lightness, double alpha, out ThemeColor color)
    {
        color = default;
        if (!TryHue(hue, out var h) || !TryPercentLike(saturation, out var s) || !TryPercentLike(lightness, out var l))
            return false;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double F(double n)
        {
            var k = (n + h / 30) % 12;
            var a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
        color = FromSrgb(F(0), F(8), F(4), alpha);
        return true;
    }

    private static bool TryPredefined(string space, double c0, double c1, double c2, double alpha, out ThemeColor color)
    {
        color = default;
        double[] linear;
        switch (space)
        {
            case "srgb":
                color = FromSrgb(c0, c1, c2, alpha);
                return true;
            case "srgb-linear":
                linear = [c0, c1, c2];
                break;
            case "display-p3":
                linear = XyzD65ToLinearSrgb(Multiply(LinearP3ToXyz, [SrgbToLinear(c0), SrgbToLinear(c1), SrgbToLinear(c2)]));
                break;
            case "a98-rgb":
                linear = XyzD65ToLinearSrgb(Multiply(LinearA98ToXyz, [A98ToLinear(c0), A98ToLinear(c1), A98ToLinear(c2)]));
                break;
            case "prophoto-rgb":
                linear = XyzD65ToLinearSrgb(D50ToD65(Multiply(LinearProPhotoToXyzD50, [ProPhotoToLinear(c0), ProPhotoToLinear(c1), ProPhotoToLinear(c2)])));
                break;
            case "rec2020":
                linear = XyzD65ToLinearSrgb(Multiply(Linear2020ToXyz, [Rec2020ToLinear(c0), Rec2020ToLinear(c1), Rec2020ToLinear(c2)]));
                break;
            case "xyz" or "xyz-d65":
                linear = XyzD65ToLinearSrgb([c0, c1, c2]);
                break;
            case "xyz-d50":
                linear = XyzD65ToLinearSrgb(D50ToD65([c0, c1, c2]));
                break;
            default:
                return false;
        }
        color = FromLinearSrgb(linear, alpha);
        return true;
    }

    private static bool TryNumber(string token, out double value, out string unit)
    {
        value = 0;
        unit = "";
        var end = token.Length;
        while (end > 0 && (char.IsAsciiLetter(token[end - 1]) || token[end - 1] == '%'))
            end--;
        unit = token[end..];
        // Exponent notation such as 1e3 ends in a digit, so a trailing letter is always a unit.
        return end > 0 && double.TryParse(token[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value);
    }

    /// <summary>A bare number or a percentage where 100% equals <paramref name="percentReference"/>.</summary>
    private static bool TryScaled(string token, double percentReference, out double value)
    {
        value = 0;
        if (!TryNumber(token, out var v, out var unit) || unit is not ("" or "%"))
            return false;
        value = unit == "%" ? v / 100 * percentReference : v;
        return true;
    }

    /// <summary>CSS Color 4 color() channels: numbers are 0..1 and 100% is 1.</summary>
    private static bool TryUnit(string token, out double value) => TryScaled(token, 1, out value);

    /// <summary>HSL saturation/lightness: a percentage, or a bare number read as a percentage value.</summary>
    private static bool TryPercentLike(string token, out double unit)
    {
        unit = 0;
        if (!TryNumber(token, out var v, out var suffix) || suffix is not ("" or "%"))
            return false;
        unit = v / 100;
        return true;
    }

    private static bool TryAlpha(string token, out double alpha)
    {
        alpha = 1;
        if (!TryNumber(token, out var v, out var unit) || unit is not ("" or "%"))
            return false;
        alpha = Math.Clamp(unit == "%" ? v / 100 : v, 0, 1);
        return true;
    }

    private static bool TryHue(string token, out double degrees)
    {
        degrees = 0;
        if (!TryNumber(token, out var v, out var unit))
            return false;
        degrees = unit switch
        {
            "" or "deg" => v,
            "grad" => v * 0.9,
            "rad" => v * 180 / Math.PI,
            "turn" => v * 360,
            _ => double.NaN,
        };
        if (double.IsNaN(degrees))
            return false;
        degrees %= 360;
        if (degrees < 0)
            degrees += 360;
        return true;
    }

    private static (double A, double B) Polar(double chroma, double hueDegrees)
    {
        var radians = hueDegrees * Math.PI / 180;
        var c = Math.Max(0, chroma);
        return (c * Math.Cos(radians), c * Math.Sin(radians));
    }

    private static ThemeColor FromSrgb(double r, double g, double b, double alpha) =>
        new(ThemeColor.ToByte(alpha), ThemeColor.ToByte(r), ThemeColor.ToByte(g), ThemeColor.ToByte(b));

    private static ThemeColor FromLinearSrgb(double[] linear, double alpha) =>
        FromSrgb(LinearToSrgb(linear[0]), LinearToSrgb(linear[1]), LinearToSrgb(linear[2]), alpha);

    private static double SrgbToLinear(double c)
    {
        var abs = Math.Abs(c);
        return abs <= 0.04045 ? c / 12.92 : Math.Sign(c) * Math.Pow((abs + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double c)
    {
        var abs = Math.Abs(c);
        return abs > 0.0031308 ? Math.Sign(c) * (1.055 * Math.Pow(abs, 1 / 2.4) - 0.055) : 12.92 * c;
    }

    private static double A98ToLinear(double c) => Math.Sign(c) * Math.Pow(Math.Abs(c), 563d / 256);

    private static double ProPhotoToLinear(double c)
    {
        var abs = Math.Abs(c);
        return abs <= 16d / 512 ? c / 16 : Math.Sign(c) * Math.Pow(abs, 1.8);
    }

    private static double Rec2020ToLinear(double c)
    {
        const double alpha = 1.09929682680944;
        const double beta = 0.018053968510807;
        var abs = Math.Abs(c);
        return abs < beta * 4.5 ? c / 4.5 : Math.Sign(c) * Math.Pow((abs + alpha - 1) / alpha, 1 / 0.45);
    }

    private static double[] LabToXyzD50(double l, double a, double b)
    {
        const double kappa = 24389d / 27;
        const double epsilon = 216d / 24389;
        double[] white = [0.3457 / 0.3585, 1.0, (1.0 - 0.3457 - 0.3585) / 0.3585];
        var f1 = (l + 16) / 116;
        var f0 = a / 500 + f1;
        var f2 = f1 - b / 200;
        var x = Math.Pow(f0, 3) > epsilon ? Math.Pow(f0, 3) : (116 * f0 - 16) / kappa;
        var y = l > kappa * epsilon ? Math.Pow((l + 16) / 116, 3) : l / kappa;
        var z = Math.Pow(f2, 3) > epsilon ? Math.Pow(f2, 3) : (116 * f2 - 16) / kappa;
        return [x * white[0], y * white[1], z * white[2]];
    }

    private static double[] OklabToLinearSrgb(double l, double a, double b)
    {
        var lp = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var mp = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var sp = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);
        return
        [
            4.0767416621 * lp - 3.3077115913 * mp + 0.2309699292 * sp,
            -1.2684380046 * lp + 2.6097574011 * mp - 0.3413193965 * sp,
            -0.0041960863 * lp - 0.7034186147 * mp + 1.7076147010 * sp,
        ];
    }

    private static double[] D50ToD65(double[] xyz) => Multiply(BradfordD50ToD65, xyz);

    private static double[] XyzD65ToLinearSrgb(double[] xyz) => Multiply(XyzToLinearSrgb, xyz);

    private static double[] Multiply(double[,] m, double[] v) =>
    [
        m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
        m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
        m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2],
    ];

    // Matrices from CSS Color 4 (https://www.w3.org/TR/css-color-4/#color-conversion-code).
    private static readonly double[,] XyzToLinearSrgb =
    {
        { 3.2409699419045226, -1.537383177570094, -0.4986107602930034 },
        { -0.9692436362808796, 1.8759675015077202, 0.04155505740717559 },
        { 0.05563007969699366, -0.20397695888897652, 1.0569715142428786 },
    };

    private static readonly double[,] LinearP3ToXyz =
    {
        { 0.4865709486482162, 0.26566769316909306, 0.1982172852343625 },
        { 0.2289745640697488, 0.6917385218365064, 0.079286914093745 },
        { 0.0, 0.04511338185890264, 1.043944368900976 },
    };

    private static readonly double[,] LinearA98ToXyz =
    {
        { 0.5766690429101305, 0.1855582379065463, 0.1882286462349947 },
        { 0.29734497525053605, 0.6273635662554661, 0.07529145849399788 },
        { 0.02703136138641234, 0.07068885253582723, 0.9913375368376388 },
    };

    private static readonly double[,] LinearProPhotoToXyzD50 =
    {
        { 0.7977666449006423, 0.13518129740053308, 0.0313477341283922 },
        { 0.2880748288194013, 0.711835234241873, 0.00008993693872564 },
        { 0.0, 0.0, 0.8251046025104602 },
    };

    private static readonly double[,] Linear2020ToXyz =
    {
        { 0.6369580483012914, 0.14461690358620832, 0.1688809751641721 },
        { 0.2627002120112671, 0.6779980715188708, 0.05930171646986196 },
        { 0.0, 0.028072693049087428, 1.060985057710791 },
    };

    private static readonly double[,] BradfordD50ToD65 =
    {
        { 0.9554734527042182, -0.023098536874261423, 0.0632593086610217 },
        { -0.028369706963208136, 1.0099954580058226, 0.021041398966943008 },
        { 0.012314001688319899, -0.020507696433477912, 1.3303659366080753 },
    };
}
