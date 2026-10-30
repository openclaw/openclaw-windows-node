using OpenClaw.Connection;

namespace OpenClaw.SetupEngine.Tests;

public sealed class ExistingWslGatewaySetupTests
{
    private static GatewayRecord Wsl(string url = "ws://localhost:19123") => new()
    {
        Id = "custom-b", IsLocal = true, SetupManagedDistroName = "GatewayB", Url = url,
    };

    [Theory]
    [InlineData("ws://localhost:19123", 19123)]
    [InlineData("wss://b.tail123.ts.net", 18789)]
    public void ReopenedOnboardingPinsCustomDistroAndDoesNotTreatHttpsPortAsServicePort(string url, int port)
    {
        var owner = new ExistingWslGatewaySetup(Wsl(url));
        var config = new SetupConfig { DistroName = "DefaultGatewayA", GatewayPort = 18789 };
        owner.Apply(config);
        Assert.Equal("GatewayB", config.DistroName);
        Assert.Equal(url, config.GatewayUrl);
        Assert.Equal(port, config.GatewayPort);
        owner.RequireCurrent(Wsl(url) with { LastConnected = DateTime.UtcNow });
        Assert.Throws<InvalidOperationException>(() => owner.RequireCurrent(Wsl(url) with { Id = "gateway-a" }));
        Assert.Throws<InvalidOperationException>(() => owner.RequireCurrent(Wsl(url) with { SetupManagedDistroName = "GatewayA" }));
        var localAiReview = LocalAiRecoveryConfigurationBaseline.Capture(config);
        owner.Apply(config);
        localAiReview.Restore(config);
        owner.Restore(config);
        Assert.Equal("DefaultGatewayA", config.DistroName);
        Assert.Null(config.GatewayUrl);
        Assert.Equal(18789, config.GatewayPort);
    }

    [Fact]
    public void WslReplacementNeverSelectsNativeOrAnotherDistro()
    {
        var native = new GatewayRecord
        {
            Id = "native", IsLocal = true, Url = "ws://localhost:19001",
            NativePackageFamilyName = "OpenClaw.Gateway_123456789abcd",
        };
        var other = Wsl() with { Id = "other-wsl", SetupManagedDistroName = "GatewayA" };
        Assert.Null(ExistingConfigDetector.SelectWslReplacement([native, other], "GatewayB", null));
        Assert.Equal("custom-b", ExistingConfigDetector.SelectWslReplacement([native, other, Wsl()], "GatewayB", null)!.Id);
        Assert.Null(ExistingConfigDetector.SelectWslReplacement([native, other, Wsl()], "GatewayB", native.Id));
    }
}
