namespace OpenClawTray.Services;

/// <summary>
/// Shared browser-proxy activation decisions for capability registration,
/// Command Center warnings, and Connection capability pills.
///
/// Browser.proxy authenticates to the local HTTP browser-control host with the
/// saved gateway shared token. Setup-code / QR pairing can connect the node with
/// only a device token and leave that shared token absent. In that state the
/// companion must not silently look "pending approval"; it must explain the
/// missing token.
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
    /// Warn only for the registration block that is specifically a missing
    /// shared token. Do not wait for browser.proxy to already be declared: the
    /// missing token is exactly what prevents declaration. A disconnected or
    /// unselected gateway (<see cref="RegistrationBlock.NoGatewayClient"/>)
    /// must not tell the operator to paste a token.
    /// </summary>
    internal static bool ShouldShowMissingSharedTokenWarning(
        bool nodeBrowserProxyEnabled,
        bool activeGatewayHasSharedToken,
        bool hasGatewayClient)
        => ResolveRegistrationBlock(
               toggleEnabled: nodeBrowserProxyEnabled,
               sharedGatewayToken: activeGatewayHasSharedToken ? "present" : null,
               hasGatewayClient) == RegistrationBlock.MissingSharedGatewayToken;

    internal static CapabilityPillKind ResolveCapabilityPillKind(
        bool toggleEnabled,
        bool effective,
        bool pendingDeclared,
        bool hasSharedGatewayToken,
        bool hasGatewayClient)
    {
        if (effective)
            return CapabilityPillKind.Active;

        if (!toggleEnabled && !pendingDeclared)
            return CapabilityPillKind.Off;

        // Mirror RegistrationBlock: NeedsSharedToken only when a node client is
        // attached and the shared token is what blocks browser.proxy.
        if (toggleEnabled &&
            ResolveRegistrationBlock(toggleEnabled, hasSharedGatewayToken ? "present" : null, hasGatewayClient) ==
            RegistrationBlock.MissingSharedGatewayToken)
            return CapabilityPillKind.NeedsSharedToken;

        if (pendingDeclared || toggleEnabled)
            return CapabilityPillKind.PendingApproval;

        return CapabilityPillKind.Off;
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
