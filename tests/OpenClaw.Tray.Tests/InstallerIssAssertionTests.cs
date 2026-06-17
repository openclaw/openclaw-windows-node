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
        Assert.Contains("Inno requires \"{{\" to emit a literal opening brace in AppId.", iss);
        Assert.Contains("AppId={{M0LTB0T-TRAY-4PP1-D3N7}", iss);
        Assert.DoesNotContain("AppId={{M0LTB0T-TRAY-4PP1-D3N7}}", iss);

        // The matching tray-side mutex name must be present in App.xaml.cs.
        var appXamlCs = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("var mutexName = \"OpenClawTray\";", appXamlCs);
    }

    [Fact]
    public void Installer_DoesNotShipCommandPaletteExtension()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.DoesNotContain("cmdpalette", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CommandPalette", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-AppxPackage", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-AppxPackage", iss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installer_CreatesStartMenuEntrypointsForTraySetupAndSupport()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.Contains(@"#define MyAppName ""OpenClaw Companion""", iss);
        Assert.Contains(@"#define MyCompression ""lzma""", iss);
        Assert.Contains(@"#define MySolidCompression ""yes""", iss);
        Assert.Contains("OutputBaseFilename=OpenClawCompanion-Setup-{#MyAppArch}", iss);
        Assert.Contains(@"Name: ""{group}\{#MyAppName}""; Filename: ""{app}\{#MyAppExeName}""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Gateway Setup""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""openclaw://setup""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Companion Settings""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""openclaw://commandcenter""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Chat""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""openclaw://chat""", iss);
        Assert.Contains(@"Name: ""{group}\Check for Updates""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""openclaw://check-updates""", iss);
    }

    [Fact]
    public void Installer_RemovesGeneratedAppStateOnlyAfterGatewayCleanup()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.DoesNotContain("[UninstallRun]", iss);
        Assert.Contains("[Code]", iss);
        Assert.Contains("Uninstall-LocalGateway.ps1", iss);
        Assert.Contains("UninstallSilent()", iss);
        Assert.Contains("LocalGatewayCleanupRequested := True", iss);
        Assert.Contains("OpenClawGateway WSL distro", iss);
        Assert.Contains("MB_YESNO", iss);
        Assert.Contains("ExpandConstant('{sys}\\WindowsPowerShell\\v1.0\\powershell.exe')", iss);
        Assert.Contains("ewWaitUntilTerminated", iss);
        Assert.Contains("MB_RETRYCANCEL", iss);
        Assert.Contains("DeleteGeneratedAppState", iss);
        Assert.Contains("CurUninstallStep = usPostUninstall", iss);
        Assert.Contains("DelTree(ExpandConstant('{app}'), True, True, True)", iss);
        Assert.DoesNotContain("Start-Sleep -Seconds 3", iss);
        Assert.DoesNotContain("--uninstall --confirm-destructive", iss);
        Assert.DoesNotContain("[UninstallDelete]", iss);
    }

    [Fact]
    public void UninstallLocalGatewayScript_DirectlyUnregistersWslDistro()
    {
        var script = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "scripts", "Uninstall-LocalGateway.ps1"));

        Assert.Contains("$DistroName = 'OpenClawGateway'", script);
        Assert.Contains("'--list', '--quiet'", script);
        Assert.Contains("'--terminate', $DistroName", script);
        Assert.Contains("'--shutdown'", script);
        Assert.Contains("'--unregister', $DistroName", script);
        Assert.Contains("Start-Sleep -Seconds 2", script);
        Assert.Contains("Remove-GatewayDirectory", script);
        Assert.Contains("Remove-WindowsGatewayArtifacts", script);
        Assert.Contains("gateways.json", script);
        Assert.Contains("device-key-ed25519.json", script);
        Assert.Contains("OpenClawTray", script);
        Assert.Contains("setup-state.json", script);
        Assert.Contains("wsl-keepalive", script);
        Assert.Contains("Test-DistroListed", script);
        Assert.Contains("Test-DistroNotFound", script);
        Assert.Contains("FileAttributes]::ReparsePoint", script);
        Assert.Contains("Refusing to recursively delete reparse point", script);
        Assert.Contains("for ($attempt = 1; $attempt -le 6; $attempt++)", script);
        Assert.Contains("exit $unregisterResult.ExitCode", script);
        Assert.DoesNotContain("OpenClaw.Tray.WinUI.exe", script);
        Assert.DoesNotContain("OpenClaw.SetupEngine.UI.exe", script);
        Assert.DoesNotContain("--headless", script);
        Assert.DoesNotContain("--confirm-destructive", script);
    }

    [Fact]
    public void Installer_RegistersOpenClawProtocol()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.Contains(@"Subkey: ""Software\Classes\openclaw""", iss);
        Assert.Contains(@"ValueName: ""URL Protocol""", iss);
        Assert.Contains(@"Subkey: ""Software\Classes\openclaw\shell\open\command""", iss);
        Assert.Contains(@"{app}\{#MyAppExeName}", iss);
        Assert.Contains(@"""%1""", iss);
    }

    [Fact]
    public void ReleaseBuildDoesNotShipSeparateSetupUiExecutable()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));
        var ci = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains(@"FileExists(publish + ""\OpenClaw.Tray.WinUI.exe"")", iss);
        Assert.Contains(@"FileExists(publish + ""\SetupEngine\OpenClaw.SetupEngine.UI.exe"")", iss);
        Assert.Contains("SetupEngine.UI.exe should not be shipped", iss);
        Assert.DoesNotContain("Publish SetupEngine.UI", ci);
        Assert.DoesNotContain(@"dotnet publish src/OpenClaw.SetupEngine.UI", ci);
        Assert.DoesNotContain("publish-setup", ci);
        Assert.DoesNotContain(@"mkdir publish\SetupEngine", ci);
        Assert.DoesNotContain(@"copy publish-setup\* publish\SetupEngine\ -Recurse", ci);
    }

    [Fact]
    public void MxcSdk_IsRestoredCopiedValidatedAndIncludedInInstallerPayload()
    {
        var repositoryRoot = GetRepositoryRoot();
        var packageJson = File.ReadAllText(Path.Combine(repositoryRoot, "package.json"));
        var trayProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var iss = File.ReadAllText(Path.Combine(repositoryRoot, "installer.iss"));

        Assert.Contains(@"""@microsoft/mxc-sdk""", packageJson);
        Assert.Contains("RestoreMxcNodeBridge", trayProject);
        Assert.Contains("npm ci --no-audit --no-fund", trayProject);
        Assert.Contains("CopyWxcExecToOutput", trayProject);
        Assert.Contains("CopyWxcExecToPublish", trayProject);
        Assert.Contains("ValidateWxcExecShipped", trayProject);
        Assert.Contains("ValidateWxcExecPublished", trayProject);
        Assert.Contains(@"tools\mxc\$(MxcArch)\wxc-exec.exe", trayProject);

        // The Inno payload recurses through the prepared publish directory, so
        // publish-time tools\mxc\<arch>\wxc-exec.exe is shipped with the app.
        Assert.Contains(@"Source: ""{#publish}\*""; DestDir: ""{app}""; Flags: ignoreversion recursesubdirs", iss);
    }

    [Fact]
    public void MxcRuntime_ProbesShippedWxcExecAndSystemRunUsesIt()
    {
        var repositoryRoot = GetRepositoryRoot();
        var availability = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "OpenClaw.Shared", "Mxc", "MxcAvailability.cs"));
        var nodeService = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "OpenClaw.Tray.WinUI", "Services", "NodeService.cs"));

        Assert.Contains(@"Path.Combine(root, ""tools"", ""mxc"", arch, ""wxc-exec.exe"")", availability);
        Assert.Contains("WxcExecOverrideEnvVar", availability);
        Assert.Contains("node_modules", availability);
        Assert.Contains("@microsoft", availability);
        Assert.Contains("mxc-sdk", availability);

        Assert.Contains("private ICommandRunner BuildSystemRunRunner()", nodeService);
        Assert.Contains("MxcAvailability.Probe(_logger)", nodeService);
        Assert.Contains("new DirectAppContainerExecutor(GetOrProbeMxcAvailability, _logger)", nodeService);
        Assert.Contains("return new MxcCommandRunner(", nodeService);
    }

}
