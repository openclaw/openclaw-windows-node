using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Dashboards;

/// <summary>
/// The dashboards palette from <c>Themes/DashboardResources.xaml</c>. Hues are
/// identity, never state: state keeps the Fluent success/caution/critical
/// brushes, and actions keep the system accent.
/// </summary>
internal static class DashboardHues
{
    public const int Count = 6;
    public const int Blue = 0;
    public const int Teal = 1;
    public const int Purple = 2;
    public const int Pumpkin = 3;
    public const int Green = 5;

    public static string Strong(int hue) => $"DashboardHue{Normalize(hue)}Brush";
    public static string Subtle(int hue) => $"DashboardHue{Normalize(hue)}SubtleBrush";
    public static string Wash(int hue) => $"DashboardHue{Normalize(hue)}WashBrush";
    public const string OnHue = "DashboardOnHueBrush";
    public const string Hero = "DashboardHeroBrush";

    /// <summary>Stable identity hue for a dashboard or author: the same key always gets the same color.</summary>
    public static int For(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return Blue;
        var hash = 2166136261u;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return (int)(hash % Count);
    }

    public static int ForKind(BoardWidget widget) => widget.ContentKind switch
    {
        BoardWidgetContentKind.Html => Blue,
        BoardWidgetContentKind.McpApp => Pumpkin,
        _ => widget.PluginKind switch
        {
            BoardWidgetBodyPolicy.ReportKind => Purple,
            BoardWidgetBodyPolicy.WebsiteKind => Teal,
            _ => Green,
        },
    };

    private static int Normalize(int hue) => ((hue % Count) + Count) % Count;
}

internal static class DashboardsGlyphsExtra
{
    public const string Code = "\uE943";
    public const string Report = "\uE9F9";
    public const string Globe = "\uE774";
    public const string Apps = "\uECA5";
    public const string Puzzle = "\uEA86";
    public const string Stopwatch = "\uE916";
    public const string Sparkle = "\uE734";
    public const string Lock = "\uE72E";
    public const string Clock = "\uE823";
    public const string ArrowRight = "\uE72A";
    public const string Wrench = "\uE90F";

    public static string ForKind(BoardWidget widget) => widget.ContentKind switch
    {
        BoardWidgetContentKind.Html => Code,
        BoardWidgetContentKind.McpApp => Apps,
        _ => widget.PluginKind switch
        {
            BoardWidgetBodyPolicy.ReportKind => Report,
            BoardWidgetBodyPolicy.WebsiteKind => Globe,
            BoardWidgetBodyPolicy.ProgressKind => Stopwatch,
            _ => Puzzle,
        },
    };
}

/// <summary>Decorative building blocks with personality: monograms, glyph badges, illustrations, live halo.</summary>
internal static class DashboardsArt
{
    /// <summary>Rounded tile with a tinted wash and the first letter in the identity hue.</summary>
    public static Element Monogram(string text, int hue, double size)
    {
        var letter = string.IsNullOrWhiteSpace(text) ? "?" : text.Trim()[..1].ToUpperInvariant();
        return Border(TextBlock(letter)
                .FontSize(Math.Round(size * 0.46))
                .SemiBold()
                .Foreground(Theme.Ref(DashboardHues.Strong(hue)))
                .Center())
            .Size(size, size)
            .CornerRadius(Math.Round(size * 0.28))
            .Background(Theme.Ref(DashboardHues.Subtle(hue)))
            .AccessibilityView(AccessibilityView.Raw);
    }

    /// <summary>Circular avatar filled with the hue; text uses the matching on-hue brush.</summary>
    public static Element Avatar(string initial, int hue, double size) =>
        Border(TextBlock(initial).FontSize(Math.Round(size * 0.48)).SemiBold()
                .Foreground(Theme.Ref(DashboardHues.OnHue)).Center())
            .Size(size, size).CornerRadius(size / 2)
            .Background(Theme.Ref(DashboardHues.Strong(hue)))
            .AccessibilityView(AccessibilityView.Raw);

    /// <summary>Glyph centered on a soft tinted disc.</summary>
    public static Element GlyphBadge(string glyph, string foregroundKey, string backgroundKey, double size) =>
        Border(DashboardsStates.Glyph(glyph, Math.Round(size * 0.45), foregroundKey).Center())
            .Size(size, size).CornerRadius(size / 2)
            .Background(Theme.Ref(backgroundKey))
            .AccessibilityView(AccessibilityView.Raw);

    public static Element HueBadge(string glyph, int hue, double size) =>
        GlyphBadge(glyph, DashboardHues.Strong(hue), DashboardHues.Subtle(hue), size);

    /// <summary>Kind chip: a colored glyph and the kind label on a tinted pill.</summary>
    public static Element KindChip(BoardWidget widget, string label)
    {
        var hue = DashboardHues.ForKind(widget);
        return Border(HStack(5,
                DashboardsStates.Glyph(DashboardsGlyphsExtra.ForKind(widget), 11, DashboardHues.Strong(hue))
                    .VAlign(VerticalAlignment.Center),
                TextBlock(label).FontSize(11).SemiBold()
                    .Foreground(Theme.Ref("TextFillColorPrimaryBrush"))
                    .VAlign(VerticalAlignment.Center)))
            .Padding(7, 2, 8, 3)
            .CornerRadius(999)
            .Background(Theme.Ref(DashboardHues.Subtle(hue)));
    }

    /// <summary>A green dot with a soft breathing halo.</summary>
    public static Element LiveDot(bool reducedMotion)
    {
        var halo = Border(Empty()).Size(12, 12).CornerRadius(6)
            .Background(Theme.Ref("SystemFillColorSuccessBrush"))
            .Opacity(reducedMotion ? 0.25 : 0.4);
        if (!reducedMotion)
        {
            halo = halo.Keyframes("live-halo", 0, kf => kf
                .Duration(1800).Loop()
                .At(0f, opacity: 0.5f)
                .At(0.5f, opacity: 0.05f, easing: Easing.Decelerate)
                .At(1f, opacity: 0.5f, easing: Easing.Accelerate));
        }
        return Grid([GridSize.Px(12)], [GridSize.Px(12)],
                halo,
                Border(Empty()).Size(6, 6).CornerRadius(3)
                    .Background(Theme.Ref("SystemFillColorSuccessBrush"))
                    .Center())
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw);
    }

    /// <summary>Live pill: the breathing dot and the localized "Live" label.</summary>
    public static Element LiveBadge(bool reducedMotion) =>
        Border(HStack(4,
                LiveDot(reducedMotion),
                TextBlock(LocalizationHelper.GetString("Dashboards_Live")).FontSize(11).SemiBold()
                    .Foreground(Theme.Ref("TextFillColorPrimaryBrush"))
                    .VAlign(VerticalAlignment.Center)))
            .Padding(5, 2, 9, 2)
            .CornerRadius(999)
            .Background(Theme.Ref("SystemFillColorSuccessBackgroundBrush"))
            .VAlign(VerticalAlignment.Center)
            .AutomationName(LocalizationHelper.GetString("Dashboards_Live"));

    /// <summary>
    /// A small mosaic of colored widget tiles: the dashboards illustration used
    /// by the hero, empty gallery, and empty board. Tiles drift gently unless
    /// the user prefers reduced motion.
    /// </summary>
    public static Element Mosaic(double scale, bool reducedMotion)
    {
        Element Tile(double x, double y, double w, double h, int hue, string glyph, int period, float lift)
        {
            Element tile = Border(DashboardsStates.Glyph(glyph, Math.Round(Math.Min(w, h) * 0.38), DashboardHues.Strong(hue)).Center())
                .Size(w * scale, h * scale)
                .CornerRadius(10 * scale)
                .Background(Theme.Ref(DashboardHues.Subtle(hue)))
                .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                .BorderThickness(1);
            if (!reducedMotion)
            {
                tile = tile.Keyframes("drift", 0, kf => kf
                    .Duration(period).Loop()
                    .At(0f, translation: new System.Numerics.Vector3(0, 0, 0))
                    .At(0.5f, translation: new System.Numerics.Vector3(0, -lift * (float)scale, 0), easing: Easing.Decelerate)
                    .At(1f, translation: new System.Numerics.Vector3(0, 0, 0), easing: Easing.Accelerate));
            }
            return tile.Canvas(x * scale, y * scale);
        }
        return Canvas(
                Tile(0, 22, 58, 46, DashboardHues.Purple, DashboardsGlyphsExtra.Report, 4200, 3),
                Tile(64, 6, 44, 36, DashboardHues.Blue, DashboardsGlyphsExtra.Code, 3600, 4),
                Tile(64, 48, 70, 42, DashboardHues.Teal, DashboardsGlyphsExtra.Globe, 4800, 3),
                Tile(114, 0, 34, 34, DashboardHues.Pumpkin, DashboardsGlyphsExtra.Sparkle, 3000, 5))
            .Size(150 * scale, 96 * scale)
            .AccessibilityView(AccessibilityView.Raw);
    }

    /// <summary>Card-entrance motion: fade and rise. Skipped when motion is reduced.</summary>
    public static T Enter<T>(T element, bool reducedMotion) where T : Element =>
        reducedMotion ? element : element.Transition(Microsoft.UI.Reactor.Animation.Transition.Fade + Microsoft.UI.Reactor.Animation.Transition.Slide(Edge.Bottom));
}
