using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.Connection.Tests;

public sealed class LocalGatewaySettingsTests
{
    [Fact]
    public void DiagnosticsUsesSelectedNativeEndpointInsteadOfStaleWslSettingsOrSnapshot()
    {
        var record = Native() with { Url = "ws://localhost:19001" };
        var stale = new GatewayConnectionSnapshot { GatewayId = "wsl", GatewayUrl = "ws://localhost:18789" };
        Assert.Equal(record.Url, LocalGatewaySettings.DisplayedGatewayUrl(record, stale, stale.GatewayUrl));
        Assert.Equal(record.Url, LocalGatewaySettings.DisplayedGatewayUrl(record, null, stale.GatewayUrl));
        Assert.Equal(record.Url, LocalGatewaySettings.DisplayedGatewayUrl(record,
            stale with { GatewayId = record.Id, GatewayUrl = record.Url }, stale.GatewayUrl));
    }

    internal static GatewayRecord Native() => new()
    {
        Id = "native", Url = "ws://127.0.0.1:18789", IsLocal = true,
        NativePackageFamilyName = "OpenClaw.Gateway_123456789abcd",
        NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract,
    };

    [Theory]
    [InlineData(null, LocalGatewayKind.LegacyNative)]
    [InlineData(NativeGatewayPackageClient.IsolatedContract, LocalGatewayKind.Native)]
    [InlineData("unknown", LocalGatewayKind.None)]
    public void NativeContractSelectsOnlyItsOwnSettings(string? contract, LocalGatewayKind expected) =>
        Assert.Equal(expected, LocalGatewaySettings.Classify(Native() with { NativeRuntimeContract = contract }));

    [Fact]
    public void MixedOrInvalidNativeOwnershipCannotFallBackToWsl()
    {
        Assert.Equal(LocalGatewayKind.None, LocalGatewaySettings.Classify(Native() with { SetupManagedDistroName = "OpenClawGateway" }));
        Assert.Equal(LocalGatewayKind.None, LocalGatewaySettings.Classify(Native() with { Url = "wss://remote.test" }));
        Assert.Equal(LocalGatewayKind.None, LocalGatewaySettings.Classify(Native() with { NativePackageFamilyName = "untrusted" }));
    }

    [Fact]
    public void LocalAddressAloneDoesNotAuthorizeRemoval()
    {
        Assert.Equal(LocalGatewayKind.None, LocalGatewaySettings.Classify(null));
        Assert.Equal(LocalGatewayKind.None, LocalGatewaySettings.Classify(new() { IsLocal = true, Url = "ws://localhost:18789" }));
        Assert.Equal(LocalGatewayKind.None, LocalGatewaySettings.Classify(new() { Url = "wss://remote.test" }));
    }

    [Theory]
    [InlineData("ws://127.0.0.1:18789")]
    [InlineData("wss://gateway.tail123.ts.net")]
    public void ManagedWslKeepsItsOwnRoute(string url) =>
        Assert.Equal(LocalGatewayKind.Wsl, LocalGatewaySettings.Classify(new()
        {
            Id = "wsl", IsLocal = true, Url = url, SetupManagedDistroName = "CustomGateway",
        }));

    [Fact]
    public void LegacyWslNameRemainsSupportedButNativeNameNeverConfersWslOwnership()
    {
        Assert.Equal(LocalGatewayKind.Wsl, LocalGatewaySettings.Classify(new()
        {
            IsLocal = true, Url = "ws://localhost:18789", FriendlyName = "Local (OpenClawGateway)",
        }));
        Assert.Equal(LocalGatewayKind.Native, LocalGatewaySettings.Classify(
            Native() with { FriendlyName = "Local (OpenClawGateway)" }));
    }

    [Fact]
    public void ConfirmationToleratesTelemetryButNotChangedAuthority()
    {
        var record = Native();
        Assert.True(LocalGatewaySettings.IsSameTarget(record, record with { LastConnected = DateTime.UtcNow }));
        Assert.False(LocalGatewaySettings.IsSameTarget(record, record with { Url = "ws://localhost:19000" }));
        Assert.False(LocalGatewaySettings.IsSameTarget(record, record with { SharedGatewayToken = "changed" }));
        Assert.False(LocalGatewaySettings.IsSameTarget(record, record with { NativeRuntimeContract = null }));
        Assert.False(LocalGatewaySettings.IsSameTarget(record, null));
    }
}
