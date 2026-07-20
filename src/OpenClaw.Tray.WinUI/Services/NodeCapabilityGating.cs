namespace OpenClawTray.Services;

/// <summary>
/// Pure predicates that decide which optional node capabilities should be
/// advertised based on the user's <see cref="SettingsManager"/> flags.
///
/// Extracted from <c>NodeService.RegisterCapabilities</c> so the gating
/// rules can be unit-tested without standing up the full tray host. Both
/// the gateway client path and the MCP-only path read from the same
/// authoritative capability list, so a regression here would silently drop
/// or leak a capability across both surfaces.
///
/// Defaults: capabilities default ON (a missing or null settings object
/// counts as enabled) except <c>tts.speak</c> and <c>stt.transcribe</c>,
/// which are privacy-sensitive and require an explicit opt-in.
///
/// <see cref="ShouldRegisterSystemRun"/> gates the <c>system.run</c> /
/// <c>system.run.prepare</c> commands inside <c>SystemCapability</c>, not
/// the whole <c>system</c> category - <c>system.notify</c>, <c>system.which</c>,
/// and the exec-approval read/write commands stay registered regardless.
/// </summary>
internal static class NodeCapabilityGating
{
    public static bool ShouldRegisterCanvas(SettingsManager? s)       => s?.NodeCanvasEnabled       != false;
    public static bool ShouldRegisterScreen(SettingsManager? s)       => s?.NodeScreenEnabled       != false;
    public static bool ShouldRegisterCamera(SettingsManager? s)       => s?.NodeCameraEnabled       != false;
    public static bool ShouldRegisterLocation(SettingsManager? s)     => s?.NodeLocationEnabled     != false;
    public static bool ShouldRegisterBrowserProxy(SettingsManager? s) => s?.NodeBrowserProxyEnabled != false;
    public static bool ShouldRegisterBrowserProxy(SettingsManager? s, string? sharedGatewayToken, bool hasGatewayClient) =>
        hasGatewayClient &&
        !string.IsNullOrWhiteSpace(sharedGatewayToken) &&
        ShouldRegisterBrowserProxy(s);
    public static bool ShouldRegisterTts(SettingsManager? s)          => s?.NodeTtsEnabled          == true;
    public static bool ShouldRegisterStt(SettingsManager? s)          => s?.NodeSttEnabled          == true;

    /// <summary>
    /// Resolve the local node's capability list from the gateway-reported
    /// <see cref="OpenClaw.Shared.GatewayNodeInfo"/> array - the single source
    /// of truth used by the tray menu, instances page, connection page, and
    /// permissions page.
    /// </summary>
    /// <param name="nodes">Current <see cref="AppState.Nodes"/> snapshot.</param>
    /// <param name="localDeviceId">The local node's full device id (from <c>App.NodeFullDeviceId</c>).</param>
    /// <returns>The capability list, or <c>null</c> when the node info is not yet available.</returns>
    public static System.Collections.Generic.IReadOnlyList<string>? GetLocalNodeCapabilities(
        OpenClaw.Shared.GatewayNodeInfo[]? nodes, string? localDeviceId)
        => GetLocalNodeInfo(nodes, localDeviceId)?.Capabilities?.ToArray();

    /// <summary>
    /// Resolve the complete local node record so visible surfaces can preserve
    /// the gateway's effective-versus-pending approval boundary.
    /// </summary>
    public static OpenClaw.Shared.GatewayNodeInfo? GetLocalNodeInfo(
        OpenClaw.Shared.GatewayNodeInfo[]? nodes, string? localDeviceId)
    {
        if (string.IsNullOrEmpty(localDeviceId) || nodes == null || nodes.Length == 0)
            return null;

        return System.Array.Find(nodes, n =>
            string.Equals(n.NodeId, localDeviceId, System.StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldRegisterSystemRun(SettingsManager? s)    => s?.NodeSystemRunEnabled    != false;

    /// <summary>Counts node capability categories served by local MCP without a gateway node client.</summary>
    public static int CountMcpServedCapabilities(SettingsManager? s)
    {
        int n = 2; // system + device are always registered
        if (ShouldRegisterCanvas(s)) n++;
        if (ShouldRegisterScreen(s)) n++;
        if (ShouldRegisterCamera(s)) n++;
        if (ShouldRegisterLocation(s)) n++;
        if (ShouldRegisterTts(s)) n++;
        if (ShouldRegisterStt(s)) n++;
        return n;
    }
}
