using OpenClaw.Connection;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class BrowserProxyActivationTests
{
    [Fact]
    public void Registration_RequiresToggleClientAndSharedToken()
    {
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.ToggleDisabled,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: false,
                sharedGatewayToken: "token",
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.NoGatewayClient,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: "token",
                hasGatewayClient: false));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.MissingSharedGatewayToken,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: null,
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.MissingSharedGatewayToken,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: "  ",
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.None,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: "token",
                hasGatewayClient: true));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.UnverifiedBrowserEndpoint,
            BrowserProxyActivation.ResolveRegistrationBlock(
                toggleEnabled: true,
                sharedGatewayToken: "token",
                hasGatewayClient: true,
                browserEndpointVerified: false));
        Assert.True(BrowserProxyActivation.ShouldRegister(true, "token", true));
        Assert.False(BrowserProxyActivation.ShouldRegister(true, null, true));
        Assert.False(BrowserProxyActivation.ShouldRegister(
            true,
            "token",
            true,
            browserEndpointVerified: false));
    }

    [Fact]
    public void MissingSharedTokenWarning_RequiresLiveNodeSession()
    {
        Assert.True(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            nodeSessionLive: true));
        Assert.False(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            nodeSessionLive: false));
        // Token present must not ask for a token paste even when the session is live.
        Assert.False(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: true,
            nodeSessionLive: true));
        Assert.False(BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled: false,
            activeGatewayHasSharedToken: false,
            nodeSessionLive: true));
    }

    [Fact]
    public void Remediation_PrioritizesMissingTokenBeforeEndpoint()
    {
        Assert.Equal(
            BrowserProxyActivation.RemediationKind.MissingSharedToken,
            BrowserProxyActivation.ResolveRemediation(
                toggleEnabled: true,
                hasSharedGatewayToken: false,
                nodeSessionLive: true,
                browserEndpointVerified: false));
        Assert.Equal(
            BrowserProxyActivation.RemediationKind.UnverifiedEndpoint,
            BrowserProxyActivation.ResolveRemediation(
                toggleEnabled: true,
                hasSharedGatewayToken: true,
                nodeSessionLive: true,
                browserEndpointVerified: false));
        Assert.Equal(
            BrowserProxyActivation.RemediationKind.None,
            BrowserProxyActivation.ResolveRemediation(
                toggleEnabled: true,
                hasSharedGatewayToken: true,
                nodeSessionLive: true,
                browserEndpointVerified: true));
    }

    [Fact]
    public void CapabilityPill_UsesNeedsSharedTokenOnlyWhenNodeSessionLive()
    {
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.NeedsSharedToken,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                nodeSessionLive: true));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.PendingApproval,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                nodeSessionLive: false));
        // Attached-but-disconnected must not claim NeedsSharedToken.
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.PendingApproval,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: true,
                hasSharedGatewayToken: true,
                nodeSessionLive: true));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.Active,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: true,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                nodeSessionLive: false));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.Off,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: false,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                nodeSessionLive: false));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.NeedsVerifiedEndpoint,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: true,
                nodeSessionLive: true,
                browserEndpointVerified: false));
        Assert.Equal(
            BrowserProxyActivation.CapabilityPillKind.NeedsSharedToken,
            BrowserProxyActivation.ResolveCapabilityPillKind(
                toggleEnabled: true,
                effective: false,
                pendingDeclared: false,
                hasSharedGatewayToken: false,
                nodeSessionLive: true,
                browserEndpointVerified: false));
    }

    [Fact]
    public void RemoteEndpointRequirement_IgnoresLocalAndConfiguredRemotes()
    {
        Assert.False(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            isLocalGateway: true,
            gatewayUrl: "wss://remote.example",
            browserControlPort: null,
            hasSshTunnelConfigured: false,
            sshBrowserProxyForwardEnabled: false));
        Assert.False(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            isLocalGateway: false,
            gatewayUrl: "ws://127.0.0.1:18789",
            browserControlPort: null,
            hasSshTunnelConfigured: false,
            sshBrowserProxyForwardEnabled: false));
        Assert.False(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            isLocalGateway: false,
            gatewayUrl: "wss://remote.example",
            browserControlPort: 18791,
            hasSshTunnelConfigured: false,
            sshBrowserProxyForwardEnabled: false));
        Assert.False(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            isLocalGateway: false,
            gatewayUrl: "wss://remote.example",
            browserControlPort: null,
            hasSshTunnelConfigured: true,
            sshBrowserProxyForwardEnabled: true));
        Assert.True(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            isLocalGateway: false,
            gatewayUrl: "wss://remote.example",
            browserControlPort: null,
            hasSshTunnelConfigured: false,
            sshBrowserProxyForwardEnabled: false));
        // SSH local forward URL still needs remote endpoint guidance when forward is off.
        Assert.True(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            isLocalGateway: false,
            gatewayUrl: "ws://127.0.0.1:18789",
            browserControlPort: null,
            hasSshTunnelConfigured: true,
            sshBrowserProxyForwardEnabled: false));
    }

    [Fact]
    public void SshBrowserEndpoint_RequiresManagedForwardOrMatchingOverride()
    {
        var tunnel = new SshTunnelConfig(
            "user",
            "host.example",
            RemotePort: 18789,
            LocalPort: 9100,
            IncludeBrowserProxyForward: true);

        Assert.True(BrowserProxyActivation.IsSshBrowserEndpointVerified(tunnel, null));
        Assert.True(BrowserProxyActivation.IsSshBrowserEndpointVerified(tunnel, 9102));
        Assert.False(BrowserProxyActivation.IsSshBrowserEndpointVerified(tunnel, 19000));
        Assert.False(BrowserProxyActivation.IsSshBrowserEndpointVerified(
            tunnel with { IncludeBrowserProxyForward = false },
            19000));
        Assert.True(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            "wss://remote.example",
            19000,
            tunnel));
        Assert.False(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(
            "wss://remote.example",
            9102,
            tunnel));
        Assert.Contains(
            "managed browser-proxy forward",
            BrowserProxyActivation.BuildUnverifiedSshBrowserEndpointDetail(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSharedTokenGuidance_MentionsRemoteEndpointWhenNeeded()
    {
        var local = BrowserProxyActivation.BuildMissingSharedTokenCaveat(requiresRemoteBrowserEndpoint: false);
        var remote = BrowserProxyActivation.BuildMissingSharedTokenCaveat(requiresRemoteBrowserEndpoint: true);
        Assert.Contains("shared token", local, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("browser-control port", local, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("browser-control port", remote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SSH browser-proxy forward", remote, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "browser-control port",
            BrowserProxyActivation.BuildMissingSharedTokenWarningDetail(true),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "SSH browser-proxy forward",
            BrowserProxyActivation.BuildMissingSharedTokenCopyText(true),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityPillTooltip_UsesSharedRemediationDetailForRemoteTopologies()
    {
        const string shortLabel = "Needs gateway shared token";
        var localTooltip = BrowserProxyActivation.ResolveCapabilityPillTooltip(
            BrowserProxyActivation.CapabilityPillKind.NeedsSharedToken,
            shortLabel,
            requiresRemoteBrowserEndpoint: false);
        var remoteTooltip = BrowserProxyActivation.ResolveCapabilityPillTooltip(
            BrowserProxyActivation.CapabilityPillKind.NeedsSharedToken,
            shortLabel,
            requiresRemoteBrowserEndpoint: true);

        Assert.Equal(
            BrowserProxyActivation.BuildMissingSharedTokenWarningDetail(false),
            localTooltip);
        Assert.Equal(
            BrowserProxyActivation.BuildMissingSharedTokenWarningDetail(true),
            remoteTooltip);
        Assert.Contains("browser-control port", remoteTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("browser-control port", localTooltip, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            shortLabel,
            BrowserProxyActivation.ResolveCapabilityPillTooltip(
                BrowserProxyActivation.CapabilityPillKind.PendingApproval,
                shortLabel,
                requiresRemoteBrowserEndpoint: true));

        Assert.True(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(new GatewayRecord
        {
            Id = "gw-remote",
            Url = "wss://gateway.example.com",
            IsLocal = false,
        }));
        Assert.True(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(new GatewayRecord
        {
            Id = "gw-ssh",
            Url = "ws://127.0.0.1:18789",
            IsLocal = false,
            SshTunnel = new SshTunnelConfig("dev", "remote.example", 22, 18789, IncludeBrowserProxyForward: false),
        }));
        Assert.False(BrowserProxyActivation.RequiresRemoteBrowserEndpoint(new GatewayRecord
        {
            Id = "gw-local",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
        }));
    }
}
