using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using Windows.ApplicationModel.DataTransfer;
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
                .AutomationName(T("Cleaning"))));

        var adminBar = state.IsConnected && !admin && worktrees.Load != SessionsHubLoad.Unsupported
            ? (InfoBar(T("AdminRequiredTitle"), T("AdminRequiredBody")) with { IsOpen = true })
                .Severity(InfoBarSeverity.Informational).IsClosable(false)
                .IconSource(FontIcon(ShieldGlyph))
                .Margin(0, 0, 0, 8)
                .AutomationId("SessionsHubWorktreesAdminRequired")
            : Empty();
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            adminBar.WithKey("admin").Grid(row: 0),
            toolbar.WithKey("toolbar").Margin(0, 0, 0, 10).AutomationId("SessionsHubWorktreeCommands").Grid(row: 1),
            Body(context).WithKey("body").Grid(row: 2));
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
        var active = rows.Where(record => !record.IsRemoved).ToArray();
        var removed = rows.Where(record => record.IsRemoved).ToArray();
        var sections = new List<Element>();
        if (active.Length > 0)
            sections.Add(WorktreeGroup(context, T("WorktreesActiveGroup"), active, SessionTitle));
        if (removed.Length > 0)
            sections.Add(WorktreeGroup(context, T("WorktreesRemovedGroup"), removed, SessionTitle));
        return VStack(18, sections.ToArray())
                .WithTransitions(new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { IsStaggeringEnabled = true },
                    new Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition())
                .Padding(0, 0, 12, 8)
            .AutomationId("SessionsHubWorktreeList");
    }

    private static Element WorktreeGroup(
        SessionsHubViewContext context,
        string title,
        IReadOnlyList<WorktreeRecord> rows,
        Func<string, string?> sessionTitle) =>
        VStack(10,
            SessionsHubView.SectionTitle(title, rows.Count),
            VStack(PageResponsiveLayout.GapInner,
                rows.Select(record => Row(context, record, sessionTitle).WithKey(record.Id)).ToArray()));

    private static Element Row(SessionsHubViewContext context, WorktreeRecord record, Func<string, string?> sessionTitle)
    {
        var state = context.State;
        var controller = context.Controller;
        var admin = state.Permissions.CanManageWorktrees;
        var busy = state.Worktrees.BusyId == record.Id;
        var idle = state.Worktrees.BusyId is null && !state.Worktrees.IsCleaning;
        var owner = SessionsHubPresentation.WorktreeOwner(record, sessionTitle);
        var lastActive = SessionsHubPresentation.RelativeTime(record.LastActiveAt, context.NowUtc);
        var repoName = string.IsNullOrWhiteSpace(record.RepoRoot)
            ? T("NotAvailable")
            : WorktreesProjection.RepoName(record.RepoRoot);
        var status = record.IsRemoved ? T("WorktreeRestorable") : T("WorktreeActive");
        var headerActions = new List<Element>
        {
            TextBlock(lastActive).FontSize(12).Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center).Margin(8, 0),
        };
        if (record.OwnerKind == "session" && record.OwnerId is { } ownerKey)
        {
            headerActions.Add(SessionsHubView.IconButton(FluentIconCatalog.Chat, F("WorktreeOpenOwner", owner),
                () => controller.OpenInChat(ownerKey), automationId: "SessionsHubWorktreeOwner:" + record.Id));
        }
        var header = Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            SessionsHubView.IconTile(BranchGlyph, record.IsRemoved ? "yellow" : "green", 36).Grid(column: 0),
            VStack(3,
                    HStack(8,
                        TextBlock(record.Name).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis),
                        TextBlock(record.Branch).FontSize(12).FontFamily("Cascadia Mono, Consolas")
                            .Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis)),
                    TextBlock($"{owner} · {repoName}").FontSize(12).Foreground(Theme.SecondaryText)
                        .TextTrimming(TextTrimming.CharacterEllipsis))
                .VAlign(VerticalAlignment.Center).Margin(12, 0, 8, 0).Grid(column: 1),
            HStack(2, headerActions.ToArray()).VAlign(VerticalAlignment.Center).Grid(column: 2));
        var details = WorktreeDetails(context, record, owner, admin, idle, busy);
        var expanded = state.Worktrees.DetailsId == record.Id;
        return (Expander("", details, expanded, value =>
            {
                if (value)
                    controller.SetWorktreeDetails(record.Id);
                else if (controller.State.Worktrees.DetailsId == record.Id)
                    controller.SetWorktreeDetails(null);
            }) with { HeaderTemplate = header })
            .Background(Theme.Ref("CardBackgroundFillColorSecondaryBrush"))
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(8)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationName(F("WorktreeRowName", record.Name, record.Branch, owner, status))
            .AutomationId("SessionsHubWorktree:" + record.Id);
    }

    private static Element WorktreeDetails(
        SessionsHubViewContext context, WorktreeRecord record, string owner, bool admin, bool idle, bool busy)
    {
        var controller = context.Controller;
        var copyPath = NonBlank(record.Path) ?? NonBlank(record.RepoRoot);
        var action = record.IsRemoved
            ? AdminButton(T("Restore"), FluentIconCatalog.Unarchive, () => _ = controller.RestoreWorktreeAsync(record),
                admin && idle, "SessionsHubWorktreeRestore:" + record.Id, admin ? null : T("AdminRequiredTip"))
            : AdminButton(T("Remove"), FluentIconCatalog.Delete, () => _ = controller.RemoveWorktreeAsync(record),
                admin && idle, "SessionsHubWorktreeRemove:" + record.Id, admin ? null : T("AdminRequiredTip"));
        var command = Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            SessionsHubView.CommandButton(FluentIconCatalog.Copy, LocalizationHelper.GetString("WorkspacePage_CopyPath"),
                () => CopyPath(copyPath!),
                enabled: copyPath is not null,
                automationId: "SessionsHubWorktreeCopy:" + record.Id).Grid(column: 0),
            Border(Empty()).Grid(column: 1),
            (busy ? ProgressRing().IsActive(true).Size(16, 16) : action).Grid(column: 2));
        var facts = new (string Label, string Value)[]
        {
            (T("FactPath"), NonBlank(record.Path) ?? NonBlank(record.RepoRoot) ?? T("NotAvailable")),
            (T("WorktreeColRepo"), NonBlank(record.RepoRoot) ?? T("NotAvailable")),
            (T("WorktreeBranch"), NonBlank(record.Branch) is { } branch
                ? NonBlank(record.BaseRef) is { } baseRef ? F("WorktreeBranchFrom", branch, baseRef) : branch
                : T("NotAvailable")),
            (T("WorktreeColOwner"), owner),
            (T("FactCreated"), SessionsHubPresentation.RelativeTime(record.CreatedAt, context.NowUtc)),
            (T("WorktreeColActive"), SessionsHubPresentation.RelativeTime(record.LastActiveAt, context.NowUtc)),
        };
        var cells = new List<Element>();
        for (var i = 0; i < facts.Length; i++)
        {
            cells.Add(TextBlock(facts[i].Label).FontSize(12).Foreground(Theme.SecondaryText)
                .Margin(0, 0, 12, 6).Grid(row: i, column: 0));
            cells.Add(TextBlock(facts[i].Value).FontSize(12).TextWrapping(TextWrapping.Wrap)
                .IsTextSelectionEnabled().Margin(0, 0, 0, 6).Grid(row: i, column: 1));
        }
        return VStack(10,
                command,
                Border(Empty()).Height(1).Background(Theme.Ref("DividerStrokeColorDefaultBrush")),
                !admin
                    ? TextBlock(T("AdminRequiredBody")).FontSize(12).Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.Wrap)
                    : Empty(),
                Grid([GridSize.Auto, GridSize.Star()], facts.Select(_ => GridSize.Auto).ToArray(), cells.ToArray()))
            .Padding(4, 4, 4, 8);
    }

    private static void CopyPath(string path)
    {
        var package = new DataPackage();
        package.SetText(path);
        Clipboard.SetContent(package);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
