// Port of d3-interpolate color functions — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Interpolation functions for colors, dates, and strings.
/// Extends the base D3Interpolate with additional types.
/// </summary>
public static class D3InterpolateColor
{
    /// <summary>
    /// Returns a function that interpolates between two colors in RGB space.
    /// Port of d3.interpolateRgb().
    /// </summary>
    public static Func<double, D3Color> Rgb(D3Color a, D3Color b)
    {
        return t => new D3Color(
            ClampByte(a.R + (b.R - a.R) * t),
            ClampByte(a.G + (b.G - a.G) * t),
            ClampByte(a.B + (b.B - a.B) * t),
            a.Opacity + (b.Opacity - a.Opacity) * t);
    }

    /// <summary>
    /// Returns a function that interpolates between two colors in RGB space,
    /// specified as CSS color strings.
    /// </summary>
    public static Func<double, D3Color> Rgb(string a, string b)
    {
        return Rgb(D3Color.Parse(a), D3Color.Parse(b));
    }

    /// <summary>
    /// Returns a function that interpolates between two colors in HSL space.
    /// Port of d3.interpolateHsl().
    /// </summary>
    public static Func<double, D3Color> Hsl(D3Color a, D3Color b)
    {
        var (ah, as_, al) = ToHsl(a);
        var (bh, bs, bl) = ToHsl(b);

        // Shortest path hue interpolation
        double dh = bh - ah;
        if (dh > 180) dh -= 360;
        else if (dh < -180) dh += 360;

        return t =>
        {
            double h = ah + dh * t;
            double s = as_ + (bs - as_) * t;
            double l = al + (bl - al) * t;
            double opacity = a.Opacity + (b.Opacity - a.Opacity) * t;
            return FromHsl(h, s, l, opacity);
        };
    }

    /// <summary>
    /// Returns a function that interpolates between two Date values.
    /// Port of d3.interpolateDate().
    /// </summary>
    public static Func<double, DateTime> Date(DateTime a, DateTime b)
    {
        long aTicks = a.Ticks;
        long bTicks = b.Ticks;
        return t => new DateTime((long)(aTicks + (bTicks - aTicks) * t));
    }

    /// <summary>
    /// Returns a function that interpolates between two arrays of numbers.
    /// Port of d3.interpolateArray().
    /// </summary>
    public static Func<double, double[]> Array(double[] a, double[] b)
    {
        int n = Math.Max(a.Length, b.Length);
        return t =>
        {
            var result = new double[n];
            for (int i = 0; i < n; i++)
            {
                double av = i < a.Length ? a[i] : 0;
                double bv = i < b.Length ? b[i] : 0;
                result[i] = av + (bv - av) * t;
            }
            return result;
        };
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static (double h, double s, double l) ToHsl(D3Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double h = 0, s = 0;

        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static D3Color FromHsl(double h, double s, double l, double opacity)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        double r, g, b;

        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return new D3Color(
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255),
            opacity);
    }
}
