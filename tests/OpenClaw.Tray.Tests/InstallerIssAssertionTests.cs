using System.Diagnostics;
using System.Text.Json;
using OpenClaw.TestSupport;

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
        Assert.Contains(@"#define MyAppAumid ""OpenClaw.Companion""", iss);
        Assert.Contains(@"#define MyCompression ""lzma""", iss);
        Assert.Contains(@"#define MySolidCompression ""yes""", iss);
        Assert.Contains("OutputBaseFilename=OpenClawCompanion{#MyOutputSuffix}-Setup-{#MyAppArch}", iss);
        foreach (var iconEntry in new[]
        {
            @"Name: ""{group}\{#MyAppName}""; Filename: ""{app}\{#MyAppExeName}""; AppUserModelID: ""{#MyAppAumid}""",
            @"Name: ""{group}\OpenClaw Gateway Setup""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://setup""; IconFilename: ""{app}\{#MyAppExeName}""; AppUserModelID: ""{#MyAppAumid}""",
            @"Name: ""{group}\OpenClaw Companion Settings""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://commandcenter""; IconFilename: ""{app}\{#MyAppExeName}""; AppUserModelID: ""{#MyAppAumid}""",
            @"Name: ""{group}\OpenClaw Chat""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://chat""; IconFilename: ""{app}\{#MyAppExeName}""; AppUserModelID: ""{#MyAppAumid}""",
            @"Name: ""{group}\Check for Updates""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""{#MyProtocol}://check-updates""; IconFilename: ""{app}\{#MyAppExeName}""; AppUserModelID: ""{#MyAppAumid}""",
            @"Name: ""{autodesktop}\{#MyAppName}""; Filename: ""{app}\{#MyAppExeName}""; Tasks: desktopicon; AppUserModelID: ""{#MyAppAumid}""",
            @"Name: ""{userstartup}\{#MyAppName}""; Filename: ""{app}\{#MyAppExeName}""; Tasks: startupicon; AppUserModelID: ""{#MyAppAumid}"""
        })
        {
            Assert.Contains(iconEntry, iss);
        }
        Assert.DoesNotContain("AppUserModelID: \"OpenClaw.Tray.WinUI\"", iss);
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
        Assert.Contains("MB_YESNO or MB_DEFBUTTON2", iss);
        Assert.Contains("ExpandConstant('{sys}\\WindowsPowerShell\\v1.0\\powershell.exe')", iss);
        Assert.Contains("ewWaitUntilTerminated", iss);
        Assert.Contains("MB_RETRYCANCEL", iss);
        Assert.Contains("DeleteGeneratedAppState", iss);
        Assert.Contains("procedure RemoveAppAutoStart;", iss);
        Assert.Matches(@"    RemoveAppAutoStart;\r?\n    EnsureLocalGatewayCleanupChoice;", iss);
        Assert.Contains("CurUninstallStep = usPostUninstall", iss);
        Assert.DoesNotContain("DelTree(ExpandConstant('{app}'), True, True, True)", iss);
        Assert.Contains("Ownership uncertain: {app} is not the generated-data root", iss);
        Assert.Contains("ExpandConstant('{localappdata}\\{#MyInstallDir}')", iss);
        Assert.Contains("-RemoveConfirmedDistroChild", iss);
        Assert.Contains("Deleting only the confirmed {#MyDistroName} child under the generated-data root.", iss);
        Assert.DoesNotContain("DeleteGeneratedChild('wsl');", iss);
        foreach (var child in new[]
        {
            "Logs",
            "wsl-keepalive",
            "WebView2",
            "canvas",
            "native-cli",
            "setup-state.json",
            "run.marker",
            "exec-approvals.json",
            "exec-policy.json",
            "openclaw-tray.log",
            "uninstall-gateway-result.json",
            "uninstall-gateway-error.log",
            "uninstall-gateway-wsl.log",
        })
        {
            Assert.Contains($"DeleteGeneratedChild('{child}');", iss);
        }
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
    public async Task Uninstall_DeletesOnlyConfirmedDistroChildAndKeepsUncertainPaths()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var script = Path.Combine(root, "scripts", "Uninstall-LocalGateway.ps1");
        var temp = Directory.CreateTempSubdirectory("openclaw-uninstall-distro-");
        try
        {
            var localAppData = Path.Combine(temp.FullName, "local");
            var generatedRoot = Path.Combine(localAppData, "OpenClawTray");
            var configured = Path.Combine(generatedRoot, "wsl", "OpenClawGateway");
            var siblingVhdx = Path.Combine(generatedRoot, "wsl", "SiblingDistro", "ext4.vhdx");
            var lookalike = Path.Combine(temp.FullName, "custom", "OpenClawTray");
            var uncertainVhdx = Path.Combine(lookalike, "wsl", "OpenClawGateway", "ext4.vhdx");
            Directory.CreateDirectory(configured);
            Directory.CreateDirectory(Path.GetDirectoryName(siblingVhdx)!);
            Directory.CreateDirectory(Path.GetDirectoryName(uncertainVhdx)!);
            File.WriteAllText(Path.Combine(configured, "ext4.vhdx"), "configured");
            File.WriteAllText(siblingVhdx, "sibling");
            File.WriteAllText(uncertainVhdx, "uncertain");

            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo(powershell)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-RemoveConfirmedDistroChild",
                "-AppRoot", lookalike,
                "-DataDirectoryName", "OpenClawTray",
                "-DistroName", "OpenClawGateway",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = localAppData;
            startInfo.Environment["OPENCLAW_TRAY_LOCAL_DATA_DIR"] = "";
            startInfo.Environment["OPENCLAW_TRAY_DATA_DIR"] = "";

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var result = $"{await standardOutput}{Environment.NewLine}{await standardError}";
            var logPath = Path.Combine(lookalike, "uninstall-gateway-wsl.log");
            var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";

            Assert.True(
                process.ExitCode == 0,
                $"Confirmed distro cleanup failed with exit code {process.ExitCode}.{Environment.NewLine}{result}{Environment.NewLine}{log}");
            Assert.False(Directory.Exists(configured));
            Assert.True(File.Exists(siblingVhdx));
            Assert.True(File.Exists(uncertainVhdx));
            Assert.Contains(Path.GetDirectoryName(siblingVhdx)!, log, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Ownership uncertain", log, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(lookalike, log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("custom", "no-wsl", 0, false)]
    [InlineData("custom", "absent", 0, false)]
    [InlineData("custom", "unregistered", 0, false)]
    [InlineData("custom", "not-found", 0, false)]
    [InlineData("custom", "failed", 7, false)]
    [InlineData("lookalike", "no-wsl", 0, false)]
    [InlineData("lookalike", "absent", 0, false)]
    [InlineData("lookalike", "unregistered", 0, false)]
    [InlineData("lookalike", "not-found", 0, false)]
    [InlineData("lookalike", "failed", 7, false)]
    [InlineData("generated", "no-wsl", 0, true)]
    [InlineData("generated", "absent", 0, true)]
    [InlineData("generated", "unregistered", 0, true)]
    [InlineData("generated", "not-found", 0, true)]
    [InlineData("generated", "failed", 7, false)]
    [InlineData("root-junction", "unregistered", 1, false)]
    [InlineData("root-junction", "unregistered-empty", 1, false)]
    [InlineData("wsl-junction", "unregistered", 1, false)]
    [InlineData("wsl-junction", "unregistered-empty", 1, false)]
    [InlineData("child-junction", "unregistered", 1, false)]
    public async Task Uninstall_PrimaryPhase_BindsDeletionToGeneratedRoot_WithModeledTransport(
        string layout, string scenario, int expectedExitCode, bool deleted)
    {
        using var temp = new TempDirectory("openclaw-uninstall-primary-");
        var localAppData = temp.Combine("local");
        var generatedRoot = Path.Combine(localAppData, "OpenClawTray");
        var appRoot = layout switch
        {
            "custom" => temp.Combine("custom"),
            "lookalike" => temp.Combine("custom", "OpenClawTray"),
            _ => generatedRoot,
        };
        var configuredVhd = Path.Combine(appRoot, "wsl", "ChosenGateway", "ext4.vhdx");
        var generatedVhd = Path.Combine(generatedRoot, "wsl", "ChosenGateway", "ext4.vhdx");
        var siblingVhd = Path.Combine(appRoot, "wsl", "SiblingDistro", "ext4.vhdx");
        var defaultVhd = Path.Combine(appRoot, "wsl", "OpenClawGateway", "ext4.vhdx");
        var junctionTarget = temp.Combine("junction-target");
        var junctionVhd = layout switch
        {
            "root-junction" => Path.Combine(junctionTarget, "wsl", "ChosenGateway", "ext4.vhdx"),
            "wsl-junction" => Path.Combine(junctionTarget, "ChosenGateway", "ext4.vhdx"),
            _ => Path.Combine(junctionTarget, "ext4.vhdx"),
        };
        foreach (var path in new[] { configuredVhd, generatedVhd, siblingVhd, defaultVhd, junctionVhd }.Distinct())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "owned sentinel");
        }
        var targetHasChild = scenario != "unregistered-empty";
        if (!targetHasChild)
        {
            File.Delete(junctionVhd);
            Directory.Delete(Path.GetDirectoryName(junctionVhd)!);
        }

        var harnessPath = temp.Combine("primary-phase.ps1");
        // Only the production first-phase AST and allowlisted functions run. Native WSL,
        // Windows artifact cleanup, and delays are modeled; filesystem guards and logs are real.
        File.WriteAllText(harnessPath, """
            param([string]$SourceScript, [string]$AppRoot, [string]$Scenario, [string]$Layout, [string]$JunctionTarget)
            $ErrorActionPreference = 'Stop'
            $DataDirectoryName = 'OpenClawTray'
            $DistroName = 'ChosenGateway'
            $RemoveConfirmedDistroChild = $false
            $tokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($SourceScript, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
            foreach ($statement in $ast.EndBlock.Statements) {
                if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) { break }
                . ([scriptblock]::Create($statement.Extent.Text))
            }
            foreach ($name in @(
                'Ensure-AppRoot', 'Write-GatewayLog', 'Add-CleanupWarning', 'Write-GatewayResult',
                'Resolve-LocalDataDir', 'Test-SameFullPath', 'Remove-GatewayDirectory',
                'Test-DistroListed', 'Test-DistroNotFound', 'Complete-GatewayCleanup'
            )) {
                $definition = @($ast.EndBlock.Statements | Where-Object {
                    $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name
                })
                if ($definition.Count -ne 1) { throw "Expected one production function: $name" }
                . ([scriptblock]::Create($definition[0].Extent.Text))
            }
            function Get-WslExePath {
                if ($Scenario -eq 'no-wsl') { return $null }
                return 'modeled-wsl'
            }
            function Invoke-Wsl {
                param([string[]]$Arguments)
                $call = $Arguments -join ' '
                Add-Content -LiteralPath (Join-Path $AppRoot 'modeled-transport.log') -Value $call
                switch ($call) {
                    '--list --quiet' {
                        $output = if ($Scenario -eq 'absent') { 'SiblingDistro' } else { $DistroName }
                        return [pscustomobject]@{ ExitCode = 0; Output = $output }
                    }
                    '--terminate ChosenGateway' { return [pscustomobject]@{ ExitCode = 0; Output = '' } }
                    '--unregister ChosenGateway' {
                        if ($Scenario -eq 'failed') { return [pscustomobject]@{ ExitCode = 7; Output = 'Modeled unregister failure' } }
                        if ($Scenario -eq 'not-found') { return [pscustomobject]@{ ExitCode = 1; Output = 'WSL_E_DISTRO_NOT_FOUND' } }
                        return [pscustomobject]@{ ExitCode = 0; Output = '' }
                    }
                    default { throw "Unexpected modeled WSL call: $call" }
                }
            }
            function Remove-WindowsGatewayArtifacts { Write-GatewayLog 'Modeled Windows artifact cleanup.' }
            function Start-Sleep { param([int]$Seconds) }
            if ($Layout -in @('root-junction', 'wsl-junction', 'child-junction')) {
                $link = Join-Path $AppRoot 'wsl'
                if ($Layout -eq 'root-junction') { $link = $AppRoot }
                if ($Layout -eq 'child-junction') { $link = Join-Path $link $DistroName }
                Move-Item -LiteralPath $link -Destination ($link + '-before-junction') -ErrorAction Stop
                New-Item -ItemType Junction -Path $link -Target $JunctionTarget -ErrorAction Stop | Out-Null
            }
            $entrypoint = @($ast.EndBlock.Statements | Where-Object {
                $_ -is [System.Management.Automation.Language.TryStatementAst]
            })
            if ($entrypoint.Count -ne 1) { throw 'Expected one production first-phase entrypoint.' }
            & ([scriptblock]::Create($entrypoint[0].Extent.Text))
            """);

        var root = TestRepositoryPaths.GetRepositoryRoot();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath,
            "-SourceScript", Path.Combine(root, "scripts", "Uninstall-LocalGateway.ps1"),
            "-AppRoot", appRoot, "-Scenario", scenario, "-Layout", layout, "-JunctionTarget", junctionTarget,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = localAppData;
        startInfo.Environment["OPENCLAW_TRAY_LOCAL_DATA_DIR"] = "";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        var output = $"{await stdout}{Environment.NewLine}{await stderr}";
        Assert.True(process.ExitCode == expectedExitCode, $"Exit {process.ExitCode}: {output}");
        Assert.Equal(!deleted && targetHasChild, File.Exists(configuredVhd));
        Assert.Equal(targetHasChild, File.Exists(junctionVhd));
        if (targetHasChild)
        {
            Assert.Equal("owned sentinel", File.ReadAllText(junctionVhd));
        }
        var preservedWslRoot = layout switch
        {
            "root-junction" => Path.Combine(appRoot + "-before-junction", "wsl"),
            "wsl-junction" => Path.Combine(appRoot, "wsl-before-junction"),
            _ => Path.Combine(appRoot, "wsl"),
        };
        Assert.Equal("owned sentinel", File.ReadAllText(Path.Combine(preservedWslRoot, "SiblingDistro", "ext4.vhdx")));
        Assert.Equal("owned sentinel", File.ReadAllText(Path.Combine(preservedWslRoot, "OpenClawGateway", "ext4.vhdx")));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(appRoot, "uninstall-gateway-result.json")));
        Assert.Equal(expectedExitCode == 0, result.RootElement.GetProperty("succeeded").GetBoolean());
        var log = File.ReadAllText(Path.Combine(appRoot, "uninstall-gateway-wsl.log"));
        if (layout is "custom" or "lookalike")
        {
            Assert.Equal("owned sentinel", File.ReadAllText(generatedVhd));
            if (expectedExitCode == 0)
            {
                Assert.Contains("Ownership uncertain", log);
                Assert.Contains("Ownership uncertain",
                    result.RootElement.GetProperty("details").GetProperty("artifactWarnings")[0].GetString()!);
            }
        }
        if (layout.EndsWith("-junction", StringComparison.Ordinal))
        {
            Assert.Contains("Refusing to recursively delete reparse point", log);
        }
        var calls = File.Exists(Path.Combine(appRoot, "modeled-transport.log"))
            ? File.ReadAllLines(Path.Combine(appRoot, "modeled-transport.log"))
            : [];
        Assert.Equal(scenario switch
        {
            "no-wsl" => [],
            "absent" => ["--list --quiet"],
            _ => new[] { "--list --quiet", "--terminate ChosenGateway", "--unregister ChosenGateway" },
        }, calls);
        Assert.Equal(expectedExitCode == 0, log.Contains("Modeled Windows artifact cleanup.", StringComparison.Ordinal));
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
        Assert.Contains(@"#define MyAppAumid ""OpenClaw.Companion.Dev""", iss);
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
        var runScript = File.ReadAllText(Path.Combine(root, "run-app-local.ps1"));
        var buildScript = File.ReadAllText(Path.Combine(root, "build.ps1"));
        var project = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));

        Assert.Contains("[switch]$Dev", script);
        Assert.Contains("-p:DevBuild=$($Dev.IsPresent.ToString().ToLowerInvariant())", script);
        Assert.Contains("$args += \"/DDevBuild=1\"", script);
        Assert.Contains("app-identity.txt", script);
        Assert.Contains("Payload identity", script);
        Assert.Contains("2>&1 | Out-Host", script);
        Assert.Contains("$wingetExitCode = $LASTEXITCODE", script);
        Assert.Contains("[switch]$Dev,", runScript);
        Assert.Contains("$buildArgs = @{", runScript);
        Assert.Contains("Configuration = $Configuration", runScript);
        Assert.Contains("$buildArgs[\"DevBuild\"] = $true", runScript);
        Assert.Contains("app-identity.txt", runScript);
        Assert.Contains("does not match requested", runScript);
        Assert.Contains("[switch]$DevBuild,", buildScript);
        Assert.Contains("$dotnetArgs += \"-p:DevBuild=true\"", buildScript);
        Assert.Contains("-UseWinApp$runIdentitySwitch", buildScript);
        Assert.Contains("WritePublishedAppIdentityMarker", project);
        Assert.Contains("WriteBuildAppIdentityMarker", project);
        Assert.Contains("<AppIdentityMarker>dev</AppIdentityMarker>", project);
        Assert.Contains("<AppIdentityMarker>release</AppIdentityMarker>", project);
        Assert.DoesNotContain("'$(Configuration)' == 'Debug'", project);
        Assert.DoesNotContain("<DevBuild>true</DevBuild>", project);
    }

    [Fact]
    public async Task RunAppLocal_ExplicitArm64Runtime_WinsOverEmulatedX64Shell()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var script = Path.Combine(root, "run-app-local.ps1");
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", script,
            "-NoBuild",
            "-Configuration", "Release",
            "-RuntimeIdentifier", "win-arm64",
            "-AllowNonMain",
            "-DryRun",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PROCESSOR_ARCHITECTURE"] = "AMD64";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var result = $"{await standardOutput}{Environment.NewLine}{await standardError}";

        Assert.Contains("Selected runtime: win-arm64", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Selected runtime: win-x64", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Release", false, false, "win-x64", true)]
    [InlineData("Release", false, true, "win-x64", true)]
    [InlineData("Release", false, false, "win-arm64", true)]
    [InlineData("Release", false, true, "win-arm64", true)]
    [InlineData("Release", true, false, "win-x64", false)]
    [InlineData("Debug", false, false, "win-x64", false)]
    public async Task ComWrapperDiagnosticsSwitch_EvaluatesOnlyForProductionBuilds(
        string configuration,
        bool devBuild,
        bool packageMsix,
        string runtimeIdentifier,
        bool expected)
    {
        var repositoryRoot = TestRepositoryPaths.GetRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "OpenClaw.Tray.WinUI",
            "OpenClaw.Tray.WinUI.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-v:q");
        startInfo.ArgumentList.Add("-getItem:RuntimeHostConfigurationOption");
        startInfo.ArgumentList.Add($"-p:Configuration={configuration}");
        startInfo.ArgumentList.Add($"-p:DevBuild={devBuild.ToString().ToLowerInvariant()}");
        startInfo.ArgumentList.Add($"-p:PackageMsix={packageMsix.ToString().ToLowerInvariant()}");
        startInfo.ArgumentList.Add($"-p:RuntimeIdentifier={runtimeIdentifier}");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();

            throw new TimeoutException(
                $"MSBuild evaluation timed out.{Environment.NewLine}" +
                $"Standard output:{Environment.NewLine}{await standardOutput}{Environment.NewLine}" +
                $"Standard error:{Environment.NewLine}{await standardError}");
        }

        var output = await standardOutput;
        var error = await standardError;

        Assert.True(
            process.ExitCode == 0,
            $"MSBuild evaluation failed with exit code {process.ExitCode}.{Environment.NewLine}" +
            $"Standard output:{Environment.NewLine}{output}{Environment.NewLine}" +
            $"Standard error:{Environment.NewLine}{error}");

        JsonNode? result;
        try
        {
            result = JsonNode.Parse(output);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException(
                $"MSBuild evaluation returned invalid JSON:{Environment.NewLine}{output}",
                exception);
        }

        var options = result?["Items"]?["RuntimeHostConfigurationOption"]?.AsArray();
        Assert.NotNull(options);
        var matchingOptions = options
            .Where(option =>
                string.Equals(
                    option?["Identity"]?.GetValue<string>(),
                    "System.Diagnostics.Debugger.IsSupported",
                    StringComparison.Ordinal))
            .ToArray();

        if (!expected)
        {
            Assert.Empty(matchingOptions);
            return;
        }

        var matchingOption = Assert.Single(matchingOptions);
        Assert.Equal("false", matchingOption?["Value"]?.GetValue<string>());
        Assert.Equal("true", matchingOption?["Trim"]?.GetValue<string>());
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
        Assert.Contains("Name=\"OpenClawFoundation.OpenClaw\"", manifest);
        Assert.Contains("<uap:Protocol Name=\"openclaw\">", manifest);
        Assert.DoesNotContain("OpenClawFoundation.OpenClaw.Dev", manifest);
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
        Assert.Contains(@"""@microsoft/mxc-sdk"": ""^0.8.0""", packageJson);
        Assert.Contains(@"""node_modules/@microsoft/mxc-sdk""", packageLock);
        Assert.Contains(@"""version"": ""0.8.0""", packageLock);
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
