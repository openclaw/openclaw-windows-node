// Shared page presentation. Example: PageHero.Header(...), then PageHero.SummaryTiles(...).
using Microsoft.UI.Reactor;
using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClawTray.Helpers;
using Windows.UI;
using Windows.UI.ViewManagement;
using FunctionalTheme = OpenClawTray.FunctionalUI.Core.Theme;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Presentation;

public enum PageHeroTone
{
    Neutral,
    Accent,
    Success,
    Caution,
    Critical,
}

/// <summary>One accessible summary value, optionally actionable or rendered as a status dot.</summary>
public sealed record PageHeroTile(
    string Label,
    string Value,
    string Glyph,
    string AutomationId,
    PageHeroTone Tone = PageHeroTone.Neutral,
    bool StatusDot = false,
    Action? OnInvoked = null,
    string? AutomationName = null,
    bool IsEnabled = true);

/// <summary>Shared Fluent hero, summary tiles, attention badges, and reduced-motion-aware transitions.</summary>
public static class PageHero
{
    private const double HeaderRadius = 8;
    private const double TileGap = PageResponsiveLayout.GapGroup;
    private const double HighlightDurationMs = 1200;
    private static readonly ConditionalWeakTable<FrameworkElement, Storyboard> Highlights = new();

    /// <summary>Builds a hero; callers own localized text, actions, accessibility IDs and page state.</summary>
    /// <param name="titleIsTabStop">Opt in when the named heading is a page-navigation focus destination.</param>
    public static Element Header(
        string automationId,
        string glyph,
        string title,
        string titleAutomationId,
        Element answerLine,
        Element? detailLine,
        Element actions,
        bool narrow,
        bool reducedMotion,
        bool highContrast = false,
        string? glyphAutomationId = null,
        Element? captionLine = null,
        string? pageTag = null,
        bool titleIsTabStop = false)
    {
        var titleLine = VStack(4,
            TextBlock(title)
                .Set(native => native.Style = (Style)Application.Current.Resources["TitleTextBlockStyle"])
                .FontSize(narrow ? 24 : 28).FontWeight(FontWeights.SemiBold)
                .TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(title)
                .AutomationId(titleAutomationId)
                .IsTabStop(titleIsTabStop)
                .HeadingLevel(AutomationHeadingLevel.Level1),
            answerLine,
            detailLine ?? Empty(),
            captionLine ?? Empty());
        var animatedActions = WithMotion(
            Grid([GridSize.Auto], [GridSize.Auto], actions), reducedMotion);
        // Change placement, not ancestry, so actions retain native focus and flyout state.
        var content = WithMotion(Grid(
            [GridSize.Px(48), GridSize.Star(), GridSize.Auto],
            [GridSize.Auto, GridSize.Auto],
            PageHeroIcon.Marker(automationId, glyph, pageTag, highContrast, glyphAutomationId),
            titleLine.Margin(16, 0, narrow ? 0 : 16, 0)
                .Grid(column: 1, row: 0, columnSpan: narrow ? 2 : 1),
            animatedActions
                .Grid(column: narrow ? 1 : 2, row: narrow ? 1 : 0, columnSpan: narrow ? 2 : 1)
                .Margin(narrow ? 16 : 0, narrow ? 12 : 0, 0, 0)
                .HAlign(narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right)
                .VAlign(narrow ? VerticalAlignment.Top : VerticalAlignment.Center))
            .Padding(narrow ? 16 : 24, narrow ? 16 : 20)
            .AutomationId($"{automationId}Grid"), reducedMotion);

        var hero = Border(Grid([GridSize.Star()], [GridSize.Star()], AccentWash(highContrast), content))
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .BorderThickness(1).CornerRadius(HeaderRadius)
            .AutomationName(title).AutomationId(automationId);
        return WithMotion(hero, reducedMotion);
    }

    /// <summary>Builds two to four tiles. Width is the page host width in DIPs; pageName is localized.</summary>
    public static Element SummaryTiles(
        string automationId,
        string pageName,
        IReadOnlyList<PageHeroTile> tiles,
        double pageWidth,
        bool reducedMotion,
        bool highContrast = false)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(tiles), "Page hero summaries need two to four tiles.");

        var columns = pageWidth >= 760 ? tiles.Count : pageWidth < 380 ? 1 : 2;
        var rows = (tiles.Count + columns - 1) / columns;
        var children = new List<Element>(tiles.Count);
        for (var index = 0; index < tiles.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var lastSpanningTile = columns == 2 && tiles.Count % 2 == 1 && index == tiles.Count - 1;
            var left = column == 0 || lastSpanningTile ? 0 : TileGap / 2;
            var right = lastSpanningTile || column == columns - 1 ? 0 : TileGap / 2;
            var tile = Tile(tiles[index], highContrast)
                .Margin(left, row == 0 ? 0 : TileGap / 2, right, row == rows - 1 ? 0 : TileGap / 2)
                .Grid(column: column, row: row, columnSpan: lastSpanningTile ? columns : 1)
                .VAlign(VerticalAlignment.Stretch);
            children.Add(tile.WithKey(tiles[index].AutomationId));
        }

        return WithMotion(Grid(Enumerable.Repeat(GridSize.Star(), columns).ToArray(),
                Enumerable.Repeat(GridSize.Auto, rows).ToArray(), children.ToArray())
            .AutomationName(pageName).AutomationId(automationId), reducedMotion);
    }

    /// <summary>Builds an accessible status badge with a stable reconciliation key.</summary>
    public static Element AttentionBadge(
        string automationId,
        string text,
        string accessibleReason,
        string glyph,
        PageHeroTone tone,
        bool reducedMotion,
        string? reconciliationKey = null)
    {
        var (glyphForeground, textForeground, background) = BadgeTone(tone);
        var badge = Border(HStack(4,
                Glyph(glyph, 12, glyphForeground),
                TextBlock(text).FontSize(12).FontWeight(FontWeights.SemiBold)
                    .Foreground(textForeground).TextWrapping(TextWrapping.NoWrap)))
            .Height(20).Padding(8, 0).CornerRadius(10).Background(background)
            .ToolTip(accessibleReason)
            .AutomationName(string.IsNullOrWhiteSpace(accessibleReason) ? text : $"{text}. {accessibleReason}")
            .ItemStatus(accessibleReason).AutomationId(automationId)
            .WithKey(reconciliationKey ?? automationId);
        return AnimationsEnabled(reducedMotion)
            ? badge.Transition(Microsoft.UI.Reactor.Animation.Transition.Fade,
                Microsoft.UI.Reactor.Animation.Curve.Ease(160, Microsoft.UI.Reactor.Animation.Easing.Decelerate))
            : badge;
    }

    /// <summary>Applies entrance, reflow and exit transitions to a caller-owned container.</summary>
    public static Element WithMotion(Element element, bool reducedMotion, bool staggerChildren = true)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element switch
        {
            BorderElement border => WithMotion(border, reducedMotion, staggerChildren),
            GridElement grid => WithMotion(grid, reducedMotion, staggerChildren),
            StackElement stack => WithMotion(stack, reducedMotion, staggerChildren),
            _ => throw new NotSupportedException(
                $"PageHero motion requires a Border, Grid, or StackPanel element, not {element.GetType().Name}."),
        };
    }

    public static BorderElement WithMotion(BorderElement element, bool reducedMotion, bool staggerChildren = true) =>
        element.Set(native => ConfigureMotion(native, reducedMotion, staggerChildren));

    public static GridElement WithMotion(GridElement element, bool reducedMotion, bool staggerChildren = true) =>
        element.Set(native => ConfigureMotion(native, reducedMotion, staggerChildren));

    public static StackElement WithMotion(StackElement element, bool reducedMotion, bool staggerChildren = true) =>
        element.Set(native => ConfigureMotion(native, reducedMotion, staggerChildren));

    /// <summary>Brings a target into view, focuses its first enabled control, and briefly highlights it.</summary>
    public static void RevealAndHighlight(FrameworkElement target, bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(target);
        StopHighlight(target);
        var animate = AnimationsEnabled(reducedMotion);
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = animate });
        TryFocus(target);
        if (!animate || new AccessibilitySettings().HighContrast) return;
        if (GetBackground(target) is null) return;
        var accent = FunctionalTheme.ResolveColor("AccentFillColorDefault", target.ActualTheme);
        var highlight = new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
        var duration = TimeSpan.FromMilliseconds(HighlightDurationMs);
        // Animation precedence leaves the underlying style, resource expression or binding intact.
        var background = new ObjectAnimationUsingKeyFrames
        {
            Duration = duration,
            FillBehavior = FillBehavior.Stop,
        };
        background.KeyFrames.Add(new DiscreteObjectKeyFrame { KeyTime = TimeSpan.Zero, Value = highlight });
        Storyboard.SetTarget(background, target);
        Storyboard.SetTargetProperty(background, nameof(Control.Background));
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = duration,
        };
        var storyboard = new Storyboard();
        Highlights.Add(target, storyboard);
        Storyboard.SetTarget(animation, highlight);
        Storyboard.SetTargetProperty(animation, nameof(Brush.Opacity));
        storyboard.Children.Add(background);
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (Highlights.TryGetValue(target, out var current) && ReferenceEquals(current, storyboard))
                StopHighlight(target);
        };
        storyboard.Begin();
    }

    private static void StopHighlight(FrameworkElement target)
    {
        if (!Highlights.TryGetValue(target, out var storyboard)) return;
        Highlights.Remove(target);
        storyboard.Stop();
    }

    private static Element AccentWash(bool highContrast)
    {
        if (highContrast) return Empty();
        return Border(Empty()).CornerRadius(HeaderRadius)
            .Set(native => FunctionalTheme.EnsureThemeCallback(native, () => native.Background = AccentGradient(native.ActualTheme)))
            .IsHitTestVisible(false).AccessibilityView(AccessibilityView.Raw)
            .AutomationId("PageHeroAccentWash");
    }

    private static void ConfigureMotion(FrameworkElement native, bool reducedMotion, bool staggerChildren)
    {
        var animate = AnimationsEnabled(reducedMotion);
        if (animate)
        {
            if (native.Transitions is not { Count: 1 } transitions
                || transitions[0] is not RepositionThemeTransition)
                native.Transitions = new TransitionCollection { new RepositionThemeTransition() };
        }
        else if (native.Transitions is not { Count: 0 })
            native.Transitions = new TransitionCollection();

        if (native is not Panel panel) return;
        if (animate)
        {
            var expectedCount = staggerChildren ? 2 : 1;
            if (panel.ChildrenTransitions?.Count != expectedCount
                || staggerChildren && panel.ChildrenTransitions[0] is not EntranceThemeTransition { IsStaggeringEnabled: true }
                || panel.ChildrenTransitions[^1] is not AddDeleteThemeTransition)
            {
                panel.ChildrenTransitions = staggerChildren
                    ? new TransitionCollection
                    {
                        new EntranceThemeTransition { IsStaggeringEnabled = true },
                        new AddDeleteThemeTransition(),
                    }
                    : new TransitionCollection { new AddDeleteThemeTransition() };
            }
        }
        else if (panel.ChildrenTransitions is not { Count: 0 })
            panel.ChildrenTransitions = new TransitionCollection();
    }

    private static Element Tile(PageHeroTile tile, bool highContrast)
    {
        var (foreground, background) = Tone(tile.Tone);
        Element marker = tile.StatusDot
            ? Border(Border(Empty()).Size(10, 10).CornerRadius(5).Background(foreground))
                .Size(16, 16).CornerRadius(8).Background(background).Center()
                .AutomationId($"{tile.AutomationId}:StatusDot")
            : Glyph(tile.Glyph, 16, foreground).Center();
        var icon = Grid([GridSize.Px(36)], [GridSize.Px(36)],
            Border(Empty()).CornerRadius(18).Background(background).AutomationId($"{tile.AutomationId}:IconWell"),
            marker);
        var copy = VStack(4,
            TextBlock(tile.Label).FontSize(12).Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2),
            TextBlock(tile.Value).FontSize(20).FontWeight(FontWeights.SemiBold)
                .Foreground(!highContrast && tile.Tone == PageHeroTone.Critical ? foreground : Theme.PrimaryText)
                .TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2));
        var name = tile.AutomationName ?? $"{tile.Label}: {tile.Value}";
        var content = Grid(
            tile.OnInvoked is null
                ? [GridSize.Px(48), GridSize.Star()]
                : [GridSize.Px(48), GridSize.Star(), GridSize.Px(16)],
            [GridSize.Auto],
            icon.Grid(column: 0).Margin(0, 0, 12, 0).VAlign(VerticalAlignment.Top),
            copy.Grid(column: 1).VAlign(VerticalAlignment.Center),
            tile.OnInvoked is null ? Empty()
                : Glyph("\uE76C", 12, Theme.SecondaryText).Center().Grid(column: 2));
        if (tile.OnInvoked is null)
            return Border(content).Padding(16, 14).CornerRadius(8)
                .Background(Theme.CardBackground).BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                .BorderThickness(1).AutomationName(name).AutomationId(tile.AutomationId);

        return Button(content, tile.OnInvoked).Padding(16, 14)
            .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .CornerRadius(8).Background(Theme.CardBackground)
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush")).BorderThickness(1)
            .IsEnabled(tile.IsEnabled).ToolTip(name).AutomationName(name).AutomationId(tile.AutomationId);
    }

    private static (ThemeRef Foreground, ThemeRef Background) Tone(PageHeroTone tone) => tone switch
    {
        PageHeroTone.Accent => (Theme.Ref("AccentTextFillColorPrimaryBrush"), Theme.Ref("AccentFillColorTertiaryBrush")),
        PageHeroTone.Success => (Theme.Ref("SystemFillColorSuccessBrush"), Theme.Ref("InfoBarSuccessSeverityBackgroundBrush")),
        PageHeroTone.Caution => (Theme.Ref("SystemFillColorCautionBrush"), Theme.Ref("InfoBarWarningSeverityBackgroundBrush")),
        PageHeroTone.Critical => (Theme.Ref("SystemFillColorCriticalBrush"), Theme.Ref("InfoBarErrorSeverityBackgroundBrush")),
        _ => (Theme.SecondaryText, Theme.Ref("SubtleFillColorSecondaryBrush")),
    };

    private static (ThemeRef Glyph, ThemeRef Text, ThemeRef Background) BadgeTone(PageHeroTone tone) => tone switch
    {
        PageHeroTone.Accent => (Theme.Ref("AccentTextFillColorPrimaryBrush"), Theme.Ref("AccentTextFillColorPrimaryBrush"),
            Theme.Ref("AccentFillColorTertiaryBrush")),
        PageHeroTone.Caution => (Theme.Ref("SystemFillColorCautionBrush"), Theme.PrimaryText,
            Theme.Ref("InfoBarWarningSeverityBackgroundBrush")),
        PageHeroTone.Critical => (Theme.Ref("SystemFillColorCriticalBrush"), Theme.Ref("SystemFillColorCriticalBrush"),
            Theme.Ref("InfoBarErrorSeverityBackgroundBrush")),
        _ => (Tone(tone).Foreground, Tone(tone).Foreground, Tone(tone).Background),
    };

    private static LinearGradientBrush AccentGradient(ElementTheme theme)
    {
        var accent = FunctionalTheme.ResolveColor("AccentFillColorDefault", theme);
        return new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops =
            [
                new GradientStop { Offset = 0, Color = Color.FromArgb(0x26, accent.R, accent.G, accent.B) },
                new GradientStop { Offset = 0.58, Color = Color.FromArgb(0x0D, accent.R, accent.G, accent.B) },
                new GradientStop { Offset = 1, Color = Color.FromArgb(0, accent.R, accent.G, accent.B) },
            ],
        };
    }

    private static bool AnimationsEnabled(bool reducedMotion) =>
        !reducedMotion && new UISettings().AnimationsEnabled;

    private static TextBlockElement Glyph(string glyph, double size, ThemeRef foreground) =>
        TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(size).Foreground(foreground).AccessibilityView(AccessibilityView.Raw)
            .VAlign(VerticalAlignment.Center);

    private static bool TryFocus(DependencyObject root)
    {
        if (root is UIElement { Visibility: Visibility.Collapsed }) return false;
        if (root is Control { IsEnabled: true, IsTabStop: true } control
            && control.Focus(FocusState.Programmatic))
            return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (TryFocus(VisualTreeHelper.GetChild(root, index))) return true;
        return false;
    }

    private static Brush? GetBackground(FrameworkElement target) => target switch
    {
        Control control => control.Background,
        Border border => border.Background,
        _ => null,
    };

}
