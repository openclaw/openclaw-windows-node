using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

internal sealed record SessionsHubRootProps(
    SessionsHubController Controller,
    SessionsHubPresentationState Presentation);

internal sealed class SessionsHubPresentationState
{
    private double _topInset;

    public double TopInset => _topInset;
    public event Action? Changed;

    public void ApplyTopInset(double topInset)
    {
        if (!double.IsFinite(topInset) || topInset < 0)
            throw new ArgumentOutOfRangeException(nameof(topInset));
        if (_topInset.Equals(topInset))
            return;

        _topInset = topInset;
        Changed?.Invoke();
    }
}

/// <summary>
/// Reactor root for the Settings Sessions page: the Sessions and Worktrees tabs,
/// connection and notice bars, and the active tab body. State and requests are
/// owned by <see cref="SessionsHubController"/>; this tree only renders snapshots
/// and forwards user intent.
/// </summary>
internal sealed class SessionsHubRoot : Component<SessionsHubRootProps>
{
    private static readonly IReadOnlySet<string> NoCollapsedGroups = new HashSet<string>(StringComparer.Ordinal);

    public override Element Render()
    {
        var controller = Props.Controller;
        var presentation = Props.Presentation;
        var (state, setState) = UseState(controller.State, threadSafe: true);
        var (width, setWidth) = UseState(1200d);
        var (mastHeight, setMastHeight) = UseState(220d);
        var (topInset, setTopInset) = UseState(presentation.TopInset, threadSafe: true);
        var (collapsed, setCollapsed) = UseState(NoCollapsedGroups);
        var announce = this.UseAnnounce();

        UseEffect(() =>
        {
            if (state.Notice is { } notice)
                announce.Announce(NoticeMessage(notice), assertive: notice.Severity == InfoBarSeverity.Error);
        }, state.Notice);

        UseEffect((Func<Action>)(() =>
        {
            Action changed = () => setState(controller.State);
            controller.Changed += changed;
            setState(controller.State);
            return () => controller.Changed -= changed;
        }), controller);

        UseEffect((Func<Action>)(() =>
        {
            Action changed = () => setTopInset(presentation.TopInset);
            presentation.Changed += changed;
            setTopInset(presentation.TopInset);
            return () => presentation.Changed -= changed;
        }), presentation);

        var context = new SessionsHubViewContext(
            controller, state, width, DateTime.UtcNow, collapsed, setCollapsed);

        UseEffect((Func<Action>)(() =>
        {
            void RevealRestored(string key) => SessionsHubSessionsView.RevealDetails(context, key, focus: true);
            controller.ArchiveRestored += RevealRestored;
            return () => controller.ArchiveRestored -= RevealRestored;
        }), controller, collapsed, width);

        var body = state.Tab == SessionsHubTab.Worktrees
            ? SessionsHubWorktreesView.Render(context)
            : SessionsHubSessionsView.Render(context);
        var header = Border(Header(context, UseHighContrast()))
            .Width(context.ContentWidth)
            .Padding(0, 18, 0, 8)
            .Margin(0, 0, 0, 12)
            .HAlign(HorizontalAlignment.Center)
            .OnSizeChanged((_, args) =>
            {
                if (args.NewSize.Height > 0 && Math.Abs(args.NewSize.Height - mastHeight) > 1)
                    setMastHeight(args.NewSize.Height);
            })
            .AutomationId("SessionsHubContentColumn")
            .WithKey("header");
        var banners = Border(Banners(context).WithKey("banners"))
            .Width(context.ContentWidth)
            .HAlign(HorizontalAlignment.Center)
            .WithKey("banner-host");
        var pageBody = Border(body.WithKey("body"))
            .Width(context.ContentWidth)
            .Padding(0, 0, 0, 12)
            .HAlign(HorizontalAlignment.Center)
            .WithKey("body-host");
        var pageScroll = ScrollView(VStack(0, header, banners, pageBody))
            .AutomationId("SessionsHubPageScroll")
            .HAlign(HorizontalAlignment.Stretch)
            .Margin(0, topInset, 0, 0);
        return Grid([GridSize.Star()], [GridSize.Star()],
                Border(Empty()).Background(MastBrush())
                    .Height(mastHeight + topInset)
                    .HAlign(HorizontalAlignment.Stretch)
                    .VAlign(VerticalAlignment.Top)
                    .Set(border => border.IsHitTestVisible = false)
                    .AutomationId("SessionsHubMastBackdrop")
                    .Grid(row: 0),
                pageScroll.Grid(row: 0),
                Toast(context).Grid(row: 0).WithKey("notice-toast"),
                SessionsHubDialogView.Render(context).WithKey("dialog"),
                announce.Region.LiveRegion(state.Notice?.Severity == InfoBarSeverity.Error
                        ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite)
                    .AutomationId("SessionsHubAnnouncements").WithKey("announcements"))
            .AutomationId("SessionsHubRoot")
            .HAlign(HorizontalAlignment.Stretch)
            .OnSizeChanged((_, args) =>
            {
                if (Math.Abs(args.NewSize.Width - width) > 1)
                    setWidth(args.NewSize.Width);
            });
    }

    private static Element Header(SessionsHubViewContext context, bool useHighContrast)
    {
        var state = context.State;
        var controller = context.Controller;
        string T(string key) => SessionsHubPresentation.T(key);
        var (headline, subtitle) = SessionsHubPresentation.MastCopy(state);
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
            .Margin(-12, 12, 0, 0);
        var refresh = SessionsHubView.IconButton(FluentIconCatalog.Refresh, T("Refresh"), () =>
            {
                if (state.Tab == SessionsHubTab.Worktrees) _ = controller.LoadWorktreesAsync();
                else _ = controller.ReloadAsync(preserveCount: true);
            },
            enabled: state.IsConnected, automationId: "SessionsPageRefresh");
        var web = SessionsHubView.CommandButton("\uE8A7", T("OpenGatewayWeb"), controller.OpenDashboard,
            enabled: state.IsConnected, automationId: "SessionsHubOpenGatewayWeb");
        var eyebrowIcon = useHighContrast
            ? SessionsHubView.Glyph(FluentIconCatalog.Sessions, 20, Theme.SecondaryText)
                .AutomationId("SessionsHubEyebrowGlyph")
            : Image("ms-appx:///Assets/SidebarIcons/Sessions.svg")
                .Size(20, 20)
                .AccessibilityHidden()
                .AutomationId("SessionsHubEyebrowGlyph");
        var text = VStack(8,
                            HStack(PageResponsiveLayout.GapInner,
                                eyebrowIcon.VAlign(VerticalAlignment.Center),
                                TextBlock(T("Title")).FontSize(14).FontWeight(FontWeights.SemiBold)
                                    .Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
                                    .AutomationId("SessionsPageMarker")),
                            TextBlock(headline).FontSize(32).FontWeight(FontWeights.SemiBold)
                                .TextWrapping(TextWrapping.Wrap)
                                .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1)
                                .AutomationId("SessionsHubMastHeadline"),
                            TextBlock(subtitle).FontSize(14).Foreground(Theme.SecondaryText)
                                .TextWrapping(TextWrapping.WrapWholeWords))
                        .Grid(row: 0, column: 0);
        var actions = HStack(PageResponsiveLayout.GapInner,
                When(state.IsReloading
                    || state.Load == SessionsHubLoad.Loading && state.Rows.Count > 0
                    || state.Worktrees.Load == SessionsHubLoad.Loading && state.Tab == SessionsHubTab.Worktrees,
                    () => ProgressRing().IsActive(true).Size(16, 16).AutomationName(T("Loading")))
                    .WithKey("loading"),
                web.WithKey("web"),
                refresh.WithKey("refresh"))
            .VAlign(VerticalAlignment.Top)
            .AutomationId("SessionsHubMastActions");
        Element mast = context.IsNarrow
            ? Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto],
                text.Grid(row: 0),
                actions.HAlign(HorizontalAlignment.Left).Margin(0, PageResponsiveLayout.GapGroup, 0, 0).Grid(row: 1),
                tabs.Grid(row: 2).Margin(-12, 12, 0, 0))
            : Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
                text.Grid(row: 0, column: 0),
                actions.Grid(row: 0, column: 1),
                tabs.Grid(row: 1, column: 0, columnSpan: 2));
        return mast;
    }

    private static Brush MastBrush()
    {
        var accent = Application.Current.Resources["SystemAccentColor"] is Color color
            ? color
            : Color.FromArgb(255, 0, 103, 192);
        return new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(16, accent.R, accent.G, accent.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 },
            },
        };
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
        else if (state.Tab == SessionsHubTab.Sessions && state.Permissions.CanWrite && !state.Permissions.IsAdmin)
        {
            children.Add((InfoBar(SessionsHubPresentation.T("WriteOnlyTitle"), SessionsHubPresentation.T("WriteOnlyBody")) with
                {
                    IsOpen = true,
                })
                .Severity(InfoBarSeverity.Informational)
                .IsClosable(false)
                .AutomationId("SessionsHubWriteOnly")
                .WithKey("write-only"));
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
        return VStack(8, children.ToArray()).Margin(0, 0, 0, children.Count > 0 ? 8 : 0);
    }

    private static Element Toast(SessionsHubViewContext context)
    {
        if (context.State.Notice is not { } notice)
            return Empty();
        var (glyph, color) = notice.Severity switch
        {
            InfoBarSeverity.Error => (FluentIconCatalog.StatusErr, "red"),
            InfoBarSeverity.Warning => (FluentIconCatalog.StatusWarn, "yellow"),
            InfoBarSeverity.Success => (FluentIconCatalog.StatusOk, "green"),
            _ => (FluentIconCatalog.StatusOk, "blue"),
        };
        var message = NoticeMessage(notice);
        var toast = Border(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto], [GridSize.Auto],
                    SessionsHubView.Glyph(glyph, 18, SessionsHubView.Strong(color))
                        .AccessibilityHidden().VAlign(VerticalAlignment.Center).Grid(column: 0),
                    TextBlock(message).TextWrapping(TextWrapping.Wrap)
                        .VAlign(VerticalAlignment.Center).Margin(10, 0).Grid(column: 1),
                    notice.Action is { } action && notice.ActionLabel is { Length: > 0 } label
                        ? Button(label, () =>
                            {
                                action();
                                context.Controller.DismissNotice(notice);
                            }).SubtleButton().AutomationName(label).AutomationId("SessionsHubNoticeAction").Grid(column: 2)
                        : Empty(),
                    SessionsHubView.IconButton("\uE711", SessionsHubPresentation.T("DismissNotice"),
                            () => context.Controller.DismissNotice(notice), automationId: "SessionsHubNoticeDismiss")
                        .Grid(column: 3)))
                .Background(Theme.Ref("FlyoutPresenterBackground"))
                .WithBorder(Theme.Ref("FlyoutBorderThemeBrush"), 1)
                .CornerRadius(8)
                .Padding(14, 10)
                .MaxWidth(480)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Bottom)
                .Margin(20)
                .Set(border =>
                {
                    border.Shadow = new ThemeShadow();
                    border.RequestedTheme = context.Controller.ActualTheme;
                })
                .Translation(0, 0, 16)
                .LiveRegion(notice.Severity == InfoBarSeverity.Error
                    ? AutomationLiveSetting.Assertive
                    : AutomationLiveSetting.Polite)
                .AutomationId("SessionsHubNotice")
                .WithKey("notice");
        return toast;
    }

    private static string NoticeMessage(SessionsHubNotice notice) =>
        string.IsNullOrWhiteSpace(notice.Title)
            ? notice.Message
            : $"{notice.Title}: {notice.Message}";
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
    public PageWidthTier Tier => PageResponsiveLayout.TierFor(Width);
    public double ContentWidth => PageResponsiveLayout.ContentWidth(Width);
    public bool IsWide => Tier == PageWidthTier.Wide;
    public bool IsMedium => Tier == PageWidthTier.Medium;
    public bool IsNarrow => Tier == PageWidthTier.Narrow;
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

    public static Element WithMutationHelp(Element control, SessionsHubState state, string actionTooltip)
    {
        var help = state.CanMutateRows ? actionTooltip : SessionsHubPresentation.T("RosterNotCurrent");
        return control.ToolTip(help).HelpText(help);
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
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(8);

    public static Element Stage(Element child, string? automationId = null) =>
        Border(child)
            .Padding(16)
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(12)
            .AutomationId(automationId ?? "");

    public static Element SectionTitle(string title, int? count = null, string? automationId = null, string countColor = "blue") =>
        HStack(8,
                TextBlock(title).FontSize(20).FontWeight(FontWeights.SemiBold)
                    .VAlign(VerticalAlignment.Center)
                    .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2),
                count is { } value ? Pill(value.ToString(System.Globalization.CultureInfo.CurrentCulture), countColor) : Empty())
            .VAlign(VerticalAlignment.Center)
            .AutomationId(automationId ?? "");

    /// <summary>SelectorBar items are data records, so stable automation IDs are applied to the native items.</summary>
    public static void TagItems(SelectorBar bar, string prefix, IReadOnlyList<string> ids)
    {
        for (var i = 0; i < bar.Items.Count && i < ids.Count; i++)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(bar.Items[i], prefix + ids[i]);
    }

    public static MenuFlyoutItemData Item(string text, string? glyph, Action onClick, bool enabled = true) =>
        MenuItem(text, onClick) with { IconElement = glyph is null ? null : FontIcon(glyph), IsEnabled = enabled };
}
