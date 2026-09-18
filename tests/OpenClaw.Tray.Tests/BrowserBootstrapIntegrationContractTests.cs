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
        Assert.Contains("RegisterBrowserNativeHost;", installer);
        Assert.Contains("BrowserNativeHostRegistered := ResultCode = 0", installer);
        Assert.Contains("No extension installation may be requested", installer);
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
        var targets = Read("src", "OpenClaw.Tray.WinUI", "BrowserNativeHost.targets");
        Assert.Contains("SelfContained=true", targets);
        Assert.Contains("win-x64", targets);
        Assert.Contains("win-arm64", targets);
        Assert.Contains("OpenClaw.BrowserNativeHost.exe", targets);
        var workflow = Read(".github", "workflows", "ci.yml");
        Assert.Equal(2, workflow.Split("- name: Prove packaged native browser host").Length - 1);
        Assert.Contains("OpenClaw.BrowserNativeHost.dll", workflow);
        Assert.Contains("Test-BrowserNativeHost.ps1", workflow);
        Assert.Contains("tools\\browser-bootstrap\\OpenClaw.BrowserNativeHost.exe", Read("scripts", "Test-ReleaseExecutableSignatures.ps1"));
    }
}
