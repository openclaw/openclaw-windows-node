using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class CommandCenterStateBuilderTests
{
    [Fact]
    public void BrowserProxyAuthWarning_ShowsOnlyWhenNodeSessionLiveAndSharedTokenMissing()
    {
        Assert.True(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            nodeSessionLive: true));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            nodeSessionLive: false));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: true,
            nodeSessionLive: true));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: false,
            activeGatewayHasSharedToken: false,
            nodeSessionLive: true));
    }

    [Fact]
    public void SnapshotTopology_RemoteAndSshWithoutForwardMatchSharedRemediationDetail()
    {
        Assert.True(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            gatewayUrl: "wss://gateway.example.com",
            browserControlPort: null,
            sshTunnel: null));
        Assert.True(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            gatewayUrl: "ws://127.0.0.1:18789",
            browserControlPort: null,
            sshTunnel: new SshTunnelConfig("dev", "remote.example", 22, 18789, IncludeBrowserProxyForward: false)));
        Assert.False(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            gatewayUrl: "ws://127.0.0.1:18789",
            browserControlPort: null,
            sshTunnel: null));

        var remoteDetail = BrowserProxyActivation.BuildMissingSharedTokenWarningDetail(true);
        Assert.Contains("browser-control port", remoteDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SSH browser-proxy forward", remoteDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "shared token alone is not enough",
            remoteDetail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandCenterStateBuilder_WiresSharedRemoteEndpointRemediation()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "CommandCenterStateBuilder.cs"));
        var page = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs"));

        Assert.Contains(
            "BrowserProxyActivation.RequiresRemoteBrowserEndpoint(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserProxyActivation.BuildMissingSharedTokenWarningDetail(requiresRemoteEndpoint)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserProxyActivation.BuildMissingSharedTokenCopyText(requiresRemoteEndpoint)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserProxyActivation.BuildUnverifiedSshBrowserEndpointDetail()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserProxyActivation.IsSshBrowserEndpointVerified(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserProxyActivation.ResolveRemediation(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserProxyActivation.ResolveCapabilityPillTooltip(",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "CapabilityPillKind.NeedsVerifiedEndpoint",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandCenter_BrowserProxyHostNotDetected",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "activeGateway?.Url ?? settings.GatewayUrl",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserProxyActivation.RequiresRemoteBrowserEndpoint(",
            page,
            StringComparison.Ordinal);
    }
}
