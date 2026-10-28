using System.IO;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-contract tests for the session-actions + compaction-checkpoint UX.
/// They assert the Sessions page and App route destructive actions through the
/// shared <c>SessionActionPlanner</c> (confirmation + main-session protection)
/// and expose export/checkpoint entry points, without needing a UI thread.
/// </summary>
public sealed class SessionActionsWiringTests
{
    [Fact]
    public void ArchiveBrowsingBelongsToSettingsNotWorkspace()
    {
        var workspaceXaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml");
        var workspace = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs");
        var view = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubSessionsView.cs");
        var controller = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubController.cs");
        Assert.DoesNotContain("WorkspaceArchivedToggle", workspaceXaml);
        Assert.DoesNotContain("ArchivedHeader", workspaceXaml);
        Assert.DoesNotContain("WorkspaceArchivedSession", workspaceXaml);
        Assert.DoesNotContain("_archived", workspace);
        // Settings Sessions owns archived browsing through its server-filtered Archived view.
        Assert.Contains("\"SessionsPageStatus\", [\"Active\", \"Archived\", \"All\"]", view);
        Assert.Contains("\"SessionsPageUnarchive:\" + row.Key", view);
        // Unarchive waits for Gateway acceptance on the captured connection.
        Assert.Contains("new WorkspaceSessionOperation(client", controller);
        Assert.Contains("operation.PatchAsync(row.Key, new SessionPatch { Archived = false })", controller);
    }

    [Fact]
    public void SessionsPage_ConfirmsDestructiveActions_ViaPlanner()
    {
        var controller = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubController.cs");
        var dialog = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubDialogView.cs");

        // Single delete uses the shared planner gate and its localized prompt.
        Assert.Contains("SessionActionPlanner.IsAllowed(SessionActionKind.Delete", controller);
        Assert.Contains("SessionActionPlanner.BuildPrompt(SessionActionKind.Delete", controller);
        Assert.Contains("SessionActionPromptLocalizer.Localize(prompt)", controller);
        // Reset and Compact run through the Workspace menu runner, which owns their confirmation.
        Assert.Contains("_menu.ExecuteAsync(action, ToWorkspaceSession(row, title), row)", controller);
        // Bulk, delete-all-archived, cloud-worker stop, and forced worktree removal confirm first.
        Assert.Contains("ConfirmAsync(T(\"DeleteSelectedTitle\")", controller);
        Assert.Contains("ConfirmAsync(T(\"DeleteAllArchivedTitle\")", controller);
        Assert.Contains("ConfirmAsync(T(\"StopWorkerTitle\")", controller);
        Assert.Contains("ConfirmAsync(T(\"ForceRemoveTitle\")", controller);
        // Runtime dialogs use runtime keys, and destructive dialogs default to Cancel.
        Assert.Contains("SessionActionPrompt_CancelLabel", dialog);
        Assert.Contains("destructive ? ContentDialogButton.Close : ContentDialogButton.Primary", dialog);
    }

    [Fact]
    public void SessionsPage_ExposesExportAndCheckpointActions()
    {
        var menus = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubMenus.cs");
        var controller = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubController.cs");
        var shared = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceSessionMenuController.cs");
        var checkpoints = ReadSource(
            "src",
            "OpenClaw.Tray.WinUI",
            "Dialogs",
            "SessionCheckpointDialogCoordinator.cs");

        // The page menu is built from the Workspace sidebar's shared entries (export, copy, fork...).
        Assert.Contains("WorkspaceSessionMenu.Build(menuState)", menus);
        Assert.Contains("RequestChatHistoryAsync", shared);
        Assert.Contains("SessionTranscriptFormatter", shared);
        Assert.Contains("FileSavePicker", shared);

        // Checkpoints use the gateway's compaction-checkpoint protocol APIs.
        Assert.Contains("SessionCheckpointDialogCoordinator.ShowAsync", controller);
        Assert.Contains("ListCompactionCheckpointsAsync", checkpoints);
        Assert.Contains("BranchCompactionCheckpointAsync", checkpoints);
        Assert.Contains("RestoreCompactionCheckpointAsync", checkpoints);
        Assert.Contains("ReplaceHistoryAfterCheckpointRestoreAsync", checkpoints);
        // Unsupported gateways are surfaced via the typed IsSupported flag.
        Assert.Contains("IsSupported", checkpoints);
    }

    [Fact]
    public void ChatCompactionAction_OpensTheCurrentSessionsCheckpoints()
    {
        var root = ReadSource("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");
        var host = ReadSource("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatHostExtensions.cs");
        var timeline = ReadSource("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");
        var chatPage = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ChatPage.xaml.cs");
        var sessions = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubController.cs");
        var checkpoints = ReadSource(
            "src",
            "OpenClaw.Tray.WinUI",
            "Dialogs",
            "SessionCheckpointDialogCoordinator.cs");

        Assert.Contains("Action<string>? OnOpenCheckpoints", root);
        Assert.Contains("Action<string>? onOpenCheckpoints", host);
        Assert.Contains("row.Props.OnOpenCheckpoints!(sessionKey!)", timeline);
        Assert.Contains("onOpenCheckpoints: OpenSessionCheckpoints", chatPage);
        Assert.Contains("SessionCheckpointDialogCoordinator.ShowAsync", chatPage);
        Assert.Contains("SessionCheckpointDialogCoordinator.ShowAsync", sessions);
        Assert.Contains("XamlRoot = _xamlRoot", checkpoints);
        Assert.DoesNotContain("ShowHub(\"sessions\")", chatPage);
    }

    [Fact]
    public void SessionsPage_HardensDestructiveActions()
    {
        var controller = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubController.cs");
        var checkpoints = ReadSource(
            "src",
            "OpenClaw.Tray.WinUI",
            "Dialogs",
            "SessionCheckpointDialogCoordinator.cs");

        // Main-session gating uses an authoritative resolver, not a VM-only default.
        Assert.Contains("ResolveMainState", checkpoints);
        Assert.Contains("SessionActionPlanner.ResolveMainState(", controller);
        // Restore only acts on a provably-latest checkpoint and re-validates fresh.
        Assert.Contains("ResolveUnambiguousLatest", checkpoints);
        // ID-less checkpoints are preserved for restore safety, but can't be used as branch targets.
        Assert.Contains(
            "FirstOrDefault(checkpoint => !string.IsNullOrWhiteSpace(checkpoint.Id))",
            checkpoints);
        // Deletes are compare-and-delete per row and Gateway rejections are surfaced, not swallowed.
        Assert.Contains("SessionsHubDeletion.Plan(", controller);
        Assert.Contains("ShowNotice(failureTitle, ex.Message, InfoBarSeverity.Error)", controller);
    }

    [Fact]
    public void SessionsPage_ConnectionChange_CancelsPromptsAndDropsOldResults()
    {
        var controller = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubController.cs");
        var epoch = controller[controller.IndexOf("private void StartConnectionEpoch", StringComparison.Ordinal)..];
        epoch = epoch[..epoch.IndexOf("private void Unsubscribe", StringComparison.Ordinal)];

        // A confirm dialog open across a reconnect resolves as cancel, Undo dies with the old notice,
        // and old rows and results are dropped.
        Assert.Contains("CloseDialog(false);", epoch);
        Assert.Contains("Notice = null", epoch);
        Assert.Contains("Rows = Array.Empty<SessionInfo>()", epoch);
        Assert.Contains("_worktreeGeneration++;", epoch);
        Assert.Contains("_transcriptGeneration++;", epoch);
        // A same-client reconnect also ends the binding, and actions keep the binding captured before their prompt.
        Assert.Contains("if (_binding is { IsCurrent: true } ? false : _binding is not null || connected)", controller);
        Assert.Contains("() => _ = UndoArchiveAsync(row, binding)", controller);
        Assert.Contains("await ExecutePlanAsync(plan, T(\"DeleteFailed\"), binding);", controller);
        // Prompting actions need a live connection before the prompt opens, never a later fallback.
        Assert.Contains("if (PromptBinding() is not { } binding) return;", controller);
        // Identity-less rows are never deleted on a confirmation made before a reload.
        Assert.Contains("reloaded && plan.Requests.Any(request => request.ExpectedSessionId is null)", controller);
    }

    [Fact]
    public void SessionsPage_Menu_GatesDeleteByMainSessionAndScope()
    {
        var menus = ReadSource("src", "OpenClaw.Tray.WinUI", "Sessions", "SessionsHubMenus.cs");

        // Main-session protection comes from the shared entries; scopes narrow them further.
        Assert.Contains("controller.MainState(row)", menus);
        Assert.Contains("WorkspaceSessionMenuAction.Delete => permissions.CanDelete(row)", menus);
        Assert.Contains("WorkspaceSessionMenuAction.Reset or WorkspaceSessionMenuAction.Compact => permissions.IsAdmin", menus);
    }

    [Fact]
    public void App_SessionActions_UsePlanner_NotInlineCopy()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("SessionActionPlanner.BuildPrompt", source);
        Assert.Contains("SessionActionPlanner.IsAllowed", source);
        Assert.Contains("SessionActionPrompt_CancelLabel", source);
        Assert.DoesNotContain("LocalizationHelper.GetString(\"CancelButton.Content\")", source);
        Assert.Contains("DefaultButton = ContentDialogButton.None", source);
        // The confirmation copy comes from the shared planner, not inline strings.
        Assert.DoesNotContain("Start a fresh session for '", source);
        Assert.DoesNotContain("Keep the latest log lines for '", source);
    }

    [Fact]
    public void SessionActionPrompts_AreRuntimeLocalized()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Helpers", "SessionActionPromptLocalizer.cs");
        var requiredKeys = new[]
        {
            "SessionActionPrompt_CancelLabel",
            "SessionActionPrompt_Reset_Title",
            "SessionActionPrompt_Reset_BodyFormat",
            "SessionActionPrompt_Reset_ConfirmLabel",
            "SessionActionPrompt_Compact_Title",
            "SessionActionPrompt_Compact_BodyFormat",
            "SessionActionPrompt_Compact_ConfirmLabel",
            "SessionActionPrompt_Delete_Title",
            "SessionActionPrompt_Delete_BodyFormat",
            "SessionActionPrompt_Delete_ConfirmLabel",
            "SessionActionPrompt_Restore_Title",
            "SessionActionPrompt_Restore_BodyFormat",
            "SessionActionPrompt_Restore_ConfirmLabel",
        };

        foreach (var prefix in new[]
        {
            "SessionActionPrompt_Reset",
            "SessionActionPrompt_Compact",
            "SessionActionPrompt_Delete",
            "SessionActionPrompt_Restore",
        })
        {
            Assert.Contains(prefix, source);
        }

        var stringsRoot = Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings");
        foreach (var resourcePath in Directory.EnumerateFiles(stringsRoot, "Resources.resw", SearchOption.AllDirectories))
        {
            var keys = XDocument.Load(resourcePath)
                .Descendants("data")
                .Select(e => e.Attribute("name")?.Value)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in requiredKeys)
                Assert.Contains(key, keys);
        }
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }

    private static string GetRepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
