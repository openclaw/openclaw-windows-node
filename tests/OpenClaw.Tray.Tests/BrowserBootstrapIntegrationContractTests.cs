namespace OpenClaw.Tray.Tests;

public class BrowserBootstrapIntegrationContractTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void Installer_RegistersNativeHelperBeforeTrayWithoutRequestingElevation()
    {
        var installer = Read("installer.iss");
        Assert.Contains("if CurStep = ssPostInstall then", installer);
        Assert.Contains("RegisterBrowserIntegration;", installer);
        Assert.Contains("UsePreviousTasks=yes", installer);
        Assert.Contains("WizardIsTaskSelected('chromeextension')", installer);
        Assert.Contains("RunBrowserManagement('install', StoreAction)", installer);
        Assert.Contains("RunBrowserManagement('uninstall', 'remove')", installer);
        var transport=Read("scripts","BrowserBootstrapManagement.iss");
        Assert.Contains(" --manage",transport);
        Assert.Contains("BBWrite(InW, Input",transport);
        Assert.Contains("BBDispose(InW)",transport);
        Assert.Contains("32768",transport);
        Assert.Contains("60000",transport);
        Assert.Contains("KILL_ON_JOB_CLOSE",transport);
        Assert.Contains("BBReceipt(Output, ExitCode",transport);
        Assert.DoesNotContain("--register",installer);
        Assert.DoesNotContain("--request-extension",installer);
        Assert.Contains("PrivilegesRequired=lowest", installer);
        Assert.Contains("UnregisterBrowserNativeHost;", installer);
        Assert.DoesNotContain("ExtensionInstallForcelist", installer);
        Assert.DoesNotContain("Preferences", installer);
    }

    [Fact]
    public void Bootstrap_UsesExistingOwnersAndExcludesIsolatedProfiles()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var host = Read("src", "OpenClaw.Tray.WinUI", "Services", "BrowserBootstrapHost.cs");
        Assert.Contains("!AppIdentity.IsDev", app);
        Assert.Contains("OPENCLAW_TRAY_DATA_DIR", app);
        Assert.Contains("manager.IsAutomaticReconnectAllowed(record.Id)", host);
        Assert.Contains("settings.NodeBrowserProxyEnabled", host);
        Assert.Contains("RoleConnectionState.Connected", host);
        Assert.Contains("provenance.InspectAsync", host);
        Assert.DoesNotContain("SharedGatewayToken", host);
        Assert.DoesNotContain("BootstrapToken", host);
        Assert.Contains("await browserBootstrapHost.DisposeAsync()", Read("src", "OpenClaw.Tray.WinUI", "App.AppShutdownCoordinator.cs"));
    }

    [Fact]
    public void Packaging_RequiresRealExeAndBothArchitecturesProveIt()
    {
        var targets = Read("src", "OpenClaw.Tray.WinUI", "BrowserBootstrap.targets");
        Assert.Contains("SelfContained=true", targets);
        Assert.Contains("win-x64", targets);
        Assert.Contains("win-arm64", targets);
        Assert.Contains("OpenClaw.BrowserBootstrap.exe", targets);
        var workflow = Read(".github", "workflows", "ci.yml");
        Assert.Equal(2, workflow.Split("- name: Prove packaged native browser host").Length - 1);
        Assert.DoesNotContain("OpenClaw.BrowserNativeHost.dll", workflow);
        Assert.Contains("OpenClaw.BrowserBootstrap.Contracts.dll", workflow);
        Assert.Contains("Test-BrowserNativeHost.ps1", workflow);
        Assert.Contains("tools\\browser-bootstrap\\OpenClaw.BrowserBootstrap.exe", Read("scripts", "Test-ReleaseExecutableSignatures.ps1"));
    }
}
