using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Shared browser-proxy activation decisions for capability registration,
/// Command Center warnings, Connection capability pills, and connection
/// diagnostics.
///
/// Browser.proxy authenticates to the local HTTP browser-control host with the
/// saved gateway shared token. Setup-code / QR pairing can connect the node with
/// only a device token and leave that shared token absent. In that state the
/// companion must not silently look "pending approval"; it must explain the
/// missing token.
///
/// Registration uses an attached node client (capability declare happens at
/// AttachClient). Operator-facing remediation (warnings, pills, diagnostics)
/// requires a live connected node session so attached-but-disconnected states
/// ask for reconnect instead of a token paste.
/// </summary>
internal static class BrowserProxyActivation
{
    internal enum RegistrationBlock
    {
        None,
        ToggleDisabled,
        NoGatewayClient,
        MissingSharedGatewayToken,
    }

    internal enum CapabilityPillKind
    {
        Off,
        Active,
        PendingApproval,
        NeedsSharedToken,
    }

    internal static RegistrationBlock ResolveRegistrationBlock(
        bool toggleEnabled,
        string? sharedGatewayToken,
        bool hasGatewayClient)
    {
        if (!toggleEnabled)
            return RegistrationBlock.ToggleDisabled;
        if (!hasGatewayClient)
            return RegistrationBlock.NoGatewayClient;
        if (string.IsNullOrWhiteSpace(sharedGatewayToken))
            return RegistrationBlock.MissingSharedGatewayToken;
        return RegistrationBlock.None;
    }

    internal static bool ShouldRegister(
        bool toggleEnabled,
        string? sharedGatewayToken,
        bool hasGatewayClient)
        => ResolveRegistrationBlock(toggleEnabled, sharedGatewayToken, hasGatewayClient) ==
           RegistrationBlock.None;

    /// <summary>
    /// True when missing-token remediation is honest: Browser control is on, the
    /// node WebSocket session is live, and the active gateway has no shared token.
    /// Attached-but-disconnected / no-client states must not claim a token paste
    /// will fix browser.proxy.
    /// </summary>
    internal static bool ShouldShowMissingSharedTokenWarning(
        bool nodeBrowserProxyEnabled,
        bool activeGatewayHasSharedToken,
        bool nodeSessionLive)
        => nodeBrowserProxyEnabled &&
           nodeSessionLive &&
           !activeGatewayHasSharedToken;

    /// <summary>
    /// Remote (non-loopback) gateways need more than a shared token for usable
    /// browser.proxy: an explicit browser-control port or an SSH browser-proxy
    /// forward. Local/loopback gateways use the gateway-port+2 default.
    /// SSH-tunneled gateways count as remote even when the effective URL is a
    /// local forward (127.0.0.1:localPort).
    /// </summary>
    internal static bool RequiresRemoteBrowserEndpoint(
        bool isLocalGateway,
        string? gatewayUrl,
        int? browserControlPort,
        bool hasSshTunnelConfigured,
        bool sshBrowserProxyForwardEnabled)
    {
        if (browserControlPort is >= 1 and <= 65535)
            return false;
        if (sshBrowserProxyForwardEnabled)
            return false;
        if (hasSshTunnelConfigured)
            return true;
        if (isLocalGateway)
            return false;
        if (GatewayRecordEditing.IsLoopbackEndpoint(gatewayUrl))
            return false;
        return true;
    }

    /// <summary>
    /// Topology inputs from an active <see cref="GatewayRecord"/> (Connection
    /// pills + <c>app.connection.*</c> diagnostics). Uses the same URL/SSH
    /// classification as the Command Center snapshot overload so pill tooltips
    /// and CC warnings cannot disagree when <see cref="GatewayRecord.IsLocal"/>
    /// drifts from the effective URL.
    /// </summary>
    internal static bool RequiresRemoteBrowserEndpoint(GatewayRecord? gateway)
    {
        if (gateway is null)
            return false;

        return RequiresRemoteBrowserEndpoint(
            gatewayUrl: gateway.Url,
            browserControlPort: gateway.BrowserControlPort,
            sshTunnel: gateway.SshTunnel);
    }

    /// <summary>
    /// Topology inputs from Command Center snapshot fields. A loopback URL
    /// still counts as remote when an SSH tunnel is configured without the
    /// browser-proxy forward.
    /// </summary>
    internal static bool RequiresRemoteBrowserEndpoint(
        string? gatewayUrl,
        int? browserControlPort,
        SshTunnelConfig? sshTunnel)
        => RequiresRemoteBrowserEndpoint(
            isLocalGateway: !string.IsNullOrWhiteSpace(gatewayUrl) &&
                            LocalGatewayUrlClassifier.IsLocalGatewayUrl(gatewayUrl) &&
                            sshTunnel is null,
            gatewayUrl: gatewayUrl,
            browserControlPort: browserControlPort,
            hasSshTunnelConfigured: sshTunnel is not null,
            sshBrowserProxyForwardEnabled: sshTunnel?.IncludeBrowserProxyForward == true);

    internal static bool IsNodeSessionLive(RoleConnectionState nodeState)
        => nodeState == RoleConnectionState.Connected;

    internal static string BuildMissingSharedTokenCaveat(bool requiresRemoteBrowserEndpoint)
    {
        const string core =
            "browser.proxy is not declared until this gateway has a saved shared token; " +
            "setup-code/QR pairing alone leaves that token absent. " +
            "Enter the gateway shared token in Settings, then reconnect.";
        if (!requiresRemoteBrowserEndpoint)
            return core;

        return core +
               " For a remote gateway, browser.proxy also needs an explicit browser-control port " +
               "or an SSH browser-proxy forward before the capability is usable.";
    }

    internal static string BuildMissingSharedTokenWarningDetail(bool requiresRemoteBrowserEndpoint)
    {
        const string core =
            "Browser control is enabled, but the active gateway has no saved shared token, " +
            "so this node will not declare the browser capability. Setup-code or QR pairing " +
            "can connect with a device token alone. Enter the gateway shared token in Settings, then reconnect.";
        if (!requiresRemoteBrowserEndpoint)
            return core;

        return core +
               " Remote gateways also need an explicit browser-control port or an SSH browser-proxy " +
               "forward; the shared token alone is not enough.";
    }

    internal static string BuildMissingSharedTokenCopyText(bool requiresRemoteBrowserEndpoint)
    {
        const string core =
            "Enter the gateway shared token in Settings > Gateway Token, save, and reconnect node mode. " +
            "Setup-code/QR bootstrap tokens are not the shared gateway token and will not activate browser.proxy. " +
            "Do not paste bootstrap tokens into the normal gateway token field.";
        if (!requiresRemoteBrowserEndpoint)
            return core;

        return core +
               " For a remote gateway, also set an explicit browser-control port on the gateway record " +
               "or enable an SSH browser-proxy forward so browser.proxy can reach the control host.";
    }

    internal static CapabilityPillKind ResolveCapabilityPillKind(
        bool toggleEnabled,
        bool effective,
        bool pendingDeclared,
        bool hasSharedGatewayToken,
        bool nodeSessionLive)
    {
        if (effective)
            return CapabilityPillKind.Active;

        if (!toggleEnabled && !pendingDeclared)
            return CapabilityPillKind.Off;

        // NeedsSharedToken only when a live node session exists and the shared
        // token is what blocks browser.proxy (not reconnect).
        if (toggleEnabled &&
            ShouldShowMissingSharedTokenWarning(
                nodeBrowserProxyEnabled: true,
                activeGatewayHasSharedToken: hasSharedGatewayToken,
                nodeSessionLive: nodeSessionLive))
            return CapabilityPillKind.NeedsSharedToken;

        if (pendingDeclared || toggleEnabled)
            return CapabilityPillKind.PendingApproval;

        return CapabilityPillKind.Off;
    }

    /// <summary>
    /// Connection pills keep a short localized label; the tooltip carries the
    /// same remediation detail as Command Center (including remote endpoint /
    /// SSH forward guidance when required).
    /// </summary>
    internal static string ResolveCapabilityPillTooltip(
        CapabilityPillKind kind,
        string localizedStateText,
        bool requiresRemoteBrowserEndpoint)
    {
        if (kind != CapabilityPillKind.NeedsSharedToken)
            return localizedStateText;

        return BuildMissingSharedTokenWarningDetail(requiresRemoteBrowserEndpoint);
    }

    internal static string DescribeRegistrationBlock(RegistrationBlock block) => block switch
    {
        RegistrationBlock.ToggleDisabled =>
            "browser proxy toggle is off",
        RegistrationBlock.NoGatewayClient =>
            "no gateway node client is attached",
        RegistrationBlock.MissingSharedGatewayToken =>
            "active gateway has no shared gateway token (setup-code/QR pairing alone is not enough for browser.proxy; enter the gateway shared token in Settings)",
        _ => "none",
    };
}
