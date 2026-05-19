using System;
using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the contract of <c>scripts/cleanup-ghost-windows.ps1</c>, the
/// standalone manual-recovery tool for Windows Terminal ghost frames.
///
/// We can't actually invoke the script from a unit test (it manipulates
/// real OS windows and requires PowerShell). So we pin its surface as
/// source-text assertions: filter criteria match the production
/// <see cref="WinAppSdkGhostWindowCleanup"/> exactly (class name, title,
/// owner process, size) so the safety-net script can never close a window
/// the in-process cleanup wouldn't have closed, and the close-message
/// sequence matches the one we proved works against real ghost frames
/// during MSIX-E2E manual testing on Mike's dev box (2026-05-19).
///
/// Drift here is dangerous: a too-loose filter could close the user's
/// real Terminal windows; a missing close-message could leave ghosts
/// permanently visible.
/// </summary>
public sealed class GhostWindowCleanupScriptContractTests
{
    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string LoadScript() =>
        File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "scripts", "cleanup-ghost-windows.ps1"));

    [Theory]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "Window class filter")]
    [InlineData("WindowsTerminal",               "Owner process filter")]
    [InlineData("1000",                          "Minimum width")]
    [InlineData("500",                           "Minimum height")]
    public void FilterMatchesProductionCleanup(string token, string reason)
    {
        // Each criterion MUST also be in the C# in-process cleanup. If they
        // diverge, the safety-net script could close windows the production
        // path wouldn't (false positive risk against real user Terminals).
        var script = LoadScript();
        var prod = File.ReadAllText(Path.Combine(GetRepositoryRoot(),
            "tests", "OpenClaw.Tray.Tests", "WinAppSdkGhostWindowCleanup.cs"));

        Assert.True(script.Contains(token), $"Script missing filter token '{token}' ({reason})");
        Assert.True(prod.Contains(token),   $"In-process cleanup missing filter token '{token}' ({reason})");
    }

    [Fact]
    public void TitleFilter_IsExactlyTerminal_InBothImplementations()
    {
        // Special-cased from the FilterMatchesProductionCleanup theory because
        // PowerShell uses single quotes for string literals ('Terminal') and
        // C# uses double quotes ("Terminal"); the token "Terminal" itself is
        // too common (it appears in comments, doc, the class name, etc.) for
        // a bare-substring match to be meaningful. We assert each
        // implementation uses the language-native exact-equals comparison
        // against the literal string Terminal.
        var script = LoadScript();
        var prod = File.ReadAllText(Path.Combine(GetRepositoryRoot(),
            "tests", "OpenClaw.Tray.Tests", "WinAppSdkGhostWindowCleanup.cs"));

        // PowerShell: -ne 'Terminal' (return early if NOT a ghost-titled window)
        Assert.Matches(@"-ne\s+'Terminal'", script);
        // C#: title.ToString(), "Terminal", StringComparison.Ordinal
        Assert.Matches(@"title\.ToString\(\)\s*,\s*""Terminal""\s*,\s*StringComparison\.Ordinal", prod);
    }

    [Fact]
    public void CloseSequence_MatchesProvenMessageOrder()
    {
        // The proven sequence from MSIX-E2E manual testing:
        //   1. ShowWindow(SW_HIDE)        - hides immediately so no strobe
        //   2. PostMessage(WM_SYSCOMMAND, SC_CLOSE) - queued, non-blocking
        //   3. SendMessageTimeout(WM_CLOSE, SMTO_ABORTIFHUNG, 1000ms)
        //
        // A plain SendMessage(WM_SYSCOMMAND, SC_CLOSE) alone does NOT work —
        // WindowsTerminal swallows it on orphan frames. We saw this in the
        // session 2026-05-19 (first attempted close: 0/9 ghosts removed;
        // second attempt with this sequence: 9/9 removed in one pass).
        var script = LoadScript();
        Assert.Contains("ShowWindow",         script);
        Assert.Contains("PostMessage",        script);
        Assert.Contains("SendMessageTimeout", script);
        Assert.Contains("0x0112",             script); // WM_SYSCOMMAND
        Assert.Contains("0xF060",             script); // SC_CLOSE
        Assert.Contains("0x0010",             script); // WM_CLOSE
        Assert.Contains("0x0002",             script); // SMTO_ABORTIFHUNG
    }

    [Fact]
    public void Script_IsInvokedFromBuildPs1()
    {
        // build.ps1 invokes the script after a successful build so MSIX
        // packaging tool ghosts (MakeAppx, signtool, WindowsAppSDK markup
        // compiler) are cleaned up before the developer notices. Pin the
        // wiring so a future refactor of build.ps1 can't silently drop it.
        var buildPs1 = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "build.ps1"));
        Assert.Contains("cleanup-ghost-windows.ps1", buildPs1);
    }

    [Fact]
    public void Script_RejectsTinyWindowsToProtectUserTerminals()
    {
        // The 1000x500 minimum size is a deliberate safety guard: if a real
        // user happens to have a small Terminal window with title "Terminal"
        // (i.e. they just opened it and haven't typed anything yet), we must
        // NOT close it. Production code enforces the same minimum.
        var script = LoadScript();
        Assert.Matches(@"-lt\s+1000",  script);  // PowerShell less-than check on width
        Assert.Matches(@"-lt\s+500",   script);  // height
    }

    [Theory]
    [InlineData("-Daemon",                  "long-running watcher for shell-heavy sessions")]
    [InlineData("-PollSeconds",             "configurable daemon poll interval")]
    [InlineData("-InstallScheduledTask",    "background recovery without keeping a console alive")]
    [InlineData("-UninstallScheduledTask",  "matching uninstaller")]
    public void Script_ExposesEscalationModesForOutOfBandLeaks(string switchName, string reason)
    {
        // We can't catch every Cascadia frame leak from our wired triggers
        // (testhost + build.ps1); ad-hoc shell invocations of gh/git/dotnet
        // outside of build.ps1 leak too. The escalation modes let a developer
        // who does heavy shell work on this branch keep their box clean.
        // Drift here breaks the AGENTS.md guidance and the recovery story
        // documented in commit de5e73e and beyond.
        var script = LoadScript();
        Assert.True(script.Contains(switchName),
            $"Cleanup script missing '{switchName}' switch ({reason}).");
    }

    [Fact]
    public void Script_ScheduledTaskName_IsStable()
    {
        // Pin the task name so the uninstaller matches what the installer
        // registered. AGENTS.md and any future support recipes that refer
        // to the task name will break silently if this drifts.
        var script = LoadScript();
        Assert.Contains("OpenClaw-Ghost-Terminal-Cleanup", script);
    }
}
