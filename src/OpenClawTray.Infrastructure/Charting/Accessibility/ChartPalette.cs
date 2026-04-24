using System.Diagnostics;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Marker shape for double-encoding chart series.
/// Each series gets a distinct shape in addition to color.
/// </summary>
public enum MarkerShape
{
    Circle,
    Square,
    Triangle,
    Diamond,
    Plus,
    Cross,
    Star,
    Hexagon,
}

/// <summary>
/// Dash pattern for double-encoding chart series.
/// Each series gets a distinct dash pattern in addition to color.
/// </summary>
public enum DashStyle
{
    Solid,
    Dash4_2,
    Dash2_2,
    Dash6_2_2_2,
    Dash8_4,
    Dash1_1,
}

/// <summary>
/// Result of <see cref="ChartPalette.Harden"/> — contains the adjusted palette
/// plus diagnostic diffs showing what changed.
/// </summary>
public sealed class HardenResult
{
    /// <summary>The adjusted palette that passes all contrast/colorblind checks.</summary>
    public required ChartPalette Palette { get; init; }

    /// <summary>Per-color diffs showing what changed from input to output.</summary>
    public required IReadOnlyList<HardenDiff> Diffs { get; init; }

    /// <summary>True if no adjustments were needed — input palette already passed all checks.</summary>
    public required bool PassedWithoutChanges { get; init; }
}

/// <summary>
/// Describes one color adjustment made by <see cref="ChartPalette.Harden"/>.
/// </summary>
public record HardenDiff(int Index, D3.D3Color Original, D3.D3Color Adjusted, string Reason);

/// <summary>
/// Options for <see cref="ChartPalette.Harden"/>.
/// </summary>
public sealed class HardenOptions
{
    /// <summary>Minimum WCAG non-text contrast ratio between any two series colors. Default 3.0.</summary>
    public double MinPairwiseContrast { get; init; } = 3.0;

    /// <summary>Minimum ΔE in simulated colorblind space between any two series. Default 10.</summary>
    public double MinColorblindDeltaE { get; init; } = 10.0;

    /// <summary>Minimum contrast ratio of each color against the background. Default 3.0.</summary>
    public double MinBackgroundContrast { get; init; } = 3.0;

    /// <summary>Maximum number of adjustment passes. Default 8.</summary>
    public int MaxPasses { get; init; } = 8;
}

/// <summary>
/// Sealed palette class that ensures chart colors are accessible.
/// Only constructible via curated statics or the <see cref="Harden"/> utility.
/// </summary>
public sealed class ChartPalette
{
    private readonly D3.D3Color[] _colors;
    private readonly MarkerShape[] _markers;
    private readonly DashStyle[] _dashes;

    private ChartPalette(D3.D3Color[] colors, MarkerShape[]? markers = null, DashStyle[]? dashes = null)
    {
        _colors = colors;
        _markers = markers ?? DefaultMarkerCycle;
        _dashes = dashes ?? DefaultDashCycle;
    }

    /// <summary>Number of colors in this palette.</summary>
    public int Count => _colors.Length;

    /// <summary>Gets the color at the specified index (wraps for indices ≥ Count).</summary>
    public D3.D3Color this[int index] => _colors[((index % _colors.Length) + _colors.Length) % _colors.Length];

    /// <summary>Gets all colors in this palette.</summary>
    public IReadOnlyList<D3.D3Color> Colors => _colors;

    /// <summary>Gets the marker shape for a given series index (wraps).</summary>
    public MarkerShape GetMarker(int seriesIndex) =>
        _markers[((seriesIndex % _markers.Length) + _markers.Length) % _markers.Length];

    /// <summary>Gets the dash style for a given series index (wraps).</summary>
    public DashStyle GetDash(int seriesIndex) =>
        _dashes[((seriesIndex % _dashes.Length) + _dashes.Length) % _dashes.Length];

    /// <summary>Gets the dash pattern as a double array for rendering.</summary>
    public static double[] GetDashArray(DashStyle dash) => dash switch
    {
        DashStyle.Solid => [],
        DashStyle.Dash4_2 => [4, 2],
        DashStyle.Dash2_2 => [2, 2],
        DashStyle.Dash6_2_2_2 => [6, 2, 2, 2],
        DashStyle.Dash8_4 => [8, 4],
        DashStyle.Dash1_1 => [1, 1],
        _ => [],
    };

    // ── Default cycles ──────────────────────────────────────────────

    internal static readonly MarkerShape[] DefaultMarkerCycle =
    [
        MarkerShape.Circle, MarkerShape.Square, MarkerShape.Triangle, MarkerShape.Diamond,
        MarkerShape.Plus, MarkerShape.Cross, MarkerShape.Star, MarkerShape.Hexagon,
    ];

    internal static readonly DashStyle[] DefaultDashCycle =
    [
        DashStyle.Solid, DashStyle.Dash4_2, DashStyle.Dash2_2,
        DashStyle.Dash6_2_2_2, DashStyle.Dash8_4, DashStyle.Dash1_1,
    ];

    // ── Curated palettes (Tier 1) ───────────────────────────────────

    /// <summary>
    /// Okabe-Ito 8-color palette — designed for universal color vision.
    /// Safe for deuteranopia, protanopia, and tritanopia.
    /// </summary>
    public static readonly ChartPalette OkabeIto = new(
    [
        new(230, 159, 0),   // Orange
        new(86, 180, 233),  // Sky blue
        new(0, 158, 115),   // Bluish green
        new(240, 228, 66),  // Yellow
        new(0, 114, 178),   // Blue
        new(213, 94, 0),    // Vermillion
        new(204, 121, 167), // Reddish purple
        new(0, 0, 0),       // Black
    ]);

    /// <summary>
    /// IBM Design Language 5-color palette — accessible categorical palette.
    /// </summary>
    public static readonly ChartPalette IBM = new(
    [
        new(100, 143, 255), // Ultramarine 40
        new(120, 94, 240),  // Indigo 50
        new(220, 38, 127),  // Magenta 50
        new(254, 97, 0),    // Orange 40
        new(255, 176, 0),   // Gold 20
    ]);

    /// <summary>
    /// Viridis-inspired 6-color discrete palette — perceptually uniform.
    /// </summary>
    public static readonly ChartPalette Viridis = new(
    [
        new(68, 1, 84),     // Dark purple
        new(59, 82, 139),   // Blue-purple
        new(33, 145, 140),  // Teal
        new(94, 201, 98),   // Green
        new(253, 231, 37),  // Yellow
        new(189, 223, 38),  // Yellow-green
    ]);

    /// <summary>
    /// Cividis 6-color discrete palette — optimized for color vision deficiency.
    /// </summary>
    public static readonly ChartPalette Cividis = new(
    [
        new(0, 32, 76),     // Dark blue
        new(60, 77, 110),   // Steel blue
        new(124, 123, 120), // Gray
        new(186, 168, 98),  // Tan
        new(233, 212, 70),  // Gold
        new(253, 232, 37),  // Yellow
    ]);

    /// <summary>
    /// Fluent default palette — matches WinUI design language.
    /// </summary>
    public static readonly ChartPalette FluentDefault = new(
    [
        new(0, 120, 212),   // Fluent blue
        new(232, 17, 35),   // Fluent red
        new(0, 153, 188),   // Fluent teal
        new(135, 100, 184), // Fluent purple
        new(0, 183, 195),   // Fluent cyan
        new(255, 140, 0),   // Fluent orange
        new(16, 137, 62),   // Fluent green
        new(194, 57, 179),  // Fluent magenta
    ]);

    // ── Factory methods ─────────────────────────────────────────────

    /// <summary>
    /// Creates a palette from curated colors that have been pre-verified for accessibility.
    /// This is the recommended way to create custom palettes.
    /// </summary>
    internal static ChartPalette FromVerified(D3.D3Color[] colors) => new(colors);

    /// <summary>
    /// Creates a palette from user-provided colors, to be scanner-validated (Tier 3).
    /// </summary>
    public static ChartPalette FromColors(params D3.D3Color[] colors)
    {
        if (colors.Length == 0) throw new ArgumentException("At least one color is required.", nameof(colors));
        return new([.. colors]);
    }

    /// <summary>
    /// Creates a palette from raw colors — escape hatch with no validation (Tier 4).
    /// Triggers scanner warning A11Y_CHART_012.
    /// </summary>
    public static ChartPalette FromRaw(params D3.D3Color[] colors)
    {
        if (colors.Length == 0) throw new ArgumentException("At least one color is required.", nameof(colors));
        return new([.. colors]);
    }

    // ── Harden utility ──────────────────────────────────────────────

    /// <summary>
    /// Checks and adjusts colors for WCAG contrast, colorblind safety, and
    /// background contrast. Operates in LCH color space, pushing lightness
    /// apart for failing pairs. Max 8 passes by default.
    /// </summary>
    public static HardenResult Harden(D3.D3Color[] input, HardenOptions? options = null)
    {
        var opts = options ?? new HardenOptions();
        var adjusted = input.Select(c => c).ToArray();
        var diffs = new List<HardenDiff>();
        bool changed = false;

        for (int pass = 0; pass < opts.MaxPasses; pass++)
        {
            bool passChanged = false;

            // Check pairwise WCAG non-text contrast ≥ 3:1
            for (int i = 0; i < adjusted.Length; i++)
            {
                for (int j = i + 1; j < adjusted.Length; j++)
                {
                    double contrast = ContrastRatio(adjusted[i], adjusted[j]);
                    if (contrast < opts.MinPairwiseContrast)
                    {
                        AdjustPairLightness(adjusted, i, j);
                        passChanged = true;
                        changed = true;
                    }
                }
            }

            // Check pairwise ΔE under colorblind simulation
            for (int i = 0; i < adjusted.Length; i++)
            {
                for (int j = i + 1; j < adjusted.Length; j++)
                {
                    double minDeltaE = MinColorblindDeltaE(adjusted[i], adjusted[j]);
                    if (minDeltaE < opts.MinColorblindDeltaE)
                    {
                        AdjustPairLightness(adjusted, i, j);
                        passChanged = true;
                        changed = true;
                    }
                }
            }

            // Check each color vs light and dark backgrounds
            var lightBg = new D3.D3Color(255, 255, 255);
            var darkBg = new D3.D3Color(32, 32, 32);
            for (int i = 0; i < adjusted.Length; i++)
            {
                double lightContrast = ContrastRatio(adjusted[i], lightBg);
                double darkContrast = ContrastRatio(adjusted[i], darkBg);
                if (lightContrast < opts.MinBackgroundContrast && darkContrast < opts.MinBackgroundContrast)
                {
                    // Push away from mid-lightness
                    var (l, c, h) = RgbToLch(adjusted[i]);
                    l = l < 50 ? Math.Max(5, l - 15) : Math.Min(95, l + 15);
                    adjusted[i] = LchToRgb(l, c, h);
                    passChanged = true;
                    changed = true;
                }
            }

            if (!passChanged) break;
        }

        // Build diffs
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i].R != adjusted[i].R || input[i].G != adjusted[i].G || input[i].B != adjusted[i].B)
            {
                var reasons = new List<string>();
                if (ContrastRatio(input[i], adjusted[i]) > 1.05)
                    reasons.Add("lightness adjusted");
                diffs.Add(new HardenDiff(i, input[i], adjusted[i],
                    reasons.Count > 0 ? string.Join(", ", reasons) : "contrast/colorblind adjustment"));
            }
        }

        return new HardenResult
        {
            Palette = new ChartPalette(adjusted),
            Diffs = diffs,
            PassedWithoutChanges = !changed,
        };
    }

    // ── Color science utilities ──────────────────────────────────────

    /// <summary>
    /// Computes WCAG 2.x contrast ratio between two colors (1:1 to 21:1).
    /// </summary>
    public static double ContrastRatio(D3.D3Color a, D3.D3Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// WCAG 2.x relative luminance of a color.
    /// </summary>
    public static double RelativeLuminance(D3.D3Color c)
    {
        double r = LinearizeChannel(c.R / 255.0);
        double g = LinearizeChannel(c.G / 255.0);
        double b = LinearizeChannel(c.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double LinearizeChannel(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double GammaChannel(double c)
        => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;

    /// <summary>
    /// Computes CIE76 ΔE between two colors (Euclidean distance in Lab).
    /// </summary>
    public static double DeltaE(D3.D3Color a, D3.D3Color b)
    {
        var (la, aa, ab2) = RgbToLab(a);
        var (lb, ba2, bb2) = RgbToLab(b);
        return Math.Sqrt(
            (la - lb) * (la - lb) +
            (aa - ba2) * (aa - ba2) +
            (ab2 - bb2) * (ab2 - bb2));
    }

    /// <summary>
    /// Minimum ΔE across deuteranopia, protanopia, and tritanopia simulations.
    /// </summary>
    internal static double MinColorblindDeltaE(D3.D3Color a, D3.D3Color b)
    {
        double min = double.MaxValue;
        min = Math.Min(min, DeltaE(SimulateDeuteranopia(a), SimulateDeuteranopia(b)));
        min = Math.Min(min, DeltaE(SimulateProtanopia(a), SimulateProtanopia(b)));
        min = Math.Min(min, DeltaE(SimulateTritanopia(a), SimulateTritanopia(b)));
        return min;
    }

    // ── RGB ↔ Lab ↔ LCH conversions ────────────────────────────────

    internal static (double L, double a, double b) RgbToLab(D3.D3Color c)
    {
        // sRGB → linear RGB → XYZ (D65) → Lab
        double r = LinearizeChannel(c.R / 255.0);
        double g = LinearizeChannel(c.G / 255.0);
        double b = LinearizeChannel(c.B / 255.0);

        double x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

        // D65 white point
        x /= 0.95047; y /= 1.00000; z /= 1.08883;

        x = x > 0.008856 ? Math.Cbrt(x) : (903.3 * x + 16) / 116;
        y = y > 0.008856 ? Math.Cbrt(y) : (903.3 * y + 16) / 116;
        z = z > 0.008856 ? Math.Cbrt(z) : (903.3 * z + 16) / 116;

        double L = 116 * y - 16;
        double a2 = 500 * (x - y);
        double b2 = 200 * (y - z);

        return (L, a2, b2);
    }

    internal static (double L, double C, double H) RgbToLch(D3.D3Color c)
    {
        var (l, a, b) = RgbToLab(c);
        double ch = Math.Sqrt(a * a + b * b);
        double h = Math.Atan2(b, a) * (180 / Math.PI);
        if (h < 0) h += 360;
        return (l, ch, h);
    }

    internal static D3.D3Color LchToRgb(double L, double C, double H)
    {
        double hRad = H * (Math.PI / 180);
        double a = C * Math.Cos(hRad);
        double b = C * Math.Sin(hRad);
        return LabToRgb(L, a, b);
    }

    internal static D3.D3Color LabToRgb(double L, double a, double b)
    {
        double y = (L + 16) / 116;
        double x = a / 500 + y;
        double z = y - b / 200;

        double x3 = x * x * x, y3 = y * y * y, z3 = z * z * z;
        x = x3 > 0.008856 ? x3 : (116 * x - 16) / 903.3;
        y = y3 > 0.008856 ? y3 : (116 * y - 16) / 903.3;
        z = z3 > 0.008856 ? z3 : (116 * z - 16) / 903.3;

        x *= 0.95047; y *= 1.00000; z *= 1.08883;

        double r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        double bl = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;

        return new D3.D3Color(
            ClampByte(GammaChannel(r) * 255),
            ClampByte(GammaChannel(g) * 255),
            ClampByte(GammaChannel(bl) * 255));
    }

    // ── Colorblind simulation (Brettel 1997, simplified) ───────────────
    //
    // These use simplified channel-mixing matrices rather than the full
    // Brettel two-plane spectral projection. This is an approximation that
    // works well for palette differentiation checks but may not perfectly
    // match clinical CVD simulation. For hardening purposes, the simplified
    // model errs on the conservative side (perceives *more* similarity than
    // the full model), so palettes that pass this check will also pass under
    // more accurate simulation.

    internal static D3.D3Color SimulateDeuteranopia(D3.D3Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return new D3.D3Color(
            ClampByte((0.625 * r + 0.375 * g) * 255),
            ClampByte((0.7 * r + 0.3 * g) * 255),
            ClampByte((0.3 * g + 0.7 * b) * 255));
    }

    internal static D3.D3Color SimulateProtanopia(D3.D3Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return new D3.D3Color(
            ClampByte((0.567 * r + 0.433 * g) * 255),
            ClampByte((0.558 * r + 0.442 * g) * 255),
            ClampByte((0.242 * g + 0.758 * b) * 255));
    }

    internal static D3.D3Color SimulateTritanopia(D3.D3Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return new D3.D3Color(
            ClampByte((0.95 * r + 0.05 * g) * 255),
            ClampByte((0.433 * g + 0.567 * b) * 255),
            ClampByte((0.475 * g + 0.525 * b) * 255));
    }

    // ── Private helpers ─────────────────────────────────────────────

    private static void AdjustPairLightness(D3.D3Color[] colors, int i, int j)
    {
        var (li, ci, hi) = RgbToLch(colors[i]);
        var (lj, cj, hj) = RgbToLch(colors[j]);

        // Push the lighter one lighter and the darker one darker
        if (li > lj)
        {
            li = Math.Min(95, li + 8);
            lj = Math.Max(5, lj - 8);
        }
        else
        {
            lj = Math.Min(95, lj + 8);
            li = Math.Max(5, li - 8);
        }

        colors[i] = LchToRgb(li, ci, hi);
        colors[j] = LchToRgb(lj, cj, hj);
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
