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
    public void SessionsPage_ConfirmsDestructiveActions_ViaPlanner()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "SessionsPage.xaml.cs");

        // Destructive actions are gated by the shared planner + a confirm dialog.
        Assert.Contains("SessionActionPlanner.IsAllowed", source);
        Assert.Contains("SessionActionPlanner.BuildPrompt", source);
        Assert.Contains("ConfirmAsync", source);
        // Reset/Compact/Delete funnel through a single confirmed runner.
        Assert.Contains("RunSessionActionAsync(sender, SessionActionKind.Reset)", source);
        Assert.Contains("RunSessionActionAsync(sender, SessionActionKind.Compact)", source);
        Assert.Contains("RunSessionActionAsync(sender, SessionActionKind.Delete)", source);
        // Runtime dialogs must not use XAML property resource keys; those can
        // fall back to raw key text such as "CancelButton.Content".
        Assert.Contains("SessionActionPrompt_CancelLabel", source);
        Assert.DoesNotContain("LocalizationHelper.GetString(\"CancelButton.Content\")", source);
        Assert.Contains("DefaultButton = ContentDialogButton.None", source);
    }

    [Fact]
    public void SessionsPage_ExposesExportAndCheckpointActions()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "SessionsPage.xaml.cs");

        // Export uses transcript fetch + formatter + file picker.
        Assert.Contains("RequestChatHistoryAsync", source);
        Assert.Contains("SessionTranscriptFormatter", source);
        Assert.Contains("FileSavePicker", source);

        // Checkpoints use the gateway's compaction-checkpoint protocol APIs.
        Assert.Contains("ListCompactionCheckpointsAsync", source);
        Assert.Contains("BranchCompactionCheckpointAsync", source);
        Assert.Contains("RestoreCompactionCheckpointAsync", source);
        // Unsupported gateways are surfaced via the typed IsSupported flag.
        Assert.Contains("IsSupported", source);
    }

    [Fact]
    public void SessionsPage_HardensDestructiveActions()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "SessionsPage.xaml.cs");

        // Main-session gating uses an authoritative resolver, not a VM-only default.
        Assert.Contains("ResolveMainState", source);
        // Restore only acts on a provably-latest checkpoint and re-validates fresh.
        Assert.Contains("ResolveUnambiguousLatest", source);
        // ID-less checkpoints are preserved for restore safety, but can't be used as branch targets.
        Assert.Contains("FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Id))", source);
        // Destructive send failures are surfaced, not swallowed.
        Assert.Contains("\"The gateway didn't accept the request. Try again.\"", source);
    }

    [Fact]
    public void SessionsPage_Xaml_HasActionMenuItems_AndGatesDelete()
    {
        var xaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "SessionsPage.xaml");

        Assert.Contains("Click=\"OnExportSession\"", xaml);
        Assert.Contains("Click=\"OnShowCheckpoints\"", xaml);
        Assert.Contains("Click=\"OnDeleteSession\"", xaml);
        // Delete is disabled for sessions that can't be deleted (main session).
        Assert.Contains("IsEnabled=\"{Binding CanDelete}\"", xaml);
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
