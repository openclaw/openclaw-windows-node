using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;

namespace OpenClawTray.Services;

internal static class StartupSetupState
{
    public static bool HasStoredNodeDeviceToken(string dataPath) =>
        OnboardingExistingConfigGuard.HasAnyDeviceTokenForRole(dataPath, "node");

    /// <summary>
    /// True if the user has an operator device token (root or any per-gateway dir)
    /// AND a configured gateway target (non-default <c>GatewayUrl</c> or an SSH tunnel
    /// host). Both signals together indicate a working operator config — guards
    /// against orphan tokens and against tokens-without-target stale state.
    /// Token detection delegates to
    /// <see cref="OnboardingExistingConfigGuard.HasAnyOperatorDeviceToken"/> so the
    /// startup decision and the in-wizard guard agree on what counts as paired.
    /// </summary>
    public static bool HasUsableOperatorConfiguration(SettingsManager settings, string dataPath) =>
        OnboardingExistingConfigGuard.HasAnyOperatorDeviceToken(dataPath)
        && HasAnyConfiguredGatewayTarget(settings);

    /// <summary>
    /// True when the user has configured an actual gateway target — either a
    /// non-default <c>GatewayUrl</c> or an SSH tunnel host (which routes via
    /// <c>ws://127.0.0.1:LocalPort</c> and would otherwise look "default").
    /// </summary>
    internal static bool HasAnyConfiguredGatewayTarget(SettingsManager settings)
    {
        if (settings.UseSshTunnel && !string.IsNullOrWhiteSpace(settings.SshTunnelHost))
        {
            return true;
        }

        return HasNonDefaultGatewayUrl(settings);
    }

    private static bool HasNonDefaultGatewayUrl(SettingsManager settings) =>
        !string.IsNullOrWhiteSpace(settings.GatewayUrl)
        && !string.Equals(
            settings.GatewayUrl,
            OnboardingExistingConfigGuard.DefaultGatewayUrl,
            StringComparison.OrdinalIgnoreCase);

    public static bool CanStartNodeGateway(SettingsManager settings, string dataPath)
    {
        if (!settings.EnableNodeMode)
        {
            return false;
        }

        return HasStoredNodeDeviceToken(dataPath);
    }

    public static bool RequiresSetup(SettingsManager settings, string dataPath)
    {
        // MCP-only mode doesn't require an authenticated gateway. Checked first
        // so that an MCP-server user with EnableNodeMode accidentally left on
        // (but no node token) still bypasses the wizard — preserves the
        // original "MCP wins" precedence.
        if (settings.EnableMcpServer)
        {
            return false;
        }

        // Node mode: needs a paired node device token to operate as a node.
        if (settings.EnableNodeMode)
        {
            return !HasStoredNodeDeviceToken(dataPath);
        }

        // Operator mode: returning users with any operator device token AND a
        // configured gateway target already have a working configuration —
        // don't auto-launch the wizard (Scott Hanselman repro: remote-gateway
        // operator saw the wizard pop on every launch).
        if (HasUsableOperatorConfiguration(settings, dataPath))
        {
            return false;
        }

        return true;
    }
}
