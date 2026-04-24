using OpenClawTray.Infrastructure.Charting.D3;
// Declarative D3 drawing DSL for Reactor's virtual tree.
// Replaces imperative G.AddRect/AddLine/AddEllipse/AddText/MakePath patterns
// with composable Reactor Elements that work with the reconciler.
//
// Usage: using static OpenClawTray.Infrastructure.Charting.D3Dsl;

using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Charting;

/// <summary>
/// Static factory methods for declarative D3 chart drawing.
/// Import with: using static OpenClawTray.Infrastructure.Charting.D3Dsl;
/// </summary>
public static class D3Dsl
{
    // ── Color helpers ───────────────────────────────────────────────────

    public static readonly IReadOnlyList<D3Color> Palette = D3Color.Category10;

    public static SolidColorBrush Brush(string color)
    {
        var c = D3Color.Parse(color);
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb((byte)(c.Opacity * 255), c.R, c.G, c.B));
    }

    public static SolidColorBrush Brush(D3Color c)
        => new(global::Windows.UI.Color.FromArgb((byte)(c.Opacity * 255), c.R, c.G, c.B));

    public static SolidColorBrush Brush(D3Color c, double opacity)
        => new(global::Windows.UI.Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));

    public static SolidColorBrush Brush(string color, double opacity)
        => Brush(D3Color.Parse(color), opacity);

    public static SolidColorBrush Gray(byte v, byte alpha = 255)
        => new(global::Windows.UI.Color.FromArgb(alpha, v, v, v));

    // ── Theme-aware chart chrome brushes ──────────────────────────────
    //
    // Charts draw axes, gridlines, labels and titles that need enough contrast
    // on both light and dark surfaces. Host applications set IsDarkTheme once
    // per render (e.g. from the app's light/dark toggle) and all chart helpers
    // — and any user code using ChartForeground/ChartAxis/etc. — pick the right
    // brush automatically.

    [ThreadStatic] private static bool _isDarkTheme;

    public static bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => _isDarkTheme = value;
    }

    // ── Forced-colors (high-contrast) mode ───────────────────────────
    //
    // When the system is in High Contrast / forced-colors mode, charts must
    // use system colors rather than their normal palette. The host sets this
    // flag alongside IsDarkTheme so all chart helpers automatically adapt.
    //
    // NOTE: IsForcedColors and IsReducedMotion are [ThreadStatic] — consistent
    // with the existing IsDarkTheme pattern. All chart rendering must occur on
    // the thread that called DoRender(). If chart callbacks dispatch to thread
    // pool threads, those threads will see default (false) values.

    [ThreadStatic] private static bool _isForcedColors;

    /// <summary>
    /// When true, chart brushes use system high-contrast colors instead of
    /// the normal palette. Set automatically by the host when
    /// <c>AccessibilitySettings.HighContrast</c> is active.
    /// </summary>
    public static bool IsForcedColors
    {
        get => _isForcedColors;
        set => _isForcedColors = value;
    }

    [ThreadStatic] private static bool _isReducedMotion;

    /// <summary>
    /// When true, chart entrance/exit animations snap to final state, pan inertia
    /// is disabled, and force-graph simulation terminates immediately. Set
    /// automatically by the host from <c>UISettings.AnimationsEnabled</c>.
    /// </summary>
    public static bool IsReducedMotion
    {
        get => _isReducedMotion;
        set => _isReducedMotion = value;
    }

    // ── System high-contrast color mapping ───────────────────────────

    [ThreadStatic] private static ForcedColorsTheme? _forcedColorsTheme;

    /// <summary>
    /// System high-contrast colors queried from <c>UISettings.GetColorValue</c>.
    /// Set automatically by the host each render cycle. When null, falls back to
    /// safe defaults (white foreground, black background).
    /// </summary>
    public static ForcedColorsTheme? ForcedColors
    {
        get => _forcedColorsTheme;
        set => _forcedColorsTheme = value;
    }

    /// <summary>
    /// Returns the brush for a chart series. In forced-colors mode, returns
    /// system high-contrast colors; otherwise returns the normal palette color.
    /// </summary>
    public static SolidColorBrush ChartSeries(int seriesIndex)
    {
        if (_isForcedColors)
        {
            var fc = _forcedColorsTheme ?? ForcedColorsTheme.Default;
            var color = fc.SeriesColors[((seriesIndex % fc.SeriesColors.Length) + fc.SeriesColors.Length) % fc.SeriesColors.Length];
            return new SolidColorBrush(color);
        }
        return Brush(Palette[seriesIndex % Palette.Count]);
    }

    /// <summary>
    /// Returns the dash pattern for a chart series from the default cycle.
    /// </summary>
    public static Accessibility.DashStyle ChartSeriesDash(int seriesIndex) =>
        Accessibility.ChartPalette.DefaultDashCycle[
            ((seriesIndex % Accessibility.ChartPalette.DefaultDashCycle.Length) + Accessibility.ChartPalette.DefaultDashCycle.Length)
            % Accessibility.ChartPalette.DefaultDashCycle.Length];

    /// <summary>
    /// Returns the marker shape for a chart series from the default cycle.
    /// </summary>
    public static Accessibility.MarkerShape ChartSeriesMarker(int seriesIndex) =>
        Accessibility.ChartPalette.DefaultMarkerCycle[
            ((seriesIndex % Accessibility.ChartPalette.DefaultMarkerCycle.Length) + Accessibility.ChartPalette.DefaultMarkerCycle.Length)
            % Accessibility.ChartPalette.DefaultMarkerCycle.Length];

    /// <summary>Primary text on a chart surface — titles, strong labels.</summary>
    public static SolidColorBrush ChartForeground =>
        _isForcedColors ? new SolidColorBrush((_forcedColorsTheme ?? ForcedColorsTheme.Default).Foreground)
        : _isDarkTheme ? Gray(235) : Gray(40);

    /// <summary>Secondary text — tick labels, subtle annotations, legend labels.</summary>
    public static SolidColorBrush ChartMutedForeground =>
        _isForcedColors ? new SolidColorBrush((_forcedColorsTheme ?? ForcedColorsTheme.Default).Foreground)
        : _isDarkTheme ? Gray(190) : Gray(90);

    /// <summary>Axis lines + their tick labels.</summary>
    public static SolidColorBrush ChartAxis =>
        _isForcedColors ? new SolidColorBrush((_forcedColorsTheme ?? ForcedColorsTheme.Default).Foreground)
        : _isDarkTheme ? Gray(190, 200) : Gray(100, 180);

    /// <summary>Subtle horizontal/vertical gridlines behind the plot.</summary>
    public static SolidColorBrush ChartGrid =>
        _isForcedColors ? new SolidColorBrush((_forcedColorsTheme ?? ForcedColorsTheme.Default).Foreground)
        : _isDarkTheme ? Gray(200, 50) : Gray(128, 50);

    /// <summary>Solid fill matching the chart's surrounding card — use for gap strokes between colored slices (pie / sunburst / icicle).</summary>
    public static SolidColorBrush ChartSurface =>
        _isDarkTheme ? Gray(32) : Gray(255);

    /// <summary>Translucent surface — for layered ridge fills or separators that blend with the card.</summary>
    public static SolidColorBrush ChartSurfaceAlpha(byte alpha) =>
        _isDarkTheme ? Gray(32, alpha) : Gray(255, alpha);

    /// <summary>Slightly elevated neutral surface — non-leaf tree fills, alternating rows.</summary>
    public static SolidColorBrush ChartSubtleFill =>
        _isDarkTheme ? Gray(60) : Gray(225);

    /// <summary>Subtle neutral stroke — baselines, separators, non-accented borders.</summary>
    public static SolidColorBrush ChartSubtleStroke =>
        _isDarkTheme ? Gray(90) : Gray(185);

    /// <summary>Selection highlight brush — Highlight in forced-colors, blue otherwise.</summary>
    public static SolidColorBrush ChartSelection =>
        _isForcedColors ? new SolidColorBrush((_forcedColorsTheme ?? ForcedColorsTheme.Default).Highlight)
        : Brush("#4285f4");

    /// <summary>Selection text — HighlightText in forced-colors, white otherwise.</summary>
    public static SolidColorBrush ChartSelectionText =>
        _isForcedColors ? new SolidColorBrush((_forcedColorsTheme ?? ForcedColorsTheme.Default).HighlightText)
        : new SolidColorBrush(Microsoft.UI.Colors.White);

    /// <summary>Disabled element brush — GrayText in forced-colors, gray otherwise.</summary>
    public static SolidColorBrush ChartDisabled =>
        _isForcedColors ? new SolidColorBrush((_forcedColorsTheme ?? ForcedColorsTheme.Default).GrayText)
        : Gray(160);

    public static string Fmt(double v) =>
        Math.Abs(v) >= 1e6 ? (v / 1e6).ToString("0.#", global::System.Globalization.CultureInfo.InvariantCulture) + "M" :
        Math.Abs(v) >= 1e3 ? (v / 1e3).ToString("0.#", global::System.Globalization.CultureInfo.InvariantCulture) + "k" :
        v == Math.Floor(v) ? v.ToString("F0", global::System.Globalization.CultureInfo.InvariantCulture) : v.ToString("G4", global::System.Globalization.CultureInfo.InvariantCulture);

    // ── Canvas ──────────────────────────────────────────────────────────

    /// <summary>Creates a Canvas element with the given dimensions and children.</summary>
    public static CanvasElement D3Canvas(double width, double height, params Element?[] children) =>
        Canvas(children) with
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

    // ── Primitive shapes ────────────────────────────────────────────────

    /// <summary>Creates a positioned rectangle on a Canvas.</summary>
    public static RectangleElement D3Rect(double x, double y, double width, double height) =>
        new RectangleElement()
            .Width(Math.Max(0, width)).Height(Math.Max(0, height))
            .Canvas(x, y);

    /// <summary>Creates a circle (ellipse) positioned at center (cx, cy) with radius r.</summary>
    public static EllipseElement D3Circle(double cx, double cy, double r) =>
        new EllipseElement()
            .Width(r * 2).Height(r * 2)
            .Canvas(cx - r, cy - r);

    /// <summary>Creates a line between two points.</summary>
    public static LineElement D3Line(double x1, double y1, double x2, double y2) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>Creates a path from SVG path data string.  Accepts null pathData gracefully (renders nothing).</summary>
    public static PathElement D3Path(string? pathData, Brush? stroke = null, Brush? fill = null, double strokeWidth = 1.5) =>
        new()
        {
            Data = pathData != null ? PathDataParser.Parse(pathData) : null,
            PathDataString = pathData,
            Stroke = stroke,
            Fill = fill,
            StrokeThickness = strokeWidth,
        };

    /// <summary>Creates a path from SVG path data with a translate transform.  Accepts null pathData gracefully (renders nothing).</summary>
    public static PathElement D3PathTranslated(string? pathData, double translateX, double translateY, Brush? stroke = null, Brush? fill = null, double strokeWidth = 1.5) =>
        new()
        {
            Data = pathData != null ? PathDataParser.Parse(pathData) : null,
            PathDataString = pathData,
            Stroke = stroke,
            Fill = fill,
            StrokeThickness = strokeWidth,
            RenderTransform = new TranslateTransform { X = translateX, Y = translateY },
        };

    // ── Text ────────────────────────────────────────────────────────────

    /// <summary>Creates a positioned text label on a Canvas.</summary>
    public static TextBlockElement Text(double x, double y, string text, double fontSize = 10, Brush? foreground = null) =>
        TextBlock(text)
            .FontSize(fontSize)
            .Foreground(foreground ?? ChartMutedForeground)
            .Canvas(x, y);

    /// <summary>Creates a positioned text label with right alignment and explicit width (for Y axis labels).</summary>
    public static TextBlockElement TextRight(double x, double y, string text, double width, double fontSize = 10, Brush? foreground = null) =>
        TextBlock(text)
            .FontSize(fontSize)
            .Foreground(foreground ?? ChartMutedForeground)
            .Width(width)
            .TextAlignment(TextAlignment.Right)
            .Canvas(x, y);

    /// <summary>Creates a positioned text label with center alignment and explicit width.</summary>
    public static TextBlockElement TextCenter(double x, double y, string text, double width, double fontSize = 10, Brush? foreground = null) =>
        TextBlock(text)
            .FontSize(fontSize)
            .Foreground(foreground ?? ChartMutedForeground)
            .Width(width)
            .TextAlignment(TextAlignment.Center)
            .Canvas(x, y);

    // ── Generator helpers (functional one-shot wrappers) ──────────────

    /// <summary>Creates a line path element directly from data, collapsing LineGenerator + Generate + D3Path into one expression.</summary>
    public static PathElement D3LinePath<T>(IReadOnlyList<T> data, Func<T, double> x, Func<T, double> y,
        Brush? stroke = null, double strokeWidth = 1.5,
        CurveFactory? curve = null, Func<T, int, bool>? defined = null)
    {
        var gen = LineGenerator.Create(x, y);
        if (curve != null) gen.SetCurve(curve);
        if (defined != null) gen.SetDefined(defined);
        return D3Path(gen.Generate(data), stroke: stroke, strokeWidth: strokeWidth);
    }

    /// <summary>Creates an area path element directly from data, collapsing AreaGenerator + Generate + D3Path into one expression.</summary>
    public static PathElement D3AreaPath<T>(IReadOnlyList<T> data, Func<T, double> x, Func<T, double> y0, Func<T, double> y1,
        Brush? fill = null, Brush? stroke = null, double strokeWidth = 1.5)
    {
        var gen = AreaGenerator.Create(x, y0, y1);
        return D3Path(gen.Generate(data), stroke: stroke, fill: fill, strokeWidth: strokeWidth);
    }

    /// <summary>Creates an arc sector path element at (cx, cy), collapsing ArcGenerator + Generate + D3PathTranslated into one expression.</summary>
    public static PathElement D3ArcPath(double startAngle, double endAngle, double cx, double cy,
        double innerRadius = 0, double outerRadius = 100,
        double padAngle = 0, Brush? fill = null, Brush? stroke = null, double strokeWidth = 1.5)
    {
        var pathData = new ArcGenerator()
            .SetInnerRadius(innerRadius)
            .SetOuterRadius(outerRadius)
            .Generate(startAngle, endAngle, padAngle);
        return D3PathTranslated(pathData, cx, cy, stroke: stroke, fill: fill, strokeWidth: strokeWidth);
    }

    /// <summary>Creates pie/donut slice elements directly from data, collapsing PieGenerator + ArcGenerator + iteration into one expression.</summary>
    public static Element[] D3Pie<T>(IReadOnlyList<T> data, Func<T, double> value, double cx, double cy,
        double outerRadius = 150, double innerRadius = 0,
        double padAngle = 0, bool sort = true,
        Brush? stroke = null, double strokeWidth = 1.5)
    {
        var arcs = PieGenerator.Generate(data, value, sort, padAngle);
        var arc = new ArcGenerator().SetOuterRadius(outerRadius).SetInnerRadius(innerRadius);
        return arcs.Select((a, i) =>
            (Element)D3PathTranslated(arc.Generate(a), cx, cy,
                fill: Brush(Palette[i % Palette.Count]),
                stroke: stroke,
                strokeWidth: strokeWidth)
        ).ToArray();
    }

    // ── Composite chart helpers ─────────────────────────────────────────

    /// <summary>Creates a vertical bezier tree link path between two points (parent to child).</summary>
    public static PathElement D3Link(double x1, double y1, double x2, double y2, Brush? stroke = null, double strokeWidth = 1.5)
    {
        double my = (y1 + y2) / 2;
        var pb = new PathBuilder(3);
        pb.MoveTo(x1, y1);
        pb.BezierCurveTo(x1, my, x2, my, x2, y2);
        return D3Path(pb.ToString(), stroke, null, strokeWidth);
    }

    /// <summary>Creates a legend as rect+text pairs laid out vertically.</summary>
    public static Element[] D3Legend(double x, double y, IEnumerable<(string label, SolidColorBrush color)> items, double fontSize = 11)
    {
        return items.SelectMany((item, i) => new Element[]
        {
            D3Rect(x, y + i * 22, 14, 14) with { Fill = item.color, RadiusX = 2, RadiusY = 2 },
            Text(x + 20, y + i * 22, item.label, fontSize, ChartMutedForeground),
        }).ToArray();
    }


    /// <summary>Creates X and Y axis lines with tick labels as a flat array of Elements.</summary>
    public static Element[] D3Axes(LinearScale xs, LinearScale ys,
        double left, double top, double width, double height, int xTicks = 6, int yTicks = 5)
    {
        var ab = ChartAxis;
        double bot = top + height;
        var elements = new List<Element>
        {
            (D3Line(left, bot, left + width, bot) with { Stroke = ab, StrokeThickness = 1 })
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw),
            (D3Line(left, top, left, bot) with { Stroke = ab, StrokeThickness = 1 })
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw),
        };

        foreach (var t in xs.Ticks(xTicks))
            elements.Add(Text(xs.Map(t) - 12, bot + 4, Fmt(t), 10, ab)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw));

        foreach (var t in ys.Ticks(yTicks))
            elements.Add(TextRight(0, ys.Map(t) - 7, Fmt(t), left - 6, 10, ab)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw));

        return elements.ToArray();
    }

    /// <summary>Creates horizontal grid lines as a flat array of Elements. Auto-set to AccessibilityView.Raw.</summary>
    public static Element[] D3Grid(LinearScale ys, double left, double width, int ticks = 5)
    {
        var gb = ChartGrid;
        return ys.Ticks(ticks)
            .Select(t => (Element)(D3Line(left, ys.Map(t), left + width, ys.Map(t)) with { Stroke = gb, StrokeThickness = 1 })
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw))
            .ToArray();
    }
}

/// <summary>
/// Holds the actual system high-contrast colors queried from
/// <see cref="global::Windows.UI.ViewManagement.UISettings.GetColorValue"/>.
/// The host populates this each render cycle so chart brushes adapt to the
/// user's chosen high-contrast theme (including custom themes).
/// </summary>
public sealed class ForcedColorsTheme
{
    /// <summary>System foreground (CanvasText / WindowText).</summary>
    public global::Windows.UI.Color Foreground { get; init; }

    /// <summary>System background (Canvas / Window).</summary>
    public global::Windows.UI.Color Background { get; init; }

    /// <summary>System highlight / accent.</summary>
    public global::Windows.UI.Color Highlight { get; init; }

    /// <summary>Text on highlighted background.</summary>
    public global::Windows.UI.Color HighlightText { get; init; }

    /// <summary>Disabled / secondary text (GrayText).</summary>
    public global::Windows.UI.Color GrayText { get; init; }

    /// <summary>Hyperlink text color.</summary>
    public global::Windows.UI.Color Hotlight { get; init; }

    /// <summary>
    /// Distinct colors for chart series, derived from system HC colors.
    /// Cycles through Foreground, Highlight, Hotlight, GrayText.
    /// </summary>
    public global::Windows.UI.Color[] SeriesColors { get; init; } = [];

    /// <summary>
    /// Safe fallback when system colors cannot be queried (headless / unit tests).
    /// Uses standard HC Black theme colors.
    /// </summary>
    public static readonly ForcedColorsTheme Default = new()
    {
        Foreground = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
        Background = global::Windows.UI.Color.FromArgb(255, 0, 0, 0),
        Highlight = global::Windows.UI.Color.FromArgb(255, 0, 255, 255),
        HighlightText = global::Windows.UI.Color.FromArgb(255, 0, 0, 0),
        GrayText = global::Windows.UI.Color.FromArgb(255, 128, 128, 128),
        Hotlight = global::Windows.UI.Color.FromArgb(255, 255, 255, 0),
        // Series colors maximize visual distinction on black HC backgrounds.
        // Green (not GrayText gray) is used for series[3] for better contrast.
        SeriesColors =
        [
            global::Windows.UI.Color.FromArgb(255, 255, 255, 255), // White (Foreground)
            global::Windows.UI.Color.FromArgb(255, 0, 255, 255),   // Cyan (Highlight)
            global::Windows.UI.Color.FromArgb(255, 255, 255, 0),   // Yellow (Hotlight)
            global::Windows.UI.Color.FromArgb(255, 0, 128, 0),     // Green (distinct from gray)
        ],
    };

    /// <summary>
    /// Queries the current system high-contrast colors from <see cref="global::Windows.UI.ViewManagement.UISettings"/>.
    /// Returns <see cref="Default"/> if the query fails (headless environment).
    /// </summary>
    public static ForcedColorsTheme FromSystem()
    {
        try
        {
            var s = new global::Windows.UI.ViewManagement.UISettings();
            var fg = s.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground);
            var bg = s.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Background);
            var accent = s.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            var accentDark = s.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentDark1);
            var accentLight = s.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight1);
            // Complement is not always available; fall back to GrayText-like dim foreground
            var grayText = global::Windows.UI.Color.FromArgb(255,
                (byte)(fg.R / 2 + bg.R / 2),
                (byte)(fg.G / 2 + bg.G / 2),
                (byte)(fg.B / 2 + bg.B / 2));

            return new ForcedColorsTheme
            {
                Foreground = fg,
                Background = bg,
                Highlight = accent,
                HighlightText = bg, // text on accent is typically background color
                GrayText = grayText,
                Hotlight = accentLight,
                SeriesColors = [fg, accent, accentLight, grayText],
            };
        }
        catch
        {
            return Default;
        }
    }
}
