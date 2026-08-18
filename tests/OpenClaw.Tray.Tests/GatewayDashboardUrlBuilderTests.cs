using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardUrlBuilderTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void HasBrowserCompatibleCredential_RequiresTailscaleOrSharedToken(
        bool trustTailscaleAuth,
        bool usesSharedGatewayToken,
        bool expected)
    {
        Assert.Equal(
            expected,
            GatewayDashboardUrlBuilder.HasBrowserCompatibleCredential(
                trustTailscaleAuth,
                usesSharedGatewayToken));
    }

    [Fact]
    public void Build_AppendsSharedTokenToDashboardRoot()
    {
        var url = GatewayDashboardUrlBuilder.Build("ws://localhost:4317", null, "shared token", appendSharedGatewayToken: true);

        Assert.Equal("http://localhost:4317#token=shared%20token", url);
    }

    [Fact]
    public void Build_AppendsSharedTokenToDashboardPath()
    {
        var url = GatewayDashboardUrlBuilder.Build("wss://gateway.example/", "/sessions/abc", "shared", appendSharedGatewayToken: true);

        Assert.Equal("https://gateway.example/sessions/abc#token=shared", url);
    }

    [Fact]
    public void Build_DoesNotAppendNonSharedToken()
    {
        var url = GatewayDashboardUrlBuilder.Build("ws://localhost:4317", "config", "device-token", appendSharedGatewayToken: false);

        Assert.Equal("http://localhost:4317/config", url);
    }
}
