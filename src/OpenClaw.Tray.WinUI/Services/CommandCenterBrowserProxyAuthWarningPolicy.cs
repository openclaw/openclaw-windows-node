namespace OpenClawTray.Services;

internal static class CommandCenterBrowserProxyAuthWarningPolicy
{
    internal static bool ShouldShow(
        bool nodeBrowserProxyEnabled,
        bool activeGatewayHasSharedToken,
        bool nodeSessionLive)
        => BrowserProxyActivation.ShouldShowMissingSharedTokenWarning(
            nodeBrowserProxyEnabled,
            activeGatewayHasSharedToken,
            nodeSessionLive);
}
