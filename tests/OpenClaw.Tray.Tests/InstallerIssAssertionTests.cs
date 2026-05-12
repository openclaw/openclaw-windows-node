namespace OpenClaw.Tray.Tests;

/// <summary>
/// Structural assertions on installer.iss.  These pin contracts that cannot
/// be exercised by an in-process unit test because they require ISCC + the
/// resulting unins000.exe to verify end-to-end.
///
/// Round 2 (Scott #5) — AppMutex coordination prevents the Inno uninstaller
/// from racing the running tray on shared state (settings.json,
/// gateways.json, device-key-ed25519.json, Logs/).  The mutex name must
/// match App.xaml.cs's single-instance mutex.
/// </summary>
public sealed class InstallerIssAssertionTests
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

    [Fact]
    public void Installer_HasAppMutexMatchingTraySingleInstance()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));
        Assert.Contains("AppMutex=OpenClawTray", iss);

        // The matching tray-side mutex name must be present in App.xaml.cs.
        var appXamlCs = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("var mutexName = \"OpenClawTray\";", appXamlCs);
    }

    /// <summary>
    /// Round 2 (Bot B2) — the keepalive process killer must use
    /// entireProcessTree:true. wsl.exe spawns wslhost.exe and in-distro
    /// processes; killing only the parent leaves children holding distro
    /// state and blocks wsl --unregister. Spawning a real WSL process tree
    /// in a unit test is brittle, so we pin the source contract instead.
    /// </summary>
    [Fact]
    public void StopKeepalive_KillsEntireProcessTree_SourceAssertion()
    {
        var src = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "LocalGatewaySetup",
            "LocalGatewayUninstall.cs"));

        Assert.Contains("proc.Kill(entireProcessTree: true)", src);
        Assert.DoesNotContain("proc.Kill(entireProcessTree: false)", src);
    }
}
