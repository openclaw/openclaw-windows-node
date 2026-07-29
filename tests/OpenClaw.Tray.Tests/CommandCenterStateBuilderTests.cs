using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class CommandCenterStateBuilderTests
{
    [Fact]
    public void BrowserProxyAuthWarning_ShowsWhenToggleOnAndSharedTokenMissing()
    {
        Assert.True(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: true));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: false,
            activeGatewayHasSharedToken: false));
    }
}
