using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Elements;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace OpenClawTray.Chat;

/// <summary>Shared native chat geometry. See docs/CHAT_VISUAL_DESIGN.md.</summary>
internal static class ChatVisuals
{
    internal const double ReadingWidth = 768;
    internal const double ProseInset = 16;
    internal const double ComposerRadius = 20;
    internal const double SurfaceRadius = 12;
    internal const double BodySize = 14;
    internal const double BodyLineHeight = 21;
    internal const string CodeFontFamily = "Consolas";
    internal const double CodeSize = 13;
    internal const double CodeLineHeight = 18;
    internal const double BlockGap = 16;
    internal const double TranscriptTopInset = 16;
    internal const double HeadingTopInset = 8;
    internal const double FooterBreakpoint = 640;
    internal const double CompactEffortBreakpoint = 560;
    internal const double CompactSessionBreakpoint = 400;
    internal const double AvatarBreakpoint = 960;
    internal const double AvatarSlot = 44;

    internal static double Gutter(double width) => width < FooterBreakpoint ? 12 : 40;
    internal static double RailWidth(double width) => width < FooterBreakpoint ? 24 : 32;
    internal static double ProseWidth(double width) =>
        Math.Min(ReadingWidth - 2 * ProseInset, width - 2 * (Gutter(width) + ProseInset));

    internal static void ToolbarButtonResources(ResourceBuilder resources) =>
        resources
            .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
            .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
            .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush"));

    // Native setters reapply presentation after Reactor's defaults on every update.
    internal static void StylePicker(Flyout flyout)
    {
        flyout.FlyoutPresenterStyle = (Style)Application.Current.Resources["ChatPickerFlyoutStyle"];
        flyout.Placement = FlyoutPlacementMode.TopEdgeAlignedRight;
    }

    internal static void StylePicker(MenuFlyout menu)
    {
        menu.MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["ChatPickerMenuStyle"];
        menu.Placement = FlyoutPlacementMode.TopEdgeAlignedRight;
    }

    internal static void StylePickerAccent(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["ChatPickerAccentTextStyle"];

    private static readonly ConditionalWeakTable<FrameworkElement, SizeChangedEventHandler> Observers = new();

    internal static void Observe(FrameworkElement element, Action<double> changed)
    {
        SizeChangedEventHandler handler = (_, args) =>
        {
            if (args.NewSize.Width > 0)
                changed(args.NewSize.Width);
        };
        Observers.Add(element, handler);
        element.SizeChanged += handler;
        if (element.ActualWidth > 0)
            changed(element.ActualWidth);
    }

    internal static void StopObserving(FrameworkElement element)
    {
        if (Observers.TryGetValue(element, out var handler))
        {
            element.SizeChanged -= handler;
            Observers.Remove(element);
        }
    }
}
