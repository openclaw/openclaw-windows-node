using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class CommandCenterStateBuilderTests
{
    [Fact]
    public void BrowserProxyAuthWarning_IsGatedByActiveGatewaySharedToken()
    {
        var nodes = new[]
        {
            new NodeCapabilityHealthInfo
            {
                BrowserApprovedCommands = ["browser.proxy"]
            }
        };

        Assert.True(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            nodes));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: true,
            nodes));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: false,
            activeGatewayHasSharedToken: false,
            nodes));
        Assert.False(CommandCenterBrowserProxyAuthWarningPolicy.ShouldShow(
            nodeBrowserProxyEnabled: true,
            activeGatewayHasSharedToken: false,
            []));
    }
}
