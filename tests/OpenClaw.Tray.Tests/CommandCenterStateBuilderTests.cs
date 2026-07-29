using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class CommandCenterStateBuilderTests
{
    [Fact]
    public void BrowserProxyAuthWarning_ShowsOnlyWhenClientAttachedAndSharedTokenMissing()
    {
        Assert.True(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            hasGatewayClient: true));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            hasGatewayClient: false));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: true,
            hasGatewayClient: true));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: false,
            activeGatewayHasSharedToken: false,
            hasGatewayClient: true));
    }
}
