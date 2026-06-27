using System.IO;

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
        // The confirmation copy comes from the shared planner, not inline strings.
        Assert.DoesNotContain("Start a fresh session for '", source);
        Assert.DoesNotContain("Keep the latest log lines for '", source);
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
