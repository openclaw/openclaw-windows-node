using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Light-weight tests covering the data-side wiring of the redesigned ConnectionPage:
///   - 5-mode page-order behaviour (Local/Wsl/Remote/Ssh/Later)
///   - GatewayTopologyClassifier.Classify() outputs for each mode's canonical URL
///   - SshTunnelCommandLine preview generation matches the mockup format
    /// These tests do not spin up WinUI — they exercise the same public APIs the page calls
/// during Render(), so a regression in any of them will surface clearly.
/// </summary>
public class ConnectionPageTopologyTests
{
    private static OnboardingState CreateState(ConnectionMode m)
    {
        var s = new OnboardingState(new SettingsManager(CreateTempSettingsDirectory()));
        s.Mode = m;
        return s;
    }

    private static string CreateTempSettingsDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void GetPageOrder_WslMode_BehavesLikeLocal()
    {
        var s = CreateState(ConnectionMode.Wsl);
        s.ShowChat = true;
        var pages = s.GetPageOrder();
        Assert.Contains(OnboardingRoute.Wizard, pages);
        Assert.Equal(OnboardingRoute.Connection, pages[1]);
    }

    [Fact]
    public void GetPageOrder_SshMode_BehavesLikeLocal()
    {
        var s = CreateState(ConnectionMode.Ssh);
        s.ShowChat = true;
        var pages = s.GetPageOrder();
        Assert.Contains(OnboardingRoute.Wizard, pages);
    }

    [Theory]
    [InlineData("ws://localhost:18789",       false, GatewayKind.WindowsNative)]
    [InlineData("ws://wsl.localhost:18789",   false, GatewayKind.Wsl)]
    [InlineData("wss://relay.tailnet.ts.net", false, GatewayKind.Tailscale)]
    public void Classify_KnownLocalAndRemoteUrls_ReturnsExpectedKind(string url, bool useSsh, GatewayKind expected)
    {
        var info = GatewayTopologyClassifier.Classify(url, useSsh, sshHost: "", sshLocalPort: 0, sshRemotePort: 0);
        Assert.Equal(expected, info.DetectedKind);
    }

    [Fact]
    public void Classify_SshTunnel_ReportsMacOverSsh()
    {
        var info = GatewayTopologyClassifier.Classify(
            "ws://127.0.0.1:18789",
            useSshTunnel: true,
            sshHost: "mac-studio.local",
            sshLocalPort: 18789,
            sshRemotePort: 18789);
        Assert.Equal(GatewayKind.MacOverSsh, info.DetectedKind);
        Assert.True(info.UsesSshTunnel);
    }

    [Fact]
    public void SshTunnelCommandLine_PreviewIncludesBrowserProxyForward()
    {
        var args = SshTunnelCommandLine.BuildArguments(
            "harsh", "mac-studio.local", remotePort: 18789, localPort: 18789,
            includeBrowserProxyForward: true);

        Assert.Contains("-L 18789:127.0.0.1:18789", args);
        // BrowserProxyForward shifts both ports by +2 on the same forward
        Assert.Contains("18791:127.0.0.1:18791", args);
        Assert.Contains("harsh@mac-studio.local", args);
        Assert.Contains("BatchMode=yes", args);
    }

    [Fact]
    public void SshTunnelCommandLine_RejectsInvalidHost()
    {
        Assert.Throws<ArgumentException>(() => SshTunnelCommandLine.BuildArguments(
            "harsh", "bad host with spaces", 18789, 18789, includeBrowserProxyForward: false));
    }
}
