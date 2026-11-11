using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
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
    private const double TextShimmerDurationMilliseconds = 2400;
    private const double TextShimmerContrastBoost = 0.2;
    private sealed record ShimmerState(Storyboard Storyboard, object OriginalLocal, LinearGradientBrush Shimmer);
    private static readonly ConditionalWeakTable<TextBlock, ShimmerState> s_shimmers = new();

    internal static TextBlockElement WithTextShimmer(this TextBlockElement element, bool active) =>
        element.Set(text => TextShimmer(text, active))
            .OnUnmount(control => TextShimmer((TextBlock)control, false));

    internal static void StopToolRowShimmers(FrameworkElement control)
    {
        // Reactor preview.16 tears down Expander.Content but does not visit its Header slot.
        if (control is Expander { Header: DependencyObject header })
            StopHeader(header);

        static void StopHeader(DependencyObject element)
        {
            if (element is TextBlock text)
                TextShimmer(text, false);
            else if (element is Panel panel)
                foreach (var child in panel.Children)
                    StopHeader(child);
            else if (element is Border { Child: { } child })
                StopHeader(child);
            else if (element is ContentControl { Content: DependencyObject content })
                StopHeader(content);
        }
    }

    /// <summary>
    /// The web's running-text shimmer (<c>text-shimmer</c>): a brighter band sweeps
    /// across the label while a tool, thinking or run is active. Windows animation
    /// off or high contrast keeps static text, like the web's reduced-motion and
    /// forced-colors rules. Use <see cref="WithTextShimmer"/> to stop before Reactor pools the control.
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
            RefreshShimmerColors(text);
            if (!ReferenceEquals(text.Foreground, state.Shimmer))
                text.Foreground = state.Shimmer;
            return;
        }

        // Keep the brush coordinates fixed. Animating RelativeTransform on a translucent
        // text brush can corrupt other content on WinUI's shared render surface.
        var brush = new LinearGradientBrush
        {
            StartPoint = new(0, 0.5),
            EndPoint = new(1, 0.5),
        };
        foreach (var offset in new[] { 0, 0, 0, 0.15, 1 })
            brush.GradientStops.Add(new GradientStop { Offset = offset });

        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (index, end) in new[] { (1, 0.85), (2, 1.0), (3, 1.0) })
        {
            var stop = brush.GradientStops[index];
            var sweep = new DoubleAnimation
            {
                From = stop.Offset,
                To = end,
                Duration = TimeSpan.FromMilliseconds(TextShimmerDurationMilliseconds),
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
        text.Loaded -= OnShimmerLoaded;
        text.Loaded += OnShimmerLoaded;
        text.ActualThemeChanged -= OnShimmerActualThemeChanged;
        text.ActualThemeChanged += OnShimmerActualThemeChanged;
        RefreshShimmerColors(text);
        storyboard.Begin();
    }

    /// <summary>
    /// Resolve ink on the actual label through native theme binding, not by reading
    /// an application-wide brush whose color may belong to the opposite theme.
    /// The temporary style/foreground swap is synchronous and never painted.
    /// </summary>
    private static void RefreshShimmerColors(TextBlock text)
    {
        if (!s_shimmers.TryGetValue(text, out var state))
            return;
        var originalStyle = text.Style;
        var originalForeground = text.ReadLocalValue(TextBlock.ForegroundProperty);
        global::Windows.UI.Color ink;
        global::Windows.UI.Color secondary;
        try
        {
            text.ClearValue(TextBlock.ForegroundProperty);
            text.Style = (Style)Application.Current.Resources["ChatPrimaryTextStyle"];
            ink = ((SolidColorBrush)text.Foreground).Color;
            text.Style = (Style)Application.Current.Resources["ChatSecondaryTextStyle"];
            secondary = ((SolidColorBrush)text.Foreground).Color;
        }
        finally
        {
            text.Style = originalStyle;
            if (originalForeground == DependencyProperty.UnsetValue)
                text.ClearValue(TextBlock.ForegroundProperty);
            else
                text.SetValue(TextBlock.ForegroundProperty, originalForeground);
        }
        secondary.A = (byte)Math.Round(secondary.A - (ink.A - secondary.A) * TextShimmerContrastBoost);
        var dark = text.ActualTheme == ElementTheme.Dark;
        foreach (var stop in state.Shimmer.GradientStops)
            stop.Color = dark ? secondary : ink;
        var flare = dark ? ink : secondary;
        state.Shimmer.GradientStops[2].Color = flare;
    }

    private static void OnShimmerLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock text)
            RefreshShimmerColors(text);
    }

    private static void OnShimmerActualThemeChanged(FrameworkElement sender, object args)
    {
        if (sender is TextBlock text)
            RefreshShimmerColors(text);
    }

    private static void OnShimmerUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock text && s_shimmers.TryGetValue(text, out var state))
            StopShimmer(text, state);
    }

    private static void StopShimmer(TextBlock text, ShimmerState state)
    {
        text.Unloaded -= OnShimmerUnloaded;
        text.Loaded -= OnShimmerLoaded;
        text.ActualThemeChanged -= OnShimmerActualThemeChanged;
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
    // Web .chat-thinking is 12px muted text; 18 follows the 12/18 footer ramp.
    internal const double ReasoningSize = 12;
    internal const double ReasoningLineHeight = 18;
    internal const double BlockGap = 8;
    // Web --chat-block-gap: top-level Markdown blocks and text/media stacks are one text size apart.
    internal const double MessageBlockGap = 14;
    // Headings that follow other blocks get the web's 1.5x block gap.
    internal const double HeadingTopInset = MessageBlockGap / 2;
    // Web .code-block-wrapper: 10px corners, 36px header (8/8/4/14 insets) and pre insets that
    // place code 14 pixels from the side and bottom edges.
    internal const double CodeRadius = 10;
    internal const double CodeInset = 14;
    internal const double CodeHeaderTopInset = 8;
    internal const double CodeHeaderTrailingInset = 8;
    internal const double CodeHeaderBottomInset = 4;
    internal const double CodeLabelSize = 12;
    // Web lists reserve a 24px marker column (16px marker box plus 8px gap) and space items 0.4em apart.
    internal const double ListMarkerColumn = 20;
    internal const double ListItemGap = 6;
    // Compact thinking text keeps Reactor's original 2px list rhythm.
    internal const double CompactListItemGap = 2;
    internal const double CompactGap = 4;
    internal const double TurnGap = 4;
    internal const double DetailHorizontalInset = 12;
    internal const double DetailVerticalInset = 8;
    internal const double ToolRowMinHeight = 32;
    // Web tool and activity summaries are 13px (.chat-tool-msg-summary); details keep 12.
    internal const double ToolRowFontSize = 13;
    internal const double ActivityDetailsMaxHeight = 160;
    internal const double TranscriptTopInset = 8;
    internal const double TranscriptBottomInset = 24;
    internal const double JumpLatestSize = 32;
    internal const double QueueAvatarSize = 20;
    internal const double QueueCompactAvatarSize = 18;
    internal const double QueueLeadingInset = 13;
    internal const double QueueVerticalInset = 6;
    internal const double QueueTrailingInset = 4;
    internal const double QueueCompactGap = 6;
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
    /// Aligns the stock Expander template with prose without replacing its interaction states.
    /// Header padding and the content border use template-scoped StaticResources, so local
    /// resource overrides alone cannot remove those insets.
    /// </summary>
    internal static void FlushToolRowExpander(Microsoft.UI.Xaml.Controls.Expander expander)
    {
        expander.Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);
        expander.Resources["ExpanderChevronMargin"] = new Thickness(0);
        expander.Loaded -= OnToolRowLoaded;
        expander.Loaded += OnToolRowLoaded;
        if (expander.IsLoaded)
            ApplyToolRowInsets(expander);
    }

    private static void OnToolRowLoaded(object sender, RoutedEventArgs args) =>
        ApplyToolRowInsets((Expander)sender);

    private static void ApplyToolRowInsets(Expander expander)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(expander, 0);
        var header = (ToggleButton)root.FindName("ExpanderHeader");
        header.Padding = new Thickness(0);
        header.BorderThickness = new Thickness(0);
        ((Border)root.FindName("ExpanderContent")).BorderThickness = new Thickness(0);
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
