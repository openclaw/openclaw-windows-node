using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Cron;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Automations;

/// <summary>Everything the Automations tree needs from its WinUI host.</summary>
/// <param name="OpenWeb">Opens Automations in the Gateway web dashboard, at one job when given its ID.</param>
/// <param name="CopyText">Copies text to the clipboard; false when Windows refused.</param>
/// <param name="KnownAgents">Agent IDs the app knows on the connected Gateway, for the agent filter.</param>
internal sealed record AutomationsHost(
    AutomationsStore Store,
    AutomationsText Text,
    Func<IOperatorGatewayClient?> Client,
    Action OpenConnection,
    Action<string> OpenChat,
    Action<string?> OpenWeb,
    Func<string, bool> CopyText,
    Func<IReadOnlyList<string>> KnownAgents)
{
    /// <summary>Asks for confirmation (title, body, primary label). Bound by the root to its controlled dialog.</summary>
    public Func<ConfirmRequest, Task<bool>> Confirm { get; init; } = static _ => Task.FromResult(false);
    public Action<FrameworkElement>? RevealElement { get; init; }
    public Func<string?> GatewayDisplayName { get; init; } = static () => null;
    public Func<ElementTheme> ActualTheme { get; init; } = static () => ElementTheme.Default;
    public Func<string> DraftScope { get; init; } = Guid.NewGuid().ToString;
    /// <summary>Carries an outside "show this job" request (Hub search) to the mounted root. One instance per host, shared by the copies the root makes.</summary>
    public AutomationsRevealChannel Reveals { get; init; } = new();
    public AutomationsViewportState Viewport { get; init; } = new();
}

/// <summary>
/// Shared geometry and building blocks. Spacing follows DESIGN.md steps
/// (4/8/12/16/24/32) and every colour is a theme resource so light, dark
/// and high contrast stay correct.
/// </summary>
internal static class AutomationsVisuals
{
    internal const double Gap = 8;
    internal const double AgendaColumnsMinWidth = 820;
    internal const double SectionGap = 16;
    internal const double PagePadding = 24;
    internal const double EditorHeight = 640;
    internal const double DialogMaxWidth = 560;
    internal const double DialogWindowMargin = 48;
    internal const double DialogFactsStackWidth = 400;
    internal const double DialogBadgeSize = 36;
    internal const double DialogFactLabelWidth = 120;
    internal const double ToastMaxWidth = 480;
    internal const double ReadingWidth = 640;
    internal const double CardRadius = 8;
    internal const string MonoFont = "Cascadia Mono, Consolas";

    internal const string GlyphPlay = "\uE768";
    internal const string GlyphPause = "\uE769";
    internal const string GlyphEdit = "\uE70F";
    internal const string GlyphMore = "\uE712";
    internal const string GlyphDelete = "\uE74D";
    internal const string GlyphRefresh = "\uE72C";
    internal const string GlyphAdd = "\uE710";
    internal const string GlyphCalendar = "\uE787";
    internal const string GlyphClock = "\uE823";
    internal const string GlyphChat = "\uE8BD";
    internal const string GlyphSend = "\uE724";
    internal const string GlyphAlert = "\uE7BA";
    internal const string GlyphError = "\uEA39";
    internal const string GlyphOk = "\uE73E";
    internal const string GlyphSkip = "\uE893";
    internal const string GlyphBranch = "\uE8AB";
    internal const string GlyphSearch = "\uE721";
    internal const string GlyphLock = "\uE72E";
    internal const string GlyphEvent = "\uE945";
    internal const string GlyphSparkle = "\uE9D5";
    internal const string GlyphOpenInBrowser = "\uE8A7";
    internal const string GlyphCopy = "\uE8C8";

    internal static TextBlockElement Glyph(string glyph, double size = 16, ThemeRef? brush = null) =>
        TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(size)
            .FontWeight(FontWeights.Normal)
            .Foreground(brush ?? Theme.SecondaryText)
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

    internal static TextBlockElement Secondary(string text, double size = 12) =>
        TextBlock(text).FontSize(size).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.WrapWholeWords)
            .MaxWidth(ReadingWidth).HAlign(HorizontalAlignment.Left);

    internal static TextBlockElement SingleLine(string text, double size = 12) =>
        TextBlock(text).FontSize(size).TextWrapping(TextWrapping.NoWrap).MaxLines(1)
            .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(text);

    internal static Element GlyphButton(string glyph, string label, Action onClick, string automationId, bool enabled = true, bool accent = false)
    {
        var button = Button(HStack(Gap, Glyph(glyph, 14, accent ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.PrimaryText), TextBlock(label).TextWrapping(TextWrapping.NoWrap)), onClick)
            .IsEnabled(enabled)
            .AutomationId(automationId)
            .AutomationName(label).WithKey(automationId);
        return accent ? button.ApplyStyle("AccentButtonStyle") : button;
    }

    internal static Element IconButton(string glyph, string label, Action onClick, string automationId, bool enabled = true) =>
        Button(Glyph(glyph, 14, Theme.PrimaryText), onClick)
            .IsEnabled(enabled)
            .ToolTip(label)
            .ApplyStyle("SubtleButtonStyle")
            .AutomationId(automationId)
            .AutomationName(label).WithKey(automationId);

    internal static Element ChatButton(AutomationsHost host, string sessionKey, string title, string id) =>
        IconButton(GlyphChat, host.Text["Runs_OpenChat"], () => host.OpenChat(sessionKey), id)
            .AutomationName(host.Text.Format("Redesign_OpenChatFor", title));

    internal static Element AgentAvatar(string agent) =>
        Border(TextBlock(StringInfo.GetNextTextElement(agent).ToUpper(CultureInfo.CurrentCulture))
            .FontSize(14).FontWeight(FontWeights.SemiBold).Foreground(Theme.AccentText).Center())
            .Size(32, 32).CornerRadius(16).Background(Theme.SystemAttentionBackground)
            .AutomationName(agent);

    internal static Element DialogHeader(string glyph, string title, bool destructive, string titleId) =>
        Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Border(Glyph(glyph, 16, destructive ? Theme.SystemCritical : Theme.AccentText).Center())
                .Size(DialogBadgeSize, DialogBadgeSize).CornerRadius(DialogBadgeSize / 2)
                .Background(destructive ? Theme.SystemCriticalBackground : Theme.SystemAttentionBackground)
                .Margin(0, 0, 12, 0).VAlign(VerticalAlignment.Top).Grid(column: 0),
            TextBlock(title).FontSize(20).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.Wrap)
                .VAlign(VerticalAlignment.Center).AutomationId(titleId).Grid(column: 1));

    internal static bool IsAdvanced(CronJob job) =>
        job.Schedule.IsWebManaged || job.Schedule.Kind == CronScheduleKind.Cron
            && CronCalendar.TryParse(job.Schedule.Expr) is not { Pattern: CronCalendarPattern.Daily or CronCalendarPattern.Weekdays or CronCalendarPattern.Weekly }
        || job.Payload.IsReadOnly || job.HasTrigger;

    /// <summary>Elevated surface used for every section.</summary>
    internal static BorderElement Surface(Element child, double padding = 16) =>
        Border(child)
            .Padding(padding)
            .CornerRadius(CardRadius)
            .Background(Theme.CardBackground)
            .BorderBrush(Theme.CardStroke)
            .BorderThickness(1);

    /// <summary>Label/value pair for read-only detail grids. Values are selectable.</summary>
    internal static Element Fact(string label, string? value, string automationId) =>
        string.IsNullOrWhiteSpace(value)
            ? Empty()
            : Component<AutomationFact, AutomationFactProps>(new(label, value!, automationId));

    internal static Element Pill(string text, ThemeRef foreground, ThemeRef background, string? glyph = null) =>
        Border(HStack(4,
                glyph is null ? Empty() : Glyph(glyph, 11, foreground),
                TextBlock(text).FontSize(12).FontWeight(FontWeights.SemiBold).Foreground(foreground).TextWrapping(TextWrapping.NoWrap)))
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
            : TextBlock(text).FontSize(12).Foreground(Theme.SystemCritical).TextWrapping(TextWrapping.WrapWholeWords).MaxWidth(ReadingWidth)
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

    internal static async Task CloseTransientSurfacesAsync(DependencyObject root)
    {
        var closed = new List<Task>();
        void Visit(DependencyObject node)
        {
            if (node is CommandBar { IsOpen: true } bar)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<object>? handler = null;
                handler = (_, _) => { bar.Closed -= handler; completion.TrySetResult(); };
                bar.Closed += handler;
                closed.Add(completion.Task);
                bar.IsOpen = false;
            }
            if (node is Button { Flyout: { IsOpen: true } flyout })
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<object>? handler = null;
                handler = (_, _) => { flyout.Closed -= handler; completion.TrySetResult(); };
                flyout.Closed += handler;
                closed.Add(completion.Task);
                flyout.Hide();
            }
            for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); index++)
                Visit(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, index));
        }
        Visit(root);
        await Task.WhenAll(closed);
    }

    // ─── Personality: kind colours, tiles and motion ───

    internal const string GlyphRepeat = "\uE8EE";
    internal const string GlyphStopwatch = "\uE916";
    internal const string GlyphCommand = "\uE756";
    internal const string AutomationsIcon = "ms-appx:///Assets/SidebarIcons/Cron.svg";

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

    internal sealed record AutomationFactProps(string Label, string Value, string Id);

    internal sealed class AutomationFact : Component<AutomationFactProps>
    {
        public override Element Render()
        {
            var (stacked, setStacked) = UseState(false);
            return Grid(stacked ? [GridSize.Star()] : [GridSize.Px(AutomationsVisuals.DialogFactLabelWidth), GridSize.Star()],
                stacked ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                AutomationsVisuals.Secondary(Props.Label, 13).Grid(column: 0),
                TextBlock(Props.Value).FontSize(13).TextWrapping(TextWrapping.WrapWholeWords).IsTextSelectionEnabled(true)
                    .AutomationId(Props.Id).Grid(column: stacked ? 0 : 1, row: stacked ? 1 : 0))
                .OnSizeChanged((_, args) => setStacked(args.NewSize.Width < AutomationsVisuals.DialogFactsStackWidth));
        }
    }

    internal sealed record AutomationRowLabelsProps(
        string Title,
        string Subtitle,
        string? Tag,
        string JobId,
        bool IsFailing,
        bool IsAutoDisabled,
        string? FailureReason,
        int? ConsecutiveErrors,
        AutomationsText Text,
        bool ReduceMotion);

    internal sealed class AutomationRowLabels : Component<AutomationRowLabelsProps>
    {
        public override Element Render()
        {
            var (width, setWidth) = UseState(0d);
            double NaturalWidth(string text, bool bold = false)
            {
                var probe = new TextBlock { Text = text, FontSize = 12, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
                probe.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return probe.DesiredSize.Width;
            }
            var showTag = Props.Tag is { } tag && NaturalWidth(Props.Subtitle) + NaturalWidth(tag, true) + 24 <= width;
            var failureDetail = string.Join(" ", new[]
            {
                Props.ConsecutiveErrors is { } count ? Props.Text.Format("Redesign_FailureStreak", count) : null,
                Props.FailureReason,
                Props.IsAutoDisabled ? Props.Text["Review_SchedulerStopped"] : null,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var titleBadge = Props.IsAutoDisabled
                ? (Element)PageHero.AttentionBadge($"AutomationAttentionBadge:{Props.JobId}",
                    Props.Text["Scheduler_Off"], failureDetail, GlyphLock, PageHeroTone.Critical, Props.ReduceMotion,
                    reconciliationKey: "AutomationRowStatusBadge")
                : Props.IsFailing
                    ? PageHero.AttentionBadge($"AutomationAttentionBadge:{Props.JobId}",
                        Props.Text["State_Failing"], failureDetail, GlyphError, PageHeroTone.Critical, Props.ReduceMotion,
                        reconciliationKey: "AutomationRowStatusBadge")
                    : Empty();
            return PageHero.WithMotion(
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
                    Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        AutomationsVisuals.SingleLine(Props.Title, 14).FontWeight(FontWeights.SemiBold)
                            .AutomationId($"CronJobTitle:{Props.JobId}").Grid(column: 0),
                        titleBadge.VAlign(VerticalAlignment.Center).Grid(column: 1))
                        .Grid(column: 0, row: 0, columnSpan: 2),
                    AutomationsVisuals.SingleLine(Props.Subtitle).Foreground(Theme.SecondaryText).Margin(0, 4, 0, 0).Grid(row: 1),
                    (showTag ? AutomationsVisuals.Pill(Props.Tag!, Theme.SecondaryText, Theme.SystemNeutralBackground).Margin(8, 4, 0, 0) : Empty())
                        .Grid(column: 1, row: 1)),
                Props.ReduceMotion, staggerChildren: false)
                .OnSizeChanged((_, args) => setWidth(args.NewSize.Width));
        }
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

    /// <summary>The colourful Automations artwork, or its Fluent glyph in high contrast.</summary>
    internal static Element AppIcon(bool highContrast, double size) =>
        highContrast
            ? Glyph(GlyphCalendar, size * 0.8, Theme.PrimaryText)
            : Image(AutomationsIcon).Size(size, size).AccessibilityHidden();
}

/// <summary>Rendering preferences read once at the root and threaded to static builders.</summary>
internal readonly record struct AutomationsLook(bool ReduceMotion, bool HighContrast);
