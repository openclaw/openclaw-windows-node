namespace OpenClawTray.Services;

internal static class CommandCenterBrowserProxyAuthWarningPolicy
{
    internal static bool ShouldShow(
        bool nodeBrowserProxyEnabled,
        bool activeGatewayHasSharedToken)
        => BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled,
            activeGatewayHasSharedToken);
}
