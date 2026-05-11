using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class LocalGatewayApproverTests
{
    #region IsLocalGateway — positive cases

    [Fact]
    public void IsLocalGateway_Localhost_ReturnsTrue()
    {
        Assert.True(LocalGatewayApprover.IsLocalGateway("ws://localhost:18789"));
    }

    [Fact]
    public void IsLocalGateway_127001_ReturnsTrue()
    {
        Assert.True(LocalGatewayApprover.IsLocalGateway("ws://127.0.0.1:18789"));
    }

    [Fact]
    public void IsLocalGateway_IPv6Bracketed_ReturnsTrue()
    {
        Assert.True(LocalGatewayApprover.IsLocalGateway("ws://[::1]:18789"));
    }

    [Fact]
    public void IsLocalGateway_LocalhostUpperCase_ReturnsTrue()
    {
        Assert.True(LocalGatewayApprover.IsLocalGateway("ws://LOCALHOST:18789"));
    }

    [Fact]
    public void IsLocalGateway_LocalhostMixedCase_ReturnsTrue()
    {
        Assert.True(LocalGatewayApprover.IsLocalGateway("ws://LocalHost:18789"));
    }

    [Fact]
    public void IsLocalGateway_WssLocalhost_ReturnsTrue()
    {
        Assert.True(LocalGatewayApprover.IsLocalGateway("wss://localhost:443"));
    }

    #endregion

    #region IsLocalGateway — negative cases

    [Fact]
    public void IsLocalGateway_RemoteHost_ReturnsFalse()
    {
        Assert.False(LocalGatewayApprover.IsLocalGateway("ws://gateway.example.com:18789"));
    }

    [Fact]
    public void IsLocalGateway_PrivateIP_ReturnsFalse()
    {
        Assert.False(LocalGatewayApprover.IsLocalGateway("ws://192.168.1.1:18789"));
    }

    [Fact]
    public void IsLocalGateway_10Network_ReturnsFalse()
    {
        Assert.False(LocalGatewayApprover.IsLocalGateway("ws://10.0.0.1:18789"));
    }

    [Fact]
    public void IsLocalGateway_Empty_ReturnsFalse()
    {
        Assert.False(LocalGatewayApprover.IsLocalGateway(""));
    }

    [Fact]
    public void IsLocalGateway_Null_ReturnsFalse()
    {
        Assert.False(LocalGatewayApprover.IsLocalGateway(null!));
    }

    [Fact]
    public void IsLocalGateway_NotAUrl_ReturnsFalse()
    {
        Assert.False(LocalGatewayApprover.IsLocalGateway("not-a-url"));
    }

    [Fact]
    public void IsLocalGateway_Whitespace_ReturnsFalse()
    {
        Assert.False(LocalGatewayApprover.IsLocalGateway("   "));
    }

    #endregion

    [Theory]
    [InlineData("ws://localhost:18789")]
    [InlineData("ws://127.0.0.1:18789")]
    [InlineData("ws://[::1]:18789")]
    [InlineData("wss://localhost:18789")]
    [InlineData("ws://gateway.example.com:18789")]
    [InlineData("ws://10.0.0.5:18789")]
    [InlineData("not a url")]
    public void IsLocalGateway_ReturnsSharedClassifierResult(string gatewayUrl)
    {
        Assert.Equal(
            LocalGatewayUrlClassifier.IsLocalGatewayUrl(gatewayUrl),
            LocalGatewayApprover.IsLocalGateway(gatewayUrl));
    }
}
