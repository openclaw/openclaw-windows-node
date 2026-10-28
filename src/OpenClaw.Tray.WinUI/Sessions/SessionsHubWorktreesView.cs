using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

/// <summary>
/// Managed worktrees: repository, branch, owner, activity and status, with
/// admin-only create, remove (including forced removal), restore, and cleanup.
/// Admin-only commands carry a shield glyph and a tooltip naming the requirement.
/// </summary>
internal static class SessionsHubWorktreesView
{
    private const string ShieldGlyph = "\uEA18";
    private const string BranchGlyph = "\uE943"; // Code: a worktree is a code workspace
    private static string T(string key) => SessionsHubPresentation.T(key);
    private static string F(string key, params object?[] args) => SessionsHubPresentation.F(key, args);

    public static Element Render(SessionsHubViewContext context)
    {
        var state = context.State;
        var controller = context.Controller;
        var worktrees = state.Worktrees;
        var admin = state.Permissions.CanManageWorktrees;
        var idle = worktrees.BusyId is null && !worktrees.IsCleaning;
        var adminTip = admin ? null : T("AdminRequiredTip");

        var toolbar = HStack(8,
            AdminButton(T("NewWorktree"), FluentIconCatalog.Add, () => _ = controller.CreateWorktreeAsync(),
                state.IsConnected && admin && idle, "SessionsHubNewWorktree", adminTip, accent: true),
            AdminButton(worktrees.IsCleaning ? T("Cleaning") : T("CleanNow"), FluentIconCatalog.Clear, () => _ = controller.CleanWorktreesAsync(),
                state.IsConnected && admin && idle && worktrees.Load == SessionsHubLoad.Loaded, "SessionsHubCleanWorktrees",
                adminTip ?? T("CleanNowTip")),
            When(worktrees.IsCleaning, () => ProgressRing().IsActive(true).Size(16, 16).VAlign(VerticalAlignment.Center)
                .AutomationName(T("Cleaning")))).Margin(0, 0, 0, 8);

        var adminBar = state.IsConnected && !admin && worktrees.Load != SessionsHubLoad.Unsupported
            ? (InfoBar(T("AdminRequiredTitle"), T("AdminRequiredBody")) with { IsOpen = true })
                .Severity(InfoBarSeverity.Informational).IsClosable(false)
                .IconSource(FontIcon(ShieldGlyph))
                .Margin(0, 0, 0, 8)
                .AutomationId("SessionsHubWorktreesAdminRequired")
            : Empty();
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            adminBar.Grid(row: 0),
            toolbar.Grid(row: 1),
            Body(context).Grid(row: 2));
    }

    private static Element AdminButton(string label, string glyph, Action onClick, bool enabled, string automationId,
        string? tooltip, bool accent = false)
    {
        var button = Button(HStack(8,
                SessionsHubView.Glyph(glyph, 14).VAlign(VerticalAlignment.Center),
                TextBlock(label).VAlign(VerticalAlignment.Center),
                SessionsHubView.Glyph(ShieldGlyph, 12, Theme.SecondaryText).VAlign(VerticalAlignment.Center)),
            onClick).AutomationName(label);
        if (accent) button = button.AccentButton();
        return button.IsEnabled(enabled)
            .HelpText(T("AdminOnly"))
            .ToolTip(tooltip ?? $"{label} ({T("AdminOnly")})")
            .AutomationId(automationId);
    }

    private static Element Body(SessionsHubViewContext context)
    {
        var state = context.State;
        var controller = context.Controller;
        var worktrees = state.Worktrees;
        if (!state.IsConnected)
            return SessionsHubView.EmptyState(FluentIconCatalog.Folder, T("DisconnectedTitle"), T("WorktreesDisconnected"),
                automationId: "SessionsHubWorktreesDisconnected");
        switch (worktrees.Load)
        {
            case SessionsHubLoad.Unsupported:
                return SessionsHubView.EmptyState(FluentIconCatalog.StatusWarn, T("WorktreesUnsupportedTitle"), T("WorktreesUnsupported"),
                    automationId: "SessionsHubWorktreesUnsupported");
            case SessionsHubLoad.Failed when worktrees.Rows is not { Count: > 0 }:
                return SessionsHubView.EmptyState(FluentIconCatalog.StatusErr, T("WorktreesFailed"), worktrees.Error ?? "",
                    Button(T("Retry"), () => _ = controller.LoadWorktreesAsync()).AutomationName(T("Retry")).AutomationId("SessionsHubWorktreesRetry"),
                    "SessionsHubWorktreesFailed");
            case SessionsHubLoad.Idle or SessionsHubLoad.Loading when worktrees.Rows is null:
                return HStack(12, ProgressRing().IsActive(true).Size(20, 20), TextBlock(T("WorktreesLoading")).VAlign(VerticalAlignment.Center))
                    .HAlign(HorizontalAlignment.Center).Margin(0, 48).AutomationId("SessionsHubWorktreesLoading");
        }
        var rows = worktrees.Rows ?? Array.Empty<WorktreeRecord>();
        if (rows.Count == 0)
            return SessionsHubView.EmptyState(BranchGlyph, T("WorktreesEmptyTitle"), T("WorktreesEmptyBody"),
                automationId: "SessionsHubWorktreesEmpty", colorId: "green");

        var titles = SessionsHubPresentation.Titles(state.Rows);
        string? SessionTitle(string key) => titles.TryGetValue(key, out var title) ? title : null;
        var header = Border(Grid(Columns, [GridSize.Auto],
                Header(T("WorktreeColName"), 0), Header(T("WorktreeColRepo"), 1), Header(T("WorktreeColOwner"), 2),
                Header(T("WorktreeColActive"), 3), Header(T("WorktreeColStatus"), 4)))
            .Padding(12, 0, 12, 4);
        var list = rows.Select(record => Row(context, record, SessionTitle).WithKey(record.Id)).ToArray();
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
            header.Grid(row: 0),
            ScrollView(VStack(6, list)
                    .WithTransitions(new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { IsStaggeringEnabled = true },
                        new Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition())
                    .Padding(0, 0, 12, 8))
                .AutomationId("SessionsHubWorktreeList").Grid(row: 1));
    }

    private static readonly GridSize[] Columns =
        [GridSize.Star(2), GridSize.Star(1.4), GridSize.Star(1.4), GridSize.Px(110), GridSize.Px(170), GridSize.Px(140)];

    private static Element Header(string text, int column) =>
        TextBlock(text).FontSize(12).FontWeight(FontWeights.SemiBold).Foreground(Theme.SecondaryText).Grid(column: column);

    private static Element Row(SessionsHubViewContext context, WorktreeRecord record, Func<string, string?> sessionTitle)
    {
        var state = context.State;
        var controller = context.Controller;
        var admin = state.Permissions.CanManageWorktrees;
        var busy = state.Worktrees.BusyId == record.Id;
        var idle = state.Worktrees.BusyId is null && !state.Worktrees.IsCleaning;
        var owner = SessionsHubPresentation.WorktreeOwner(record, sessionTitle);
        var status = record.IsRemoved ? T("WorktreeRestorable") : T("WorktreeActive");
        var lastActive = SessionsHubPresentation.RelativeTime(record.LastActiveAt, context.NowUtc);

        var repoName = WorktreesProjection.RepoName(record.RepoRoot);
        var repoColor = SessionAppearance.PaletteColor(repoName);
        var (ownerGlyph, ownerColor) = record.OwnerKind switch
        {
            "session" => (FluentIconCatalog.Chat, "blue"),
            "workboard" => (FluentIconCatalog.DashboardView, "purple"),
            _ => (FluentIconCatalog.Operator, "cyan"),
        };
        Element ownerText = record.OwnerKind == "session" && record.OwnerId is { } ownerKey
            ? HyperlinkButton(owner, onClick: () => controller.OpenInChat(ownerKey))
                .AutomationName(F("WorktreeOpenOwner", owner))
                .ToolTip(ownerKey)
                .Padding(0)
                .AutomationId("SessionsHubWorktreeOwner:" + record.Id)
            : TextBlock(owner).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center);
        var ownerCell = HStack(8, SessionsHubView.IconTile(ownerGlyph, ownerColor, 24), ownerText.VAlign(VerticalAlignment.Center));

        Element action = record.IsRemoved
            ? AdminButton(T("Restore"), FluentIconCatalog.Unarchive, () => _ = controller.RestoreWorktreeAsync(record),
                admin && idle, "SessionsHubWorktreeRestore:" + record.Id, admin ? null : T("AdminRequiredTip"))
            : AdminButton(T("Remove"), FluentIconCatalog.Delete, () => _ = controller.RemoveWorktreeAsync(record),
                admin && idle, "SessionsHubWorktreeRemove:" + record.Id, admin ? null : T("AdminRequiredTip"));

        var cells = new Element[]
        {
            Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    SessionsHubView.IconTile(BranchGlyph, record.IsRemoved ? "yellow" : "green", 36).Grid(column: 0),
                    VStack(2,
                            TextBlock(record.Name).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis),
                            TextBlock(record.Branch).FontSize(12).FontFamily("Cascadia Mono, Consolas")
                                .Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis)
                                .ToolTip(record.BaseRef is null ? record.Branch : F("WorktreeFromBase", record.Branch, record.BaseRef)))
                        .VAlign(VerticalAlignment.Center).Margin(12, 0, 8, 0).Grid(column: 1))
                .Grid(column: 0),
            HStack(8,
                    SessionsHubView.ColorDot(repoColor, 10),
                    TextBlock(repoName).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center))
                .VAlign(VerticalAlignment.Center).ToolTip(record.Path is null ? record.RepoRoot : $"{record.RepoRoot}\n{record.Path}")
                .Grid(column: 1),
            ownerCell.VAlign(VerticalAlignment.Center).Grid(column: 2),
            TextBlock(lastActive).FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(column: 3),
            (record.IsRemoved
                ? SessionsHubView.Pill(status, "yellow", FluentIconCatalog.Archive)
                : SessionsHubView.Pill(status, "green", FluentIconCatalog.StatusOk))
                .HAlign(HorizontalAlignment.Left).Grid(column: 4),
            (busy ? ProgressRing().IsActive(true).Size(16, 16).HAlign(HorizontalAlignment.Right) : action.HAlign(HorizontalAlignment.Right))
                .Grid(column: 5),
        };
        return SessionsHubView.Card(Grid(Columns, [GridSize.Auto], cells), 12, 8)
            .AutomationName(F("WorktreeRowName", record.Name, record.Branch, owner, status))
            .AutomationId("SessionsHubWorktree:" + record.Id);
    }
}
