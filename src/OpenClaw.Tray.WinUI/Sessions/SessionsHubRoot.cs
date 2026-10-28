using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

internal sealed record SessionsHubRootProps(SessionsHubController Controller);

/// <summary>
/// Reactor root for the Settings Sessions page: the Sessions and Worktrees tabs,
/// connection and notice bars, and the active tab body. State and requests are
/// owned by <see cref="SessionsHubController"/>; this tree only renders snapshots
/// and forwards user intent.
/// </summary>
internal sealed class SessionsHubRoot : Component<SessionsHubRootProps>
{
    internal const double WideLayoutWidth = 1080;
    private const double MaxContentWidth = 1600;
    private static readonly IReadOnlySet<string> NoCollapsedGroups = new HashSet<string>(StringComparer.Ordinal);

    public override Element Render()
    {
        var controller = Props.Controller;
        var (state, setState) = UseState(controller.State, threadSafe: true);
        var (width, setWidth) = UseState(1200d);
        var (collapsed, setCollapsed) = UseState(NoCollapsedGroups);

        UseEffect((Func<Action>)(() =>
        {
            Action changed = () => setState(controller.State);
            controller.Changed += changed;
            setState(controller.State);
            return () => controller.Changed -= changed;
        }), controller);

        var context = new SessionsHubViewContext(
            controller, state, width, DateTime.UtcNow, collapsed, setCollapsed);

        var body = state.Tab == SessionsHubTab.Worktrees
            ? SessionsHubWorktreesView.Render(context)
            : SessionsHubSessionsView.Render(context);
        return Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
                    Header(context).Grid(row: 0),
                    Banners(context).Grid(row: 1),
                    Stats(context).Grid(row: 2),
                    body.Grid(row: 3),
                    SessionsHubDialogView.Render(context))
                .MaxWidth(MaxContentWidth)
                // Stretch (not Left) so star columns get real width on every tab; MaxWidth still caps it.
                .HAlign(HorizontalAlignment.Stretch))
            .Padding(24, 20, 24, 12)
            .OnSizeChanged((_, args) =>
            {
                if (Math.Abs(args.NewSize.Width - width) > 1)
                    setWidth(args.NewSize.Width);
            })
            .AutomationId("SessionsHubRoot");
    }

    private static Element Header(SessionsHubViewContext context)
    {
        var state = context.State;
        var controller = context.Controller;
        string T(string key) => SessionsHubPresentation.T(key);
        var tabs = SelectorBar(
                [SelectorBarItem(T("TabSessions")), SelectorBarItem(T("TabWorktrees"))],
                state.Tab == SessionsHubTab.Worktrees ? 1 : 0,
                index =>
                {
                    if (index is 0 or 1)
                        controller.SetTab(index == 1 ? SessionsHubTab.Worktrees : SessionsHubTab.Sessions);
                })
            .Set(bar => SessionsHubView.TagItems(bar, "SessionsHubTab", ["Sessions", "Worktrees"]))
            .AutomationName(T("TabsLabel"))
            .Margin(-12, 4, 0, 0);
        var refresh = SessionsHubView.CommandButton(FluentIconCatalog.Refresh, T("Refresh"), () =>
            {
                if (state.Tab == SessionsHubTab.Worktrees) _ = controller.LoadWorktreesAsync();
                else _ = controller.ReloadAsync(preserveCount: true);
            },
            enabled: state.IsConnected, automationId: "SessionsPageRefresh");
        return Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
                VStack(2,
                    Title(T("Title")).HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1)
                        .AutomationId("SessionsPageMarker"),
                    TextBlock(T("Subtitle")).FontSize(12).Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.Wrap))
                    .Grid(row: 0, column: 0),
                HStack(8,
                        When(state.Load == SessionsHubLoad.Loading && state.Rows.Count > 0
                            || state.Worktrees.Load == SessionsHubLoad.Loading && state.Tab == SessionsHubTab.Worktrees,
                            () => ProgressRing().IsActive(true).Size(16, 16).AutomationName(T("Loading"))),
                        refresh)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 1),
                tabs.Grid(row: 1, column: 0, columnSpan: 2))
            .Margin(0, 0, 0, 8);
    }

    /// <summary>At-a-glance figures for the loaded roster, each in its own hue.</summary>
    private static Element Stats(SessionsHubViewContext context)
    {
        var state = context.State;
        if (state.Tab != SessionsHubTab.Sessions || !state.IsConnected || state.Load != SessionsHubLoad.Loaded || state.Rows.Count == 0)
            return Empty();
        string T(string key) => SessionsHubPresentation.T(key);
        var rows = state.Filters.ApplyClientFilters(state.Rows).ToArray();
        var tones = rows.Select(row => SessionsHubPresentation.Status(row).Tone).ToArray();
        // Once every page is loaded the visible rows are exact; the server total also counts hidden background rows.
        var total = state.HasMore ? state.TotalCount ?? rows.Length : rows.Length;
        string N(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var label = state.Filters.Status switch
        {
            OpenClaw.Shared.Sessions.SessionArchiveFilter.Archived => T("StatArchived"),
            OpenClaw.Shared.Sessions.SessionArchiveFilter.All => T("StatAll"),
            _ => T("StatActive"),
        };
        var tiles = new List<Element>
        {
            SessionsHubView.StatTile(FluentIconCatalog.Sessions, "blue", N(total), label, "SessionsHubStatTotal"),
            SessionsHubView.StatTile(FluentIconCatalog.Sync, "green",
                N(tones.Count(tone => tone == SessionsHubPresentation.StatusTone.Working)), T("StatWorking"), "SessionsHubStatWorking"),
            SessionsHubView.StatTile(FluentIconCatalog.StatusWarn, "red",
                N(tones.Count(tone => tone == SessionsHubPresentation.StatusTone.Attention)), T("StatAttention"), "SessionsHubStatAttention"),
        };
        if (context.Width >= 760)
        {
            tiles.Add(SessionsHubView.StatTile(FluentIconCatalog.MarkUnread, "orange", N(rows.Count(row => row.Unread)), T("StatUnread"),
                "SessionsHubStatUnread"));
            tiles.Add(SessionsHubView.StatTile(FluentIconCatalog.Pin, "purple", N(rows.Count(row => row.Pinned)), T("StatPinned"),
                "SessionsHubStatPinned"));
        }
        var cells = tiles.Select((tile, index) => tile.Margin(index == 0 ? 0 : 6, 0, index == tiles.Count - 1 ? 0 : 6, 0)
            .Grid(column: index).WithKey("stat" + index)).ToArray();
        return Grid(Enumerable.Repeat(GridSize.Star(), tiles.Count).ToArray(), [GridSize.Auto], cells)
            .WithTransitions(new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { IsStaggeringEnabled = true })
            .Margin(0, 4, 0, 14)
            .AutomationName(T("StatsLabel"))
            .AutomationId("SessionsHubStats");
    }

    private static Element Banners(SessionsHubViewContext context)
    {
        var state = context.State;
        var controller = context.Controller;
        var children = new List<Element>();
        if (!state.IsConnected)
        {
            children.Add((InfoBar(
                        LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Title"),
                        LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Message")) with
                    {
                        IsOpen = true,
                        ActionButtonContent = LocalizationHelper.GetString("SessionsPage_OpenConnection.Content"),
                        OnActionButtonClick = controller.OpenConnection,
                    })
                .Severity(InfoBarSeverity.Warning)
                .IsClosable(false)
                .AutomationId("SessionsPageDisconnected")
                .WithKey("disconnected"));
        }
        else if (state.Tab == SessionsHubTab.Sessions && !state.Permissions.CanWrite)
        {
            children.Add((InfoBar(SessionsHubPresentation.T("ReadOnlyTitle"), SessionsHubPresentation.T("ReadOnlyBody")) with { IsOpen = true })
                .Severity(InfoBarSeverity.Informational)
                .IsClosable(false)
                .AutomationId("SessionsHubReadOnly")
                .WithKey("readonly"));
        }
        if (state.Tab == SessionsHubTab.Sessions && state.Load == SessionsHubLoad.Failed && state.Rows.Count > 0)
        {
            children.Add((InfoBar(SessionsHubPresentation.T("LoadFailedTitle"), state.LoadError) with
                {
                    IsOpen = true,
                    ActionButtonContent = SessionsHubPresentation.T("Retry"),
                    OnActionButtonClick = () => _ = controller.ReloadAsync(preserveCount: true),
                })
                .Severity(InfoBarSeverity.Error).IsClosable(false).LiveRegion()
                .AutomationId("SessionsHubReloadFailed").WithKey("reload-failed"));
        }
        if (state.Tab == SessionsHubTab.Worktrees && state.Worktrees.Load == SessionsHubLoad.Failed
            && state.Worktrees.Rows is { Count: > 0 })
        {
            children.Add((InfoBar(SessionsHubPresentation.T("WorktreesFailed"), state.Worktrees.Error) with
                {
                    IsOpen = true,
                    ActionButtonContent = SessionsHubPresentation.T("Retry"),
                    OnActionButtonClick = () => _ = controller.LoadWorktreesAsync(),
                })
                .Severity(InfoBarSeverity.Error).IsClosable(false).LiveRegion()
                .AutomationId("SessionsHubWorktreesReloadFailed").WithKey("worktrees-reload-failed"));
        }
        if (state.Notice is { } notice)
        {
            var bar = InfoBar(notice.Title.Length > 0 ? notice.Title : null, notice.Message) with
            {
                IsOpen = true,
                ActionButtonContent = notice.ActionLabel,
                OnActionButtonClick = notice.Action is { } act ? () => { act(); } : null,
            };
            children.Add(bar
                .Severity(notice.Severity)
                .IsClosable(true)
                .Closed(controller.DismissNotice)
                .LiveRegion()
                .AutomationId("SessionsHubNotice")
                .WithKey("notice:" + notice.GetHashCode()));
        }
        return VStack(8, children.ToArray()).Margin(0, 0, 0, children.Count > 0 ? 8 : 0);
    }
}

/// <summary>Per-render inputs shared by the tab views.</summary>
internal sealed record SessionsHubViewContext(
    SessionsHubController Controller,
    SessionsHubState State,
    double Width,
    DateTime NowUtc,
    IReadOnlySet<string> CollapsedGroups,
    Action<IReadOnlySet<string>> SetCollapsedGroups)
{
    public bool IsWide => Width >= SessionsHubRoot.WideLayoutWidth;
}

/// <summary>Small shared view builders: glyphs, buttons, badges, and empty states.</summary>
internal static class SessionsHubView
{
    public static Element Glyph(string glyph, double size = 16, ThemeRef? foreground = null)
    {
        var text = TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(size)
            .Set(block => block.IsTextScaleFactorEnabled = false)
            .AccessibilityView(AccessibilityView.Raw);
        return foreground is { } brush ? text.Foreground(brush) : text;
    }

    public static Element CommandButton(
        string glyph, string label, Action onClick, bool enabled = true, string? automationId = null,
        bool accent = false, string? tooltip = null)
    {
        var button = Button(HStack(8, Glyph(glyph, 14).VAlign(VerticalAlignment.Center), TextBlock(label).VAlign(VerticalAlignment.Center)), onClick)
            .AutomationName(label);
        if (accent) button = button.AccentButton();
        return button
            .IsEnabled(enabled)
            .ToolTip(tooltip ?? label)
            .AutomationId(automationId ?? "");
    }

    public static Element IconButton(string glyph, string label, Action onClick, bool enabled = true, string? automationId = null) =>
        Button(Glyph(glyph, 14), onClick)
            .SubtleButton()
            .IsEnabled(enabled)
            .AutomationName(label)
            .ToolTip(label)
            .Padding(8, 6)
            .AutomationId(automationId ?? "");

    public static Element Badge(string text, ThemeRef background, ThemeRef foreground, string? automationId = null) =>
        Border(TextBlock(text).FontSize(12).Foreground(foreground).TextTrimming(TextTrimming.CharacterEllipsis))
            .Background(background)
            .CornerRadius(4)
            .Padding(6, 1)
            .VAlign(VerticalAlignment.Center)
            .AutomationId(automationId ?? "");

    public static Element NeutralBadge(string text, string? automationId = null) =>
        Badge(text, Theme.Ref("SubtleFillColorSecondaryBrush"), Theme.SecondaryText, automationId);

    public static Element EmptyState(string glyph, string title, string body, Element? action = null, string? automationId = null,
        string colorId = "blue") =>
        VStack(8,
                Border(Glyph(glyph, 32, Strong(colorId)).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                    .Size(80, 80).CornerRadius(40).Background(Tint(colorId))
                    .HAlign(HorizontalAlignment.Center).Margin(0, 0, 0, 8),
                TextBlock(title).FontSize(16).FontWeight(FontWeights.SemiBold)
                    .TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Center),
                TextBlock(body).FontSize(13).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap)
                    .TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Center).MaxWidth(420),
                action is null ? Empty() : action.HAlign(HorizontalAlignment.Center).Margin(0, 8, 0, 0))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 48, 0, 48)
            .LiveRegion()
            .AutomationId(automationId ?? "SessionsHubEmpty");

    // ── Palette (Themes/SessionsHubResources.xaml) ──

    public static ThemeRef Strong(string colorId) =>
        Theme.Ref($"SessionColor{SessionsHubPresentation.Pascal(colorId)}Brush");

    public static ThemeRef Tint(string colorId) =>
        Theme.Ref($"SessionColor{SessionsHubPresentation.Pascal(colorId)}TintBrush");

    /// <summary>Identity avatar: the agent's emoji, or initials, on its palette tint.</summary>
    public static Element Avatar(string? emoji, string name, string colorId, double size)
    {
        var hasEmoji = !string.IsNullOrWhiteSpace(emoji);
        return Border(TextBlock(hasEmoji ? emoji!.Trim() : OpenClaw.Shared.Sessions.SessionAppearance.Initials(name))
                    .FontSize(Math.Round(size * (hasEmoji ? 0.5 : 0.38)))
                    .FontWeight(FontWeights.SemiBold)
                    .Foreground(Strong(colorId))
                    .Set(block => block.IsTextScaleFactorEnabled = false)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
            .Size(size, size)
            .CornerRadius(size / 2)
            .Background(Tint(colorId))
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw);
    }

    public static Element AgentAvatar(SessionsHubAgent? agent, string fallbackName, double size) =>
        Avatar(agent?.Emoji, agent?.Name ?? fallbackName,
            OpenClaw.Shared.Sessions.SessionAppearance.PaletteColor(agent?.Id ?? fallbackName), size);

    /// <summary>A rounded label in a palette hue; the text always names the meaning, never color alone.</summary>
    public static Element Pill(string text, string colorId, string? glyph = null, string? automationId = null)
    {
        var parts = new List<Element>();
        if (glyph is not null)
            parts.Add(Glyph(glyph, 11, Strong(colorId)).VAlign(VerticalAlignment.Center));
        parts.Add(TextBlock(text).FontSize(12).FontWeight(FontWeights.SemiBold).Foreground(Strong(colorId))
            .TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center));
        return Border(HStack(4, parts.ToArray()))
            .Background(Tint(colorId))
            .CornerRadius(10)
            .Padding(8, 1, 8, 2)
            .VAlign(VerticalAlignment.Center)
            .AutomationId(automationId ?? "");
    }

    public static Element ColorDot(string colorId, double size = 10) =>
        Border(Empty())
            .Size(size, size)
            .CornerRadius(size / 2)
            .Background(Strong(colorId))
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw);

    public static Element IconTile(string glyph, string colorId, double size = 36) =>
        Border(Glyph(glyph, Math.Round(size * 0.45), Strong(colorId))
                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .Size(size, size)
            .CornerRadius(size / 4)
            .Background(Tint(colorId))
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw);

    /// <summary>Summary figure: tinted icon, large value, caption.</summary>
    public static Element StatTile(string glyph, string colorId, string value, string label, string automationId) =>
        Card(Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    IconTile(glyph, colorId, 40).Grid(column: 0),
                    VStack(0,
                            TextBlock(value).FontSize(22).FontWeight(FontWeights.SemiBold)
                                .Set(block => block.IsTextScaleFactorEnabled = false),
                            TextBlock(label).FontSize(12).Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis))
                        .VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0).Grid(column: 1)),
                14, 10)
            .AutomationName($"{value} {label}")
            .AutomationId(automationId);

    public static Element Card(Element child) => Card(child, 0, 0);

    public static Element Card(Element child, double horizontalPadding, double verticalPadding) =>
        Border(child)
            .Padding(horizontalPadding, verticalPadding)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(8);

    /// <summary>SelectorBar items are data records, so stable automation IDs are applied to the native items.</summary>
    public static void TagItems(SelectorBar bar, string prefix, IReadOnlyList<string> ids)
    {
        for (var i = 0; i < bar.Items.Count && i < ids.Count; i++)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(bar.Items[i], prefix + ids[i]);
    }

    public static MenuFlyoutItemData Item(string text, string? glyph, Action onClick, bool enabled = true) =>
        MenuItem(text, onClick) with { IconElement = glyph is null ? null : FontIcon(glyph), IsEnabled = enabled };
}
