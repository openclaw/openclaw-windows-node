using System;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Cron;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Automations;

/// <summary>Everything the Automations tree needs from its WinUI host.</summary>
internal sealed record AutomationsHost(
    AutomationsStore Store,
    AutomationsText Text,
    Func<IOperatorGatewayClient?> Client,
    Action OpenConnection,
    Action<string> OpenChat)
{
    /// <summary>Asks for confirmation (title, body, primary label). Bound by the root to its controlled dialog.</summary>
    public Func<string, string, string, Task<bool>> Confirm { get; init; } = static (_, _, _) => Task.FromResult(false);
}

/// <summary>
/// Shared geometry and building blocks. Spacing follows DESIGN.md steps
/// (4/8/12/16/24/32) and every colour is a theme resource so light, dark
/// and high contrast stay correct.
/// </summary>
internal static class AutomationsVisuals
{
    internal const double Gap = 8;
    internal const double SectionGap = 16;
    internal const double PagePadding = 24;
    internal const double ListPaneWidth = 360;
    internal const double NarrowBreakpoint = 860;
    internal const double ReadingWidth = 860;
    internal const double CardRadius = 8;
    internal const string MonoFont = "Cascadia Mono, Consolas";

    internal const string GlyphPlay = "\uE768";
    internal const string GlyphPause = "\uE769";
    internal const string GlyphEdit = "\uE70F";
    internal const string GlyphMore = "\uE712";
    internal const string GlyphDelete = "\uE74D";
    internal const string GlyphRefresh = "\uE72C";
    internal const string GlyphAdd = "\uE710";
    internal const string GlyphBack = "\uE72B";
    internal const string GlyphCalendar = "\uE787";
    internal const string GlyphClock = "\uE823";
    internal const string GlyphHistory = "\uE81C";
    internal const string GlyphNote = "\uE70B";
    internal const string GlyphChat = "\uE8BD";
    internal const string GlyphSend = "\uE724";
    internal const string GlyphAlert = "\uE7BA";
    internal const string GlyphError = "\uEA39";
    internal const string GlyphOk = "\uE73E";
    internal const string GlyphSkip = "\uE893";
    internal const string GlyphBranch = "\uE8AB";
    internal const string GlyphPlug = "\uE839";
    internal const string GlyphSearch = "\uE721";
    internal const string GlyphLock = "\uE72E";
    internal const string GlyphEvent = "\uE945";
    internal const string GlyphSparkle = "\uE9D5";
    internal const string GlyphDisconnected = "\uF384";

    internal static TextBlockElement Glyph(string glyph, double size = 16, ThemeRef? brush = null) =>
        TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(size)
            .FontWeight(FontWeights.Normal)
            .Foreground(brush ?? Theme.SecondaryText)
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

    internal static TextBlockElement Secondary(string text, double size = 12) =>
        TextBlock(text).FontSize(size).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap);

    internal static TextBlockElement SectionTitle(string text) =>
        TextBlock(text).FontSize(14).FontWeight(FontWeights.SemiBold).Foreground(Theme.PrimaryText);

    internal static Element GlyphButton(string glyph, string label, Action onClick, string automationId, bool enabled = true, bool accent = false)
    {
        var button = Button(HStack(Gap, Glyph(glyph, 14, accent ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.PrimaryText), TextBlock(label)), onClick)
            .IsEnabled(enabled)
            .AutomationId(automationId)
            .AutomationName(label);
        return accent ? button.ApplyStyle("AccentButtonStyle") : button;
    }

    internal static Element IconButton(string glyph, string label, Action onClick, string automationId, bool enabled = true) =>
        Button(Glyph(glyph, 14, Theme.PrimaryText), onClick)
            .IsEnabled(enabled)
            .Padding(8, 6)
            .ToolTip(label)
            .AutomationId(automationId)
            .AutomationName(label);

    /// <summary>Elevated surface used for every section.</summary>
    internal static BorderElement Surface(Element child, double padding = 16) =>
        Border(child)
            .Padding(padding)
            .CornerRadius(CardRadius)
            .Background(Theme.CardBackground)
            .BorderBrush(Theme.CardStroke)
            .BorderThickness(1);

    internal static Element Section(string title, string glyph, params Element?[] children) =>
        TintedSection(title, glyph, Theme.AccentText, Theme.SystemAttentionBackground, children);

    internal static Element TintedSection(string title, string glyph, ThemeRef brush, ThemeRef fill, params Element?[] children) =>
        Surface(VStack(12,
            HStack(10,
                Border(Glyph(glyph, 14, brush).Center()).Size(28, 28).CornerRadius(8).Background(fill)
                    .BorderBrush(Theme.Ref("AutomationsTileStrokeBrush")).BorderThickness(1),
                SectionTitle(title).VAlign(VerticalAlignment.Center)),
            VStack(12, children)));

    /// <summary>Label/value pair for read-only detail grids. Values are selectable.</summary>
    internal static Element Fact(string label, string? value, string automationId) =>
        string.IsNullOrWhiteSpace(value)
            ? Empty()
            : Grid([GridSize.Px(160), GridSize.Star()], [GridSize.Auto],
                Secondary(label, 13).Grid(column: 0),
                TextBlock(value!).FontSize(13).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true)
                    .AutomationId(automationId).Grid(column: 1));

    internal static Element Pill(string text, ThemeRef foreground, ThemeRef background, string? glyph = null) =>
        Border(HStack(4,
                glyph is null ? Empty() : Glyph(glyph, 11, foreground),
                TextBlock(text).FontSize(12).FontWeight(FontWeights.SemiBold).Foreground(foreground)))
            .Padding(8, 2)
            .CornerRadius(10)
            .Background(background)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);

    internal static (ThemeRef Foreground, ThemeRef Background, string Glyph) StatusTone(CronJob job, bool running) =>
        running || job.IsRunning ? (Theme.AccentText, Theme.SystemAttentionBackground, GlyphPlay)
        : job.IsAutoDisabled ? (Theme.SystemCritical, Theme.SystemCriticalBackground, GlyphLock)
        : job.IsFailing ? (Theme.SystemCritical, Theme.SystemCriticalBackground, GlyphError)
        : job.IsCompletedOneShot ? (Theme.SystemSuccess, Theme.SystemSuccessBackground, GlyphOk)
        : job.Enabled ? (Theme.SystemSuccess, Theme.SystemSuccessBackground, GlyphOk)
        : (Theme.SecondaryText, Theme.SystemNeutralBackground, GlyphPause);

    internal static (ThemeRef Brush, string Glyph) RunTone(string? status) => status switch
    {
        "ok" => (Theme.SystemSuccess, GlyphOk),
        "error" => (Theme.SystemCritical, GlyphError),
        "skipped" => (Theme.SystemCaution, GlyphSkip),
        _ => (Theme.SecondaryText, GlyphClock),
    };

    internal static Element ErrorText(string? text, string automationId) =>
        string.IsNullOrEmpty(text)
            ? Empty()
            : TextBlock(text).FontSize(12).Foreground(Theme.SystemCritical).TextWrapping(TextWrapping.Wrap)
                .AutomationId(automationId).LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

    internal static Element Hero(string glyph, string title, string body, string automationId, params Element?[] actions) =>
        VStack(12,
                Border(Glyph(glyph, 28, Theme.AccentText).Center())
                    .Size(72, 72).CornerRadius(24)
                    .Background(Theme.Ref("AutomationsHeroBrush"))
                    .BorderBrush(Theme.Ref("AutomationsHeroStrokeBrush")).BorderThickness(1)
                    .HAlign(HorizontalAlignment.Center),
                TextBlock(title).FontSize(20).FontWeight(FontWeights.SemiBold)
                    .TextAlignment(TextAlignment.Center).TextWrapping(TextWrapping.Wrap).HAlign(HorizontalAlignment.Center),
                Secondary(body, 14).TextAlignment(TextAlignment.Center).MaxWidth(440).HAlign(HorizontalAlignment.Center),
                HStack(Gap, actions).HAlign(HorizontalAlignment.Center).Margin(0, 4, 0, 0))
            .MaxWidth(560)
            .HAlign(HorizontalAlignment.Center)
            .AutomationId(automationId);

    // ─── Personality: kind colours, tiles and motion ───

    internal const string GlyphRepeat = "\uE8EE";
    internal const string GlyphStopwatch = "\uE916";
    internal const string GlyphCommand = "\uE756";
    internal const string GlyphHeart = "\uEB51";
    internal const string GlyphPerson = "\uE77B";
    internal const string AutomationsIcon = "ms-appx:///Assets/SidebarIcons/Cron.svg";

    /// <summary>Fluent motion: short, decelerating, no bounce (DESIGN.md).</summary>
    internal static readonly Microsoft.UI.Reactor.Animation.Curve EnterCurve =
        Microsoft.UI.Reactor.Animation.Curve.Ease(200, Microsoft.UI.Reactor.Animation.Easing.Decelerate);

    internal static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(28);

    /// <summary>Hue, tinted fill and glyph for a schedule kind (Themes/AutomationsResources.xaml).</summary>
    internal readonly record struct KindTone(ThemeRef Brush, ThemeRef Fill, string Glyph);

    internal static KindTone Kind(CronScheduleKind kind) => kind switch
    {
        CronScheduleKind.Every => new(Theme.Ref("AutomationsKindEveryBrush"), Theme.Ref("AutomationsKindEveryFillBrush"), GlyphRepeat),
        CronScheduleKind.At => new(Theme.Ref("AutomationsKindAtBrush"), Theme.Ref("AutomationsKindAtFillBrush"), GlyphStopwatch),
        CronScheduleKind.Cron => new(Theme.Ref("AutomationsKindCronBrush"), Theme.Ref("AutomationsKindCronFillBrush"), GlyphCalendar),
        CronScheduleKind.Event => new(Theme.Ref("AutomationsKindEventBrush"), Theme.Ref("AutomationsKindEventFillBrush"), GlyphEvent),
        _ => new(Theme.Ref("AutomationsKindOtherBrush"), Theme.Ref("AutomationsKindOtherFillBrush"), GlyphCommand),
    };

    /// <summary>Rounded, tinted tile carrying the kind glyph. Decorative: the row or card names it.</summary>
    internal static BorderElement KindTile(CronScheduleKind kind, double size = 40, string? glyph = null, Element? content = null)
    {
        var tone = Kind(kind);
        return Border(content ?? Glyph(glyph ?? tone.Glyph, Math.Round(size * 0.46), tone.Brush).Center())
            .Size(size, size)
            .CornerRadius(Math.Round(size * 0.3))
            .Background(tone.Fill)
            .BorderBrush(Theme.Ref("AutomationsTileStrokeBrush"))
            .BorderThickness(1);
    }

    /// <summary>Small status dot ringed with the card colour so it reads on top of a tile.</summary>
    internal static Element StatusDot(ThemeRef brush, double size = 12) =>
        Border(Empty())
            .Size(size, size)
            .CornerRadius(size / 2)
            .Background(brush)
            .BorderBrush(Theme.CardBackground)
            .BorderThickness(2)
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

    /// <summary>A dot with a slow breathing halo. Static when the user prefers reduced motion.</summary>
    internal static Element BreathingDot(ThemeRef brush, bool animate, double size = 10)
    {
        var halo = Border(Empty())
            .Size(size * 2.2, size * 2.2)
            .CornerRadius(size * 1.1)
            .Background(brush)
            .Opacity(animate ? 0.22 : 0)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);
        if (animate)
        {
            halo = halo.Keyframes("breathe", true, kf => kf
                .Duration(2400)
                .Loop()
                .At(0f, opacity: 0.05f)
                .At(0.5f, opacity: 0.32f, easing: Microsoft.UI.Reactor.Animation.Easing.EaseInOut)
                .At(1f, opacity: 0.05f, easing: Microsoft.UI.Reactor.Animation.Easing.EaseInOut));
        }
        return Grid([GridSize.Px(size * 2.2)], [GridSize.Px(size * 2.2)],
                halo,
                Border(Empty()).Size(size, size).CornerRadius(size / 2).Background(brush)
                    .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    /// <summary>Accent-washed band that gives the page header its own identity.</summary>
    internal static BorderElement Wash(Element child) =>
        Border(child)
            .Background(Theme.Ref("AutomationsHeroBrush"))
            .BorderBrush(Theme.Ref("AutomationsHeroStrokeBrush"))
            .BorderThickness(1)
            .CornerRadius(12);

    /// <summary>The colourful Automations artwork, or its Fluent glyph in high contrast.</summary>
    internal static Element AppIcon(bool highContrast, double size) =>
        highContrast
            ? Glyph(GlyphCalendar, size * 0.8, Theme.PrimaryText)
            : Image(AutomationsIcon).Size(size, size).AccessibilityHidden();
}

/// <summary>Rendering preferences read once at the root and threaded to static builders.</summary>
internal readonly record struct AutomationsLook(bool ReduceMotion, bool HighContrast);

internal static class AutomationsMotion
{
    /// <summary>Fade-in on mount (outgoing content leaves at once); nothing when the user prefers reduced motion.</summary>
    internal static T Enter<T>(this T element, bool reduceMotion) where T : Element =>
        reduceMotion
            ? element
            : element.Transition(Microsoft.UI.Reactor.Animation.Transition.Enter(Microsoft.UI.Reactor.Animation.Transition.Fade), AutomationsVisuals.EnterCurve);
}
