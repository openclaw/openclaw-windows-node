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
    private const double TextShimmerSweepMilliseconds = 900;
    private const double TextShimmerCycleMilliseconds = 1300;
    private sealed record ShimmerState(Storyboard Storyboard, object OriginalLocal, LinearGradientBrush Shimmer);
    private static readonly ConditionalWeakTable<TextBlock, ShimmerState> s_shimmers = new();

    internal static TextBlockElement WithTextShimmer(this TextBlockElement element, bool active) =>
        element.Set(text => TextShimmer(text, active))
            .OnUnmount(control => TextShimmer((TextBlock)control, false));

    /// <summary>
    /// A small, slower indeterminate ring. ProgressRing's default style has a 16px minimum, which
    /// would crop smaller rings, and its Lottie player runs at a fixed rate unless slowed here.
    /// </summary>
    internal static Element SubagentSpinner(double size) =>
        Factories.ProgressRing().IsActive(true).Size(size, size).MinWidth(0).MinHeight(0)
            .OnMount(control =>
            {
                var ring = (ProgressRing)control;
                ring.Loaded -= SlowSpinner;
                ring.Loaded += SlowSpinner;
                SlowSpinner(ring, null!);
            });

    private static void SlowSpinner(object sender, RoutedEventArgs args)
    {
        if (sender is ProgressRing ring && FindDescendant<AnimatedVisualPlayer>(ring) is { } player)
            player.PlaybackRate = SubagentSpinnerPlaybackRate;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

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

        var storyboard = new Storyboard
        {
            Duration = TimeSpan.FromMilliseconds(TextShimmerCycleMilliseconds),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        foreach (var (index, end) in new[] { (1, 0.85), (2, 1.0), (3, 1.0) })
        {
            var stop = brush.GradientStops[index];
            var sweep = new DoubleAnimation
            {
                From = stop.Offset,
                To = end,
                Duration = TimeSpan.FromMilliseconds(TextShimmerSweepMilliseconds),
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
        foreach (var stop in state.Shimmer.GradientStops)
            stop.Color = secondary;
        state.Shimmer.GradientStops[2].Color = ink;
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
    /// <summary>The active Gateway theme's corner scale (CRT's near-square 0.25), or 1. Chat surfaces read it on render.</summary>
    internal static double CornerScale { get; private set; } = 1;

    // The native element retains its registration and managed wrapper while mounted. A weak table keyed only by
    // the wrapper loses the registration when WinUI recreates that wrapper. The global list stays weak because
    // disposing a Reactor host can drop its tree without unmounting it.
    private sealed record CornerRegistration(FrameworkElement Element, Func<CornerRadius> Radius);
    private static DependencyProperty? s_cornerRegistrationProperty;
    private static DependencyProperty CornerRegistrationProperty => s_cornerRegistrationProperty ??=
        DependencyProperty.RegisterAttached("ChatCornerRegistration", typeof(object), typeof(ChatVisuals), new PropertyMetadata(null));
    private static readonly List<WeakReference<CornerRegistration>> s_cornerElements = [];

    /// <summary>
    /// Sets the corner scale (values outside (0, 4] fall back to 1) and reshapes every mounted
    /// <see cref="ThemedCorner{T}(T, double)"/> surface on its own UI thread.
    /// </summary>
    internal static void SetCornerScale(double scale)
    {
        var next = scale is > 0 and <= 4 ? scale : 1;
        if (next == CornerScale)
            return;
        CornerScale = next;
        List<CornerRegistration> mounted = [];
        lock (s_cornerElements)
        {
            s_cornerElements.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in s_cornerElements)
            {
                if (reference.TryGetTarget(out var registration))
                    mounted.Add(registration);
            }
        }
        foreach (var registration in mounted)
        {
            if (registration.Element.DispatcherQueue is not { } queue)
                continue;
            if (queue.HasThreadAccess)
                RefreshCorner(registration);
            else
                queue.TryEnqueue(() => RefreshCorner(registration));
        }
    }

    private static void RefreshCorner(CornerRegistration registration)
    {
        var element = registration.Element;
        if (ReferenceEquals(element.GetValue(CornerRegistrationProperty), registration))
            SetCornerRadius(element, registration.Radius());
    }

    /// <summary>A chat surface's corner radius under the active theme's corner scale.</summary>
    internal static double Corner(double radius) => radius * CornerScale;

    /// <summary>Rounds the element by <paramref name="radius"/> under the theme's corner scale, following later switches.</summary>
    internal static T ThemedCorner<T>(this T element, double radius) where T : Element =>
        element.ThemedCorner(() => new CornerRadius(Corner(radius)));

    /// <summary>Rounds the element by a radius derived from the scaled chat radii, following later switches.</summary>
    internal static T ThemedCorner<T>(this T element, Func<double> radius) where T : Element =>
        element.ThemedCorner(() => new CornerRadius(radius()));

    /// <summary>Applies <paramref name="radius"/> now and again whenever the theme's corner scale changes while mounted.</summary>
    internal static T ThemedCorner<T>(this T element, Func<CornerRadius> radius) where T : Element =>
        element.CornerRadius(radius())
            .OnMountAdd(control =>
            {
                var registration = new CornerRegistration(control, radius);
                control.SetValue(CornerRegistrationProperty, registration);
                lock (s_cornerElements)
                {
                    s_cornerElements.RemoveAll(reference => !reference.TryGetTarget(out _));
                    s_cornerElements.Add(new(registration));
                }
            })
            .OnUnmountAdd(control =>
            {
                var registration = control.GetValue(CornerRegistrationProperty) as CornerRegistration;
                control.ClearValue(CornerRegistrationProperty);
                lock (s_cornerElements)
                {
                    s_cornerElements.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, registration));
                }
            });

    private static void SetCornerRadius(FrameworkElement element, CornerRadius radius)
    {
        switch (element)
        {
            case Border border:
                border.CornerRadius = radius;
                break;
            case Control control:
                control.CornerRadius = radius;
                break;
            case Grid grid:
                grid.CornerRadius = radius;
                break;
            case StackPanel stack:
                stack.CornerRadius = radius;
                break;
            case ContentPresenter presenter:
                presenter.CornerRadius = radius;
                break;
        }
    }

    internal static double ComposerRadius => Corner(20);
    internal const double ComposerMinHeight = 96;
    // Desktop reference composer: 14px editor text, a 14px "+", and a compact 24px Send/Stop
    // disc inside a 32px hit target with a 12px arrow or a filled 10px Stop square.
    internal const double ComposerInputSize = 14;
    internal const double ComposerAttachGlyphSize = 14;
    internal const double ComposerSendTargetSize = 32;
    internal const double ComposerSendSize = 24;
    internal const double ComposerSendGlyphSize = 12;
    internal const double ComposerStopGlyphSize = 10;
    internal const double ComposerToolbarInset = 8;
    // Pending attachments are compact pills: a 14px document glyph, 12px SemiBold name and a
    // 24px remove target revealed on hover, focus or touch, sized to the name up to the max width.
    internal const double AttachmentChipHeight = 28;
    internal const double AttachmentChipMaxWidth = 216;
    internal const double AttachmentChipGap = 6;
    internal const double AttachmentGlyphSize = 14;
    internal const double AttachmentRemoveSize = 24;
    // Image attachments are square tiles on their own row above the file pills, sized like the web
    // composer's 56px thumbnails. The remove badge overlaps the tile's top-right corner by this inset.
    internal const double AttachmentThumbnailSize = 56;
    internal const double AttachmentThumbnailBadgeInset = 6;
    internal const double AttachmentThumbnailCell = AttachmentThumbnailSize + AttachmentThumbnailBadgeInset;
    // Physical-pixel decode bound: sharp at 300% scale without holding full-size images.
    internal const uint AttachmentThumbnailDecodeSize = 192;
    internal const double AttachmentRailRowGap = 8;
    // Pills wrap onto new rows; past this height (four rows at default text size) the rail scrolls
    // instead of growing further. Scaled text makes rows taller, so fewer rows fit before scrolling.
    internal const int AttachmentRailMaxRows = 4;
    // Includes the rail's 3px top and bottom focus padding.
    internal const double AttachmentRailMaxHeight =
        AttachmentRailMaxRows * AttachmentChipHeight + (AttachmentRailMaxRows - 1) * AttachmentChipGap + 6;
    // WinUI's expanded ScrollBar (ScrollBarSize) overlays content instead of reserving space.
    internal const double AttachmentRailScrollBarInset = 12;
    // Space between the writing surface and the window's bottom edge. Matches the workspace pane
    // footer's bottom margin so the composer and account row share a baseline.
    internal const double ComposerBottomInset = 12;
    internal static double SurfaceRadius => Corner(12);
    internal const double BodySize = 14;
    /// <summary>Fluent chat body font when no Gateway theme supplies one.</summary>
    internal const string DefaultBodyFontFamily = "Segoe UI Variable Text";
    internal const string TaskMarkerFontFamily = "Segoe UI Symbol";
    internal const double BodyLineHeight = 21;
    internal const string DefaultCodeFontFamily = "Consolas";
    private static readonly HashSet<string> s_codeFontFamilies = new(StringComparer.OrdinalIgnoreCase) { DefaultCodeFontFamily };
    private static readonly HashSet<string> s_bodyFontFamilies = new(StringComparer.OrdinalIgnoreCase) { DefaultBodyFontFamily };

    /// <summary>Code font for chat code blocks and inline code; a Gateway theme may replace it.</summary>
    internal static string CodeFontFamily { get; private set; } = DefaultCodeFontFamily;

    /// <summary>Chat prose font from the active Gateway theme, or null for the Fluent body font.</summary>
    internal static string? ProseFontFamily { get; private set; }

    /// <summary>Chat body font: the active Gateway theme's chat font, or Segoe UI Variable Text.</summary>
    internal static string BodyFontFamily => ProseFontFamily ?? DefaultBodyFontFamily;

    /// <summary>Sets the theme's chat fonts (null restores the defaults). Mounted surfaces pick them up on refresh.</summary>
    internal static void SetThemeFonts(string? prose, string? code)
    {
        ProseFontFamily = prose;
        CodeFontFamily = code ?? DefaultCodeFontFamily;
        lock (s_codeFontFamilies)
            s_codeFontFamilies.Add(CodeFontFamily);
        lock (s_bodyFontFamilies)
            s_bodyFontFamilies.Add(BodyFontFamily);
    }

    private static (global::Windows.UI.Color Dark, global::Windows.UI.Color Light)? s_themeInlineCode;

    /// <summary>
    /// Sets the active Gateway theme's inline-code chip colors (null restores Fluent's chip). Each mode keeps its own
    /// color, so a chat in the app's other mode still paints that mode's chip. Mounted surfaces pick it up on refresh.
    /// </summary>
    internal static void SetThemeInlineCode((global::Windows.UI.Color Dark, global::Windows.UI.Color Light)? chip) => s_themeInlineCode = chip;

    /// <summary>The theme's inline-code chip color for a dark or light chat, or null when no theme supplies one.</summary>
    internal static global::Windows.UI.Color? ThemeInlineCode(bool dark) =>
        s_themeInlineCode is { } chip ? dark ? chip.Dark : chip.Light : null;

    /// <summary>True for any code font this session has issued, so code rendered before a theme switch stays code.</summary>
    internal static bool IsCodeFont(FontFamily? family) => IsIssued(s_codeFontFamilies, family);

    /// <summary>True for any chat body font this session has issued, so a theme switch can restyle that text.</summary>
    internal static bool IsBodyFont(FontFamily? family) => IsIssued(s_bodyFontFamilies, family);

    // The role each chat text element was given. A plugin or imported theme may give prose and code the same font
    // stack, so the font alone cannot tell them apart once a later theme separates them again. The role lives on the
    // element itself: a weak table keyed by the managed wrapper loses it when the wrapper is collected and recreated
    // while the control lives on.
    private const int BodyRole = 1;
    private const int CodeRole = 2;
    private static DependencyProperty? s_fontRoleProperty;
    // Registered on first use from UI code, not in the static initializer, which non-UI callers can also trigger.
    private static DependencyProperty FontRoleProperty => s_fontRoleProperty ??= DependencyProperty.RegisterAttached(
        "ChatFontRole", typeof(int), typeof(ChatVisuals), new PropertyMetadata(0));

    /// <summary>True or false when the element was given the code or the body font here; null when it never was.</summary>
    internal static bool? HasCodeRole(DependencyObject element) =>
        element.GetValue(FontRoleProperty) is int role and (BodyRole or CodeRole) ? role == CodeRole : null;

    /// <summary>Forgets an element's font role when Reactor unmounts it, since it may pool the control for other content.</summary>
    internal static void ClearFontRole(DependencyObject element) => element.ClearValue(FontRoleProperty);

    private static bool IsIssued(HashSet<string> issued, FontFamily? family)
    {
        if (family?.Source is not { } source)
            return false;
        lock (issued)
            return issued.Contains(source);
    }

    /// <summary>Gives chat text the current body font; a theme switch calls it again on mounted text.</summary>
    internal static void ApplyBodyFont(DependencyObject element)
    {
        element.SetValue(FontRoleProperty, BodyRole);
        var source = BodyFontFamily;
        switch (element)
        {
            case RichTextBlock rich when rich.FontFamily?.Source != source:
                rich.FontFamily = new FontFamily(source);
                break;
            case TextBlock text when text.FontFamily?.Source != source:
                text.FontFamily = new FontFamily(source);
                break;
            case Control control when control.FontFamily?.Source != source:
                control.FontFamily = new FontFamily(source);
                break;
        }
    }

    /// <summary>Gives code text and inline-code runs the current code font; a theme switch calls it again on mounted code.</summary>
    internal static void ApplyCodeFont(DependencyObject element)
    {
        element.SetValue(FontRoleProperty, CodeRole);
        var source = CodeFontFamily;
        switch (element)
        {
            case RichTextBlock rich when rich.FontFamily?.Source != source:
                rich.FontFamily = new FontFamily(source);
                break;
            case TextBlock text when text.FontFamily?.Source != source:
                text.FontFamily = new FontFamily(source);
                break;
            case Microsoft.UI.Xaml.Documents.TextElement run when run.FontFamily?.Source != source:
                run.FontFamily = new FontFamily(source);
                break;
        }
    }
    /// <summary>
    /// Keeps mounted code text that is outside the Markdown restyle pass on the current code font across theme
    /// switches, which do not re-render it.
    /// </summary>
    internal static T FollowCodeFont<T>(this T element) where T : Element =>
        (T)element
            .OnMountAdd(control => ChatThemeChanges.Watch(control, () => ApplyCodeFont(control)))
            .OnUnmountAdd(control =>
            {
                ChatThemeChanges.Unwatch(control);
                ClearFontRole(control);
            });

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
    internal static double CodeRadius => Corner(10);
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
    // Web blockquote: 8px 12px padding over a 3px rounded bar inset 4px from the top and bottom.
    internal const double QuoteBarWidth = 3;
    internal const double QuoteBarInset = 4;
    internal const double QuoteVerticalInset = 8;
    internal const double QuoteHorizontalInset = 12;
    // Web table: rounded 1px frame, 8px 12px cells separated by 1px rules, tinted header row.
    internal static double TableRadius => Corner(8);
    internal const double TableCellHorizontalInset = 12;
    internal const double TableCellVerticalInset = 8;
    internal const double CompactGap = 4;
    internal const double TurnGap = 4;
    internal const double DetailHorizontalInset = 12;
    internal const double DetailVerticalInset = 8;
    internal const double ActionIconSize = 14;
    internal const double ToolRowMinHeight = 32;

    /// <summary>
    /// The stock Expander's minimum width (its style binds FlyoutThemeMinWidth). Disclosure details
    /// rendered below a header-only Expander keep it, so a conversation squeezed during a layout
    /// change never measures them narrower than they were inside the Expander, which can make
    /// wrapped text and the transcript's height chase each other into a layout cycle.
    /// </summary>
    internal static double DisclosureDetailsMinWidth =>
        Application.Current?.Resources.TryGetValue("FlyoutThemeMinWidth", out var value) == true &&
            value is double width ? width : 96;
    // A tool row's 16px leading icon column plus its 8px gap; disclosure content aligns with the title.
    internal const double ToolRowTitleInset = 24;
    // Web tool and activity summaries are 13px (.chat-tool-msg-summary); details keep 12.
    internal const double ToolRowFontSize = 13;
    // Quieter activity titles (tool, thinking and run status) and their dimmed details.
    internal const double ActivityTextFontSize = 12;
    // Panels above the composer (queue, provider review) fade in over this when they appear.
    internal const int PanelRevealMilliseconds = 140;
    // A tool row's dimmed directory or search scope never takes more than this; it trims with "…".
    internal const double ToolRowDetailMaxWidth = 240;
    internal const double ActivityDetailsMaxHeight = 160;
    internal const double TranscriptTopInset = 8;
    // The transcript's bottom edge fades into the composer over this height. Like the web
    // thread padding, the last row's inset adds the fade so a settled tail stays fully opaque.
    // The fade eases in and out (ChatTranscriptFade), so its top edge never reads as a line.
    internal const double TranscriptFadeHeight = 64;
    internal const double TranscriptBottomInset = TranscriptFadeHeight + 6;
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
    internal const double AvatarSize = 36;
    internal const double AvatarGap = 10;
    internal const double AvatarSlot = AvatarSize + AvatarGap;
    internal const double HistoryTickSpacing = 10;
    internal const double HistoryTickHeight = 2;
    internal const double HistoryTickMinimumGap = 4;
    internal const double HistoryRailInset = 8;
    internal const int HistoryPreviewLength = 240;
    // Subagents pill above the composer and rows in the Subagents tab.
    internal const double SubagentPillHeight = 28;
    internal const double SubagentPillIconSize = 14;
    internal const double SubagentAvatarSize = 32;
    internal const double SubagentAvatarGlyphSize = 16;
    internal const double SubagentBadgeSize = 16;
    internal const double SubagentBadgeSpinnerSize = 10;
    internal const double SubagentBadgeOverhang = 4;
    // Subagent spinners sit beside text for minutes; the default Lottie rate reads as busy.
    private const double SubagentSpinnerPlaybackRate = 0.6;
    internal const double SubagentRowMinHeight = 56;
    internal const double SubagentFinishedRowMinHeight = 40;
    // Web .chat-composer-neighbor-card: 18px icon column, 10px gaps, 10/12 insets, 16px radius, 52px minimum.
    internal const double ReviewIconColumn = 18;
    internal const double ReviewIconSize = 16;
    internal const double ReviewGap = 10;
    internal const double ReviewVerticalInset = 10;
    internal static double ReviewRadius => Corner(16);
    internal const double ReviewMinHeight = 52;
    internal const double ReviewDialogMaxHeight = 420;

    internal static double WelcomeAvatarSize(double width) => width <= 768 ? 64 : 96;
    internal static double Gutter(double width) => width < FooterBreakpoint ? 12 : 40;
    internal static double RailWidth(double width) => width < FooterBreakpoint ? 24 : 40;
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

    // Send/Stop paints its own compact disc, so the 32px button chrome stays transparent at
    // rest and while disabled, with only a subtle circular hover/pressed halo around the disc.
    internal static void SendButtonResources(ResourceBuilder resources) =>
        resources
            .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
            .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
            .Set("ButtonBackgroundDisabled", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushDisabled", Theme.Ref("SubtleFillColorTransparentBrush"));

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

    /// <summary>Quiet 12px activity title; it shimmers only while its work is active.</summary>
    internal static TextBlockElement ActivityText(string label, bool active) =>
        Factories.TextBlock(label).FontSize(ActivityTextFontSize).TextWrapping(TextWrapping.NoWrap)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .Set(StyleSecondaryText)
            .WithTextShimmer(active);

    /// <summary>
    /// Aligns the stock Expander template with prose without replacing its interaction states.
    /// Header padding and the content border use template-scoped StaticResources, so local
    /// resource overrides alone cannot remove those insets.
    /// </summary>
    internal static void FlushToolRowExpander(Microsoft.UI.Xaml.Controls.Expander expander)
    {
        ApplyToolRowFlush(expander);
        // A reused control that was header-only shows its content area again.
        if (s_headerOnlyExpanders.Remove(expander))
            RestoreContentArea(expander);
    }

    private static void ApplyToolRowFlush(Expander expander)
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

    /// <summary>
    /// Like <see cref="FlushToolRowExpander"/>, for an Expander that carries only the header and
    /// renders its details outside: its empty content area takes no height, so expanding or
    /// collapsing never adds or removes a gap before the details reveal or after they close.
    /// Runs on every render, so it changes the template only when its state differs.
    /// </summary>
    internal static void FlushHeaderOnlyExpander(Microsoft.UI.Xaml.Controls.Expander expander)
    {
        ApplyToolRowFlush(expander);
        if (!s_headerOnlyExpanders.TryGetValue(expander, out _))
        {
            s_headerOnlyExpanders.Add(expander, HeaderOnlyMarker);
            expander.Loaded += OnHeaderOnlyLoaded;
        }
        if (expander.IsLoaded)
            CollapseContentArea(expander);
    }

    /// <summary>
    /// Returns a header-only Expander's content area to the stock template on unmount, so a
    /// reused control shows its content again.
    /// </summary>
    internal static void RestoreHeaderOnlyExpander(FrameworkElement control)
    {
        if (control is Expander expander && s_headerOnlyExpanders.Remove(expander))
            RestoreContentArea(expander);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Expander, object>
        s_headerOnlyExpanders = new();
    private static readonly object HeaderOnlyMarker = new();

    private static void OnHeaderOnlyLoaded(object sender, RoutedEventArgs args) =>
        CollapseContentArea((Expander)sender);

    private static void CollapseContentArea(Expander expander) =>
        SetContentAreaMaxHeight(expander, 0);

    private static void RestoreContentArea(Expander expander)
    {
        expander.Loaded -= OnHeaderOnlyLoaded;
        SetContentAreaMaxHeight(expander, null);
    }

    private static void SetContentAreaMaxHeight(Expander expander, double? maxHeight)
    {
        if (VisualTreeHelper.GetChildrenCount(expander) == 0 ||
            VisualTreeHelper.GetChild(expander, 0) is not FrameworkElement root)
            return;
        foreach (var part in ExpanderContentParts)
        {
            if (root.FindName(part) is not FrameworkElement element)
                continue;
            if (maxHeight is { } value)
            {
                if (element.MaxHeight != value)
                    element.MaxHeight = value;
            }
            else if (element.ReadLocalValue(FrameworkElement.MaxHeightProperty) != DependencyProperty.UnsetValue)
            {
                element.ClearValue(FrameworkElement.MaxHeightProperty);
            }
        }
    }

    private static readonly string[] ExpanderContentParts = ["ExpanderContentClip", "ExpanderContent"];

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
        // WinUI connects SystemBackdrop only for windowed popups.
        flyout.ShouldConstrainToRootBounds = false;
        flyout.SystemBackdrop ??= new DesktopAcrylicBackdrop();
        flyout.FlyoutPresenterStyle = (Style)Application.Current.Resources["ChatPickerFlyoutStyle"];
        flyout.Placement = FlyoutPlacementMode.TopEdgeAlignedRight;
    }

    internal static void StylePicker(MenuFlyout menu)
    {
        menu.SystemBackdrop ??= new DesktopAcrylicBackdrop();
        menu.MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["ChatPickerMenuStyle"];
        menu.Placement = FlyoutPlacementMode.TopEdgeAlignedRight;
    }

    internal static void StyleLeadingPicker(Flyout flyout)
    {
        StylePicker(flyout);
        flyout.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft;
    }

    internal static void StylePickerAccent(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["ChatPickerAccentTextStyle"];

    internal static void StylePickerValue(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["ChatPickerValueTextStyle"];

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

    internal static void AttachComposerPlaceholder(TextBox input)
    {
        DetachComposerPlaceholder(input);
        input.Loaded += OnComposerLoaded;
        input.Unloaded += OnComposerUnloaded;
        if (input.IsLoaded)
            ActivateComposerPlaceholder(input);
    }

    internal static void DetachComposerPlaceholder(TextBox input)
    {
        input.Loaded -= OnComposerLoaded;
        input.Unloaded -= OnComposerUnloaded;
        DeactivateComposerPlaceholder(input);
    }

    private static void DeactivateComposerPlaceholder(TextBox input)
    {
        input.ActualThemeChanged -= OnComposerThemeChanged;
        ChatThemeChanges.Unwatch(input);
        input.ClearValue(TextBox.PlaceholderForegroundProperty);
    }

    private static void OnComposerLoaded(object sender, RoutedEventArgs args) =>
        ActivateComposerPlaceholder((TextBox)sender);

    private static void OnComposerUnloaded(object sender, RoutedEventArgs args) =>
        DeactivateComposerPlaceholder((TextBox)sender);

    private static void ActivateComposerPlaceholder(TextBox input)
    {
        input.ActualThemeChanged -= OnComposerThemeChanged;
        input.ActualThemeChanged += OnComposerThemeChanged;
        ChatThemeChanges.Watch(input, () =>
        {
            ApplyComposerPlaceholder(input);
            ApplyBodyFont(input);
        });
        ApplyComposerPlaceholder(input);
    }

    // Reactor owns the control Style. Keep this explicit brush in sync with both
    // element theme and contrast-only changes, and clear it before control pooling.
    private static void ApplyComposerPlaceholder(TextBox input)
    {
        if (ChatThemeChanges.IsHighContrast())
            input.ClearValue(TextBox.PlaceholderForegroundProperty);
        else
            input.PlaceholderForeground =
                ThemeRef.Resolve("ChatMutedTextBrush", input.ActualTheme == ElementTheme.Dark) as Brush
                ?? throw new InvalidOperationException("ChatMutedTextBrush is missing.");
    }

    private static void OnComposerThemeChanged(FrameworkElement sender, object args) =>
        ApplyComposerPlaceholder((TextBox)sender);

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

    internal static void StyleFlyoutBody(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["BodyTextBlockStyle"];

    internal static void StyleFlyoutCaption(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

    // A ThemeRef foreground replaces the element Style, so secondary flyout text carries its brush in its style.
    internal static void StyleFlyoutSecondaryBody(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["ChatFlyoutSecondaryBodyTextStyle"];

    internal static void StyleFlyoutSecondaryCaption(TextBlock text) =>
        text.Style = (Style)Application.Current.Resources["ChatFlyoutSecondaryCaptionTextStyle"];

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
