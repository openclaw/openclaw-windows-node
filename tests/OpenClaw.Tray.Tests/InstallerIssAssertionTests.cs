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
    [Fact]
    public void Installer_HasAppMutexMatchingTraySingleInstance()
    {
        var iss = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss"));
        // Release build uses "OpenClawTray" mutex; dev build uses "OpenClawTray-Dev".
        // The installer default (non-DevBuild) must match the release mutex.
        Assert.Contains("AppMutex={#MyMutex}", iss);
        Assert.Contains(@"#define MyMutex ""OpenClawTray""", iss);
        Assert.Contains("Inno requires \"{{\" to emit a literal opening brace in AppId.", iss);
        Assert.Contains(@"#define MyAppId ""{{M0LTB0T-TRAY-4PP1-D3N7}""", iss);

        // The matching tray-side mutex name must be present in App.xaml.cs via AppIdentity.
        var appXamlCs = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("var mutexName = AppIdentity.MutexBaseName;", appXamlCs);
    }

    [Fact]
    public void Installer_DoesNotShipCommandPaletteExtension()
    {
        var iss = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss"));

        Assert.DoesNotContain("cmdpalette", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CommandPalette", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-AppxPackage", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-AppxPackage", iss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installer_CreatesStartMenuEntrypointsForTraySetupAndSupport()
    {
        var iss = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss"));

        Assert.Contains(@"#define MyAppName ""OpenClaw Companion""", iss);
        Assert.Contains(@"#define MyCompression ""lzma""", iss);
        Assert.Contains(@"#define MySolidCompression ""yes""", iss);
        Assert.Contains("OutputBaseFilename=OpenClawCompanion{#MyOutputSuffix}-Setup-{#MyAppArch}", iss);
        Assert.Contains(@"Name: ""{group}\{#MyAppName}""; Filename: ""{app}\{#MyAppExeName}""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Gateway Setup""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://setup""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Companion Settings""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://commandcenter""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Chat""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://chat""", iss);
        Assert.Contains(@"Name: ""{group}\Check for Updates""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://check-updates""", iss);
    }

    [Fact]
    public void Installer_RemovesGeneratedAppStateOnlyAfterGatewayCleanup()
    {
        var iss = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss"));

        Assert.DoesNotContain("[UninstallRun]", iss);
        Assert.Contains("[Code]", iss);
        Assert.Contains("Uninstall-LocalGateway.ps1", iss);
        Assert.Contains("UninstallSilent()", iss);
        Assert.Contains("LocalGatewayCleanupRequested := True", iss);
        Assert.Contains("{#MyDistroName} WSL distro", iss);
        Assert.Contains("MB_YESNO", iss);
        Assert.Contains("ExpandConstant('{sys}\\WindowsPowerShell\\v1.0\\powershell.exe')", iss);
        Assert.Contains("ewWaitUntilTerminated", iss);
        Assert.Contains("MB_RETRYCANCEL", iss);
        Assert.Contains("DeleteGeneratedAppState", iss);
        Assert.Contains("procedure RemoveAppAutoStart;", iss);
        Assert.Contains("    RemoveAppAutoStart;\n    EnsureLocalGatewayCleanupChoice;", iss);
        Assert.Contains("CurUninstallStep = usPostUninstall", iss);
        Assert.Contains("DelTree(ExpandConstant('{app}'), True, True, True)", iss);
        Assert.DoesNotContain("Start-Sleep -Seconds 3", iss);
        Assert.DoesNotContain("--uninstall --confirm-destructive", iss);
        Assert.DoesNotContain("[UninstallDelete]", iss);
    }

    [Fact]
    public void UninstallLocalGatewayScript_DirectlyUnregistersWslDistro()
    {
        var script = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "scripts", "Uninstall-LocalGateway.ps1"));

        Assert.Contains("$DistroName = 'OpenClawGateway'", script);
        Assert.Contains("'--list', '--quiet'", script);
        Assert.Contains("'--terminate', $DistroName", script);
        Assert.DoesNotContain("'--shutdown'", script);
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
        var iss = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss"));

        // Protocol registration uses preprocessor variable {#MyProtocol}
        Assert.Contains(@"Subkey: ""Software\Classes\{#MyProtocol}""", iss);
        Assert.Contains(@"ValueName: ""URL Protocol""", iss);
        Assert.Contains(@"Subkey: ""Software\Classes\{#MyProtocol}\shell\open\command""", iss);
        Assert.Contains(@"{app}\{#MyAppExeName}", iss);
        Assert.Contains(@"""%1""", iss);
        // Ensure release default protocol is "openclaw"
        Assert.Contains(@"#define MyProtocol ""openclaw""", iss);
    }

    [Fact]
    public void DevInstaller_UsesIndependentIdentityAndProtocol()
    {
        var iss = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss"));

        Assert.Contains(@"#define MyAppName ""OpenClaw Companion (Dev)""", iss);
        Assert.Contains(@"#define MyInstallDir ""OpenClawTray-Dev""", iss);
        Assert.Contains(@"#define MyMutex ""OpenClawTray-Dev""", iss);
        Assert.Contains(@"#define MyProtocol ""openclaw-dev""", iss);
        Assert.Contains(@"#define MyDistroName ""OpenClawGateway-Dev""", iss);
        Assert.Contains(@"#define MyAppPublisher ""OpenClaw Foundation""", iss);
        Assert.Contains("-DataDirectoryName ' + AddQuotes('{#MyInstallDir}')", iss);
        Assert.Contains("-AutoStartName ' + AddQuotes('{#MyAutoStartName}')", iss);
        Assert.Contains("-StartupTaskName ' + AddQuotes('{#MyStartupTaskName}')", iss);
        Assert.Contains("-DistroName ' + AddQuotes('{#MyDistroName}')", iss);

        var uninstallScript = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "scripts", "Uninstall-LocalGateway.ps1"));
        Assert.Contains("[string]$DataDirectoryName = 'OpenClawTray'", uninstallScript);
        Assert.Contains("-Name $AutoStartName", uninstallScript);
        Assert.Contains("/TN $StartupTaskName", uninstallScript);

        var autoStartManager = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Services", "AutoStartManager.cs"));
        Assert.Contains("AppIdentity.StartupTaskName", autoStartManager);
    }

    [Fact]
    public void LocalInstallerBuild_UsesOneIdentitySwitchAndValidatesPayloadMarker()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "build-inno-local.ps1"));
        var project = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));

        Assert.Contains("[switch]$Dev", script);
        Assert.Contains("-p:DevBuild=$($Dev.IsPresent.ToString().ToLowerInvariant())", script);
        Assert.Contains("$args += \"/DDevBuild=1\"", script);
        Assert.Contains("app-identity.txt", script);
        Assert.Contains("Payload identity", script);
        Assert.Contains("WritePublishedAppIdentityMarker", project);
        Assert.Contains("<AppIdentityMarker>dev</AppIdentityMarker>", project);
        Assert.Contains("<AppIdentityMarker>release</AppIdentityMarker>", project);
    }

    [Fact]
    public void MsixManifest_IsGeneratedUnderObjWithoutMutatingTrackedSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var manifest = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Package.appxmanifest"));

        Assert.Contains("GenerateOpenClawAppxManifest", project);
        Assert.Contains("$(IntermediateOutputPath)openclaw.Package.appxmanifest", project);
        Assert.Contains(@"<AppxManifest Remove=""@(AppxManifest)"" />", project);
        Assert.DoesNotContain("PatchDevAppxManifestIdentity", project);
        Assert.Contains("Version=\"0.0.0.0\"", manifest);
        Assert.Contains("Name=\"OpenClaw.Companion\"", manifest);
        Assert.Contains("<uap:Protocol Name=\"openclaw\">", manifest);
        Assert.DoesNotContain("OpenClaw.Companion.Dev", manifest);
    }

    [Fact]
    public void ReleaseBuildDoesNotShipSeparateSetupUiExecutable()
    {
        var iss = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss"));
        var ci = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), ".github", "workflows", "ci.yml"));

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
        var repositoryRoot = TestRepositoryPaths.GetRepositoryRoot();
        var packageJson = File.ReadAllText(Path.Combine(repositoryRoot, "package.json"));
        var packageLock = File.ReadAllText(Path.Combine(repositoryRoot, "package-lock.json"));
        var trayProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var iss = File.ReadAllText(Path.Combine(repositoryRoot, "installer.iss"));

        Assert.Contains(@"""@microsoft/mxc-sdk""", packageJson);
        Assert.Contains(@"""@microsoft/mxc-sdk"": ""^0.7.0""", packageJson);
        Assert.Contains(@"""node_modules/@microsoft/mxc-sdk""", packageLock);
        Assert.Contains(@"""version"": ""0.7.0""", packageLock);
        Assert.Contains("RestoreMxcNodeBridge", trayProject);
        Assert.Contains(@"Inputs=""$(OpenClawRepoRoot)package-lock.json""", trayProject);
        Assert.Contains(@"<MxcSdkRestoreStamp>$(OpenClawRepoRoot)node_modules\.openclaw-mxc-sdk-$(MxcSdkExpectedVersion).stamp</MxcSdkRestoreStamp>", trayProject);
        Assert.Contains(@"Outputs=""$(MxcSdkRestoreStamp)""", trayProject);
        Assert.Contains(@"<Touch Files=""$(MxcSdkRestoreStamp)"" AlwaysCreate=""true"" />", trayProject);
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
        var repositoryRoot = TestRepositoryPaths.GetRepositoryRoot();
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
