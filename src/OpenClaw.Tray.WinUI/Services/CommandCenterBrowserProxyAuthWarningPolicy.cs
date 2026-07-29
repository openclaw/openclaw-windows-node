namespace OpenClawTray.Services;

internal static class CommandCenterBrowserProxyAuthWarningPolicy
{
    internal static bool ShouldShow(
        bool nodeBrowserProxyEnabled,
        bool activeGatewayHasSharedToken,
        bool hasGatewayClient)
        => BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled,
            activeGatewayHasSharedToken,
            hasGatewayClient);
}
