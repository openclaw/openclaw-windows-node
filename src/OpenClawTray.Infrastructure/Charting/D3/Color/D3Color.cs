// Port of d3-color — ISC License, Copyright 2010-2023 Mike Bostock
// Simplified for chart usage: parse CSS color strings, manipulate RGB/HSL

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// CSS color utilities. Parses hex, rgb(), hsl(), and named colors.
/// Provides brighter/darker manipulation for chart theming.
/// </summary>
public readonly struct D3Color
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public double Opacity { get; }

    public D3Color(byte r, byte g, byte b, double opacity = 1.0)
    {
        R = r; G = g; B = b; Opacity = Math.Clamp(opacity, 0, 1);
    }

    public D3Color Brighter(double k = 1)
    {
        double factor = Math.Pow(1.0 / 0.7, k);
        return new D3Color(
            ClampByte(R * factor),
            ClampByte(G * factor),
            ClampByte(B * factor),
            Opacity);
    }

    public D3Color Darker(double k = 1)
    {
        double factor = Math.Pow(0.7, k);
        return new D3Color(
            ClampByte(R * factor),
            ClampByte(G * factor),
            ClampByte(B * factor),
            Opacity);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
    public string ToRgb() => Opacity < 1 ? $"rgba({R}, {G}, {B}, {Opacity})" : $"rgb({R}, {G}, {B})";
    public override string ToString() => ToRgb();

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    // ── Parsing ────────────────────────────────────────────────────────

    public static D3Color Parse(string format)
    {
        format = format.Trim().ToLowerInvariant();

        // Hex
        if (format.StartsWith('#'))
        {
            string hex = format[1..];
            if (hex.Length == 3)
                return new D3Color(
                    (byte)(Convert.ToByte(hex[0..1], 16) * 17),
                    (byte)(Convert.ToByte(hex[1..2], 16) * 17),
                    (byte)(Convert.ToByte(hex[2..3], 16) * 17));
            if (hex.Length == 6)
                return new D3Color(
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }

        // rgb(r, g, b) / rgba(r, g, b, a)
        if (format.StartsWith("rgb"))
        {
            var inner = ExtractParenContents(format);
            if (inner is not null)
            {
                var parts = inner.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 &&
                    double.TryParse(parts[0], global::System.Globalization.CultureInfo.InvariantCulture, out var r) &&
                    double.TryParse(parts[1], global::System.Globalization.CultureInfo.InvariantCulture, out var g) &&
                    double.TryParse(parts[2], global::System.Globalization.CultureInfo.InvariantCulture, out var b))
                {
                    double a = 1.0;
                    if (parts.Length >= 4)
                        double.TryParse(parts[3], global::System.Globalization.CultureInfo.InvariantCulture, out a);
                    return new D3Color(ClampByte(r), ClampByte(g), ClampByte(b), a);
                }
            }
        }

        // hsl(h, s%, l%) / hsla(h, s%, l%, a)
        if (format.StartsWith("hsl"))
        {
            var inner = ExtractParenContents(format);
            if (inner is not null)
            {
                var parts = inner.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 &&
                    double.TryParse(parts[0], global::System.Globalization.CultureInfo.InvariantCulture, out var h) &&
                    double.TryParse(parts[1].TrimEnd('%'), global::System.Globalization.CultureInfo.InvariantCulture, out var s) &&
                    double.TryParse(parts[2].TrimEnd('%'), global::System.Globalization.CultureInfo.InvariantCulture, out var l))
                {
                    double a = 1.0;
                    if (parts.Length >= 4)
                        double.TryParse(parts[3], global::System.Globalization.CultureInfo.InvariantCulture, out a);
                    return FromHsl(h, s / 100.0, l / 100.0, a);
                }
            }
        }

        // Named colors (common chart colors)
        if (NamedColors.TryGetValue(format, out var named))
            return named;

        return new D3Color(0, 0, 0);
    }

    private static string? ExtractParenContents(string s)
    {
        int open = s.IndexOf('(');
        int close = s.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        return s[(open + 1)..close];
    }

    private static D3Color FromHsl(double h, double s, double l, double opacity = 1.0)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;

        double r, g, b;
        if (h < 60)       { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else               { r = c; g = 0; b = x; }

        return new D3Color(ClampByte((r + m) * 255), ClampByte((g + m) * 255), ClampByte((b + m) * 255), opacity);
    }

    // ── Predefined palettes for charting ───────────────────────────────

    /// <summary>D3's category10 color scheme — 10 distinct colors for categorical data.</summary>
    public static readonly IReadOnlyList<D3Color> Category10 = Array.AsReadOnly(new[]
    {
        Parse("#1f77b4"), Parse("#ff7f0e"), Parse("#2ca02c"), Parse("#d62728"), Parse("#9467bd"),
        Parse("#8c564b"), Parse("#e377c2"), Parse("#7f7f7f"), Parse("#bcbd22"), Parse("#17becf"),
    });

    /// <summary>Tableau10 color scheme — another popular 10-color categorical palette.</summary>
    public static readonly IReadOnlyList<D3Color> Tableau10 = Array.AsReadOnly(new[]
    {
        Parse("#4e79a7"), Parse("#f28e2b"), Parse("#e15759"), Parse("#76b7b2"), Parse("#59a14f"),
        Parse("#edc948"), Parse("#b07aa1"), Parse("#ff9da7"), Parse("#9c755f"), Parse("#bab0ac"),
    });

    private static readonly Dictionary<string, D3Color> NamedColors = new()
    {
        ["red"] = new(255, 0, 0),
        ["green"] = new(0, 128, 0),
        ["blue"] = new(0, 0, 255),
        ["white"] = new(255, 255, 255),
        ["black"] = new(0, 0, 0),
        ["orange"] = new(255, 165, 0),
        ["yellow"] = new(255, 255, 0),
        ["purple"] = new(128, 0, 128),
        ["gray"] = new(128, 128, 128),
        ["grey"] = new(128, 128, 128),
        ["steelblue"] = new(70, 130, 180),
        ["tomato"] = new(255, 99, 71),
        ["coral"] = new(255, 127, 80),
        ["teal"] = new(0, 128, 128),
        ["navy"] = new(0, 0, 128),
        ["gold"] = new(255, 215, 0),
        ["crimson"] = new(220, 20, 60),
        ["darkgreen"] = new(0, 100, 0),
        ["dodgerblue"] = new(30, 144, 255),
        ["transparent"] = new(0, 0, 0, 0),
    };
}
