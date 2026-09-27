using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardUrlBuilderTests
{
    [Theory]
    [InlineData("wss://gateway.example/mount/", "https://gateway.example/mount/custodian?onboarding=1#token=synthetic")]
    [InlineData("wss://gateway.example/mount/#token=synthetic", "https://gateway.example/mount/custodian?onboarding=1#token=synthetic")]
    public void Build_CustodianPreservesMountAndAuthFragment(string gateway, string expected)
    {
        Assert.Equal(expected, GatewayDashboardUrlBuilder.Build(
            gateway, "custodian?onboarding=1", gateway.Contains('#') ? null : "synthetic", true));
    }

    [Fact]
    public void Build_RotatedSharedTokenReplacesOldFragmentTokenWithoutQueryExport()
    {
        Assert.Equal("https://gateway.example/mount/custodian?onboarding=1#view=compact&token=current",
            GatewayDashboardUrlBuilder.Build("wss://gateway.example/mount/#token=old&view=compact",
                "custodian?onboarding=1", "current", true));
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
