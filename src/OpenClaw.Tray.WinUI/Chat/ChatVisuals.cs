using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Elements;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OpenClawTray.Chat;

/// <summary>Shared native chat geometry. See docs/CHAT_VISUAL_DESIGN.md.</summary>
internal static class ChatVisuals
{
    private sealed record ShimmerState(Storyboard Storyboard, object OriginalLocal, Brush Shimmer);
    private static readonly ConditionalWeakTable<TextBlock, ShimmerState> s_shimmers = new();

    /// <summary>
    /// The web's running-text shimmer (<c>text-shimmer</c>): a brighter band sweeps
    /// across the label while a tool, thinking or run is active. Windows animation
    /// off or high contrast keeps static text, like the web's reduced-motion and
    /// forced-colors rules. Call from a Reactor <c>.Set</c> on every update.
    /// </summary>
    internal static void TextShimmer(TextBlock text, bool active)
    {
        s_shimmers.TryGetValue(text, out var state);
        var enabled = active &&
            new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled &&
            !new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        if (!enabled)
        {
            if (state is not null)
                StopShimmer(text, state);
            return;
        }
        if (state is not null)
        {
            // Style setters may clear Foreground on update; keep the sweep visible.
            if (!ReferenceEquals(text.Foreground, state.Shimmer))
                text.Foreground = state.Shimmer;
            return;
        }

        var isDark = text.ActualTheme == ElementTheme.Dark;
        if (ThemeRef.Resolve("TextFillColorSecondaryBrush", isDark) is not SolidColorBrush rest ||
            ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not SolidColorBrush peak)
            return;
        var lead = new GradientStop { Color = rest.Color, Offset = -0.3 };
        var band = new GradientStop { Color = peak.Color, Offset = -0.15 };
        var tail = new GradientStop { Color = rest.Color, Offset = 0 };
        var brush = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
        brush.GradientStops.Add(lead);
        brush.GradientStops.Add(band);
        brush.GradientStops.Add(tail);

        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var stop in new[] { lead, band, tail })
        {
            var sweep = new DoubleAnimation
            {
                From = stop.Offset,
                To = stop.Offset + 1.6,
                Duration = TimeSpan.FromMilliseconds(2200),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(sweep, stop);
            Storyboard.SetTargetProperty(sweep, "Offset");
            storyboard.Children.Add(sweep);
        }

        var original = text.ReadLocalValue(TextBlock.ForegroundProperty);
        text.Foreground = brush;
        s_shimmers.AddOrUpdate(text, new ShimmerState(storyboard, original, brush));
        text.Unloaded -= OnShimmerUnloaded;
        text.Unloaded += OnShimmerUnloaded;
        storyboard.Begin();
    }

    private static void OnShimmerUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock text && s_shimmers.TryGetValue(text, out var state))
            StopShimmer(text, state);
    }

    private static void StopShimmer(TextBlock text, ShimmerState state)
    {
        state.Storyboard.Stop();
        if (ReferenceEquals(text.ReadLocalValue(TextBlock.ForegroundProperty), state.Shimmer))
        {
            if (state.OriginalLocal == DependencyProperty.UnsetValue)
                text.ClearValue(TextBlock.ForegroundProperty);
            else
                text.SetValue(TextBlock.ForegroundProperty, state.OriginalLocal);
        }
        s_shimmers.Remove(text);
    }
    internal const double ReadingWidth = 768;
    internal const double ProseInset = 16;
    internal const double ComposerRadius = 20;
    internal const double ComposerMinHeight = 96;
    internal const double SurfaceRadius = 12;
    internal const double BodySize = 14;
    internal const double BodyLineHeight = 21;
    internal const string CodeFontFamily = "Consolas";
    internal const double CodeSize = 13;
    internal const double CodeLineHeight = 18;
    internal const double BlockGap = 8;
    internal const double CompactGap = 4;
    internal const double TurnGap = 4;
    internal const double DetailHorizontalInset = 12;
    internal const double DetailVerticalInset = 8;
    internal const double ActivityDetailsMaxHeight = 160;
    internal const double TranscriptTopInset = 8;
    internal const double TranscriptBottomInset = 24;
    internal const double HeadingTopInset = 8;
    internal const double FooterBreakpoint = 640;
    internal const double CompactEffortBreakpoint = 560;
    internal const double AvatarBreakpoint = 960;
    internal const double AvatarSlot = 44;

    internal static double WelcomeAvatarSize(double width) => width <= 768 ? 64 : 96;
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

    /// <summary>
    /// Transparent, borderless (in every header state) tool-row Expander header whose built-in
    /// chevron is hidden; the row's leading icon swaps to a chevron on hover or keyboard focus instead.
    /// </summary>
    internal static void ToolRowExpanderResources(ResourceBuilder resources)
    {
        resources
            .Set("ExpanderHeaderBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ExpanderChevronButtonSize", 0d);
        foreach (var key in s_transparentToolRowBrushKeys)
            resources.Set(key, Theme.Ref("SubtleFillColorTransparentBrush"));
    }

    /// <summary>
    /// Removes the Expander's built-in header inset so a tool row's icon starts on the
    /// prose edge and its expanded details align under that icon, like the web rows.
    /// Reactor's resource builder has no Thickness setter, so this writes the control's
    /// own resources before its template resolves them.
    /// </summary>
    internal static void FlushToolRowExpander(Microsoft.UI.Xaml.Controls.Expander expander)
    {
        expander.Resources["ExpanderHeaderPadding"] = new Thickness(0);
        expander.Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);
    }

    private static readonly string[] s_transparentToolRowBrushKeys =
    [
        "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderPointerOverBrush", "ExpanderHeaderBorderPressedBrush",
        "ExpanderHeaderDisabledBorderBrush",
        "ExpanderChevronForeground", "ExpanderChevronPointerOverForeground", "ExpanderChevronPressedForeground",
        "ExpanderChevronBackground", "ExpanderChevronPointerOverBackground", "ExpanderChevronPressedBackground",
        "ExpanderChevronBorderBrush", "ExpanderChevronBorderPointerOverBrush", "ExpanderChevronBorderPressedBrush",
    ];

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

    internal static void StyleSecondaryText(TextBlock text)
    {
        text.Style = (Style)Application.Current.Resources["ChatSecondaryTextStyle"];
        text.ClearValue(TextBlock.ForegroundProperty);
    }

    internal static void StylePrimaryText(TextBlock text)
    {
        text.Style = (Style)Application.Current.Resources["ChatPrimaryTextStyle"];
        text.ClearValue(TextBlock.ForegroundProperty);
    }

    internal static void StyleQuestionOption(Border border)
    {
        border.Style = (Style)Application.Current.Resources["ChatQuestionOtherStyle"];
        border.ClearValue(Border.BackgroundProperty);
        border.ClearValue(Border.BorderBrushProperty);
    }

    internal static void StyleQuestionQuietButton(Button button) => StyleQuestionAction(button, "ChatQuestionQuietButtonStyle");
    internal static void StyleQuestionSubmit(Button button) => StyleQuestionAction(button, "ChatQuestionSubmitStyle");

    private static void StyleQuestionAction(Button button, string style)
    {
        button.Style = (Style)Application.Current.Resources[style];
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.BorderThicknessProperty);
    }

    internal static void StyleQuestionInput(TextBox input, bool inline)
    {
        if (!inline) return;
        input.Style = (Style)Application.Current.Resources["ChatQuestionInputStyle"];
        input.ClearValue(Control.ForegroundProperty);
        input.ClearValue(Control.BackgroundProperty);
        input.ClearValue(Control.PaddingProperty);
        input.ClearValue(Control.BorderThicknessProperty);
        input.ClearValue(Control.MinHeightProperty);
    }

    internal static void StyleQuestionCard(Border border)
    {
        border.Style = (Style)Application.Current.Resources["ChatQuestionCardStyle"];
        border.ClearValue(Border.BackgroundProperty);
        border.ClearValue(Border.BorderBrushProperty);
    }
    internal static void StyleFlyoutHeading(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];

    internal static void StyleFlyoutCaption(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

    private static readonly ConditionalWeakTable<FrameworkElement, SizeChangedEventHandler> Observers = new();

    internal static void Observe(FrameworkElement element, Action<double> changed, Action<double>? heightChanged = null)
    {
        SizeChangedEventHandler handler = (_, args) =>
        {
            if (args.NewSize.Width > 0)
                changed(args.NewSize.Width);
            if (args.NewSize.Height > 0)
                heightChanged?.Invoke(args.NewSize.Height);
        };
        Observers.Add(element, handler);
        element.SizeChanged += handler;
        if (element.ActualWidth > 0)
            changed(element.ActualWidth);
        if (element.ActualHeight > 0)
            heightChanged?.Invoke(element.ActualHeight);
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
