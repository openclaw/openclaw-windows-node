using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class NativeGatewayRecordTests
{
    private static GatewayRecord NativeRecord() => new GatewayRecordBuilder()
        .WithId("gw-native").WithUrl("ws://127.0.0.1:18789").Local().RequiresV2Signature().Build()
        with { NativePackageFamilyName = "OpenClaw.Gateway_123456789abcd" };

    [Fact]
    public void Marker_RoundTripsWithoutWslOwnership()
    {
        var record = NativeRecord();
        var restored = JsonSerializer.Deserialize<GatewayRecord>(JsonSerializer.Serialize(record));
        Assert.Equal(record, restored);
        Assert.Null(restored!.SetupManagedDistroName);
        Assert.False(GatewayRecordEditing.IsSetupManagedLocalRecord(restored));
    }

    [Theory]
    [InlineData("ws://127.0.0.1:18789")]
    [InlineData("ws://localhost:18789")]
    [InlineData("ws://[::1]:18789")]
    public void EquivalentEndpointEdit_PreservesNativeOwnership(string url)
    {
        var existing = NativeRecord();
        var rebuilt = new GatewayRecord { Id = existing.Id, Url = url, FriendlyName = "New label", SharedGatewayToken = "new-token" };
        var result = rebuilt.PreserveAdvancedFields(existing);
        Assert.Equal(existing.NativePackageFamilyName, result.NativePackageFamilyName);
        Assert.True(result.IsLocal);
        Assert.True(result.RequiresV2Signature);
        Assert.Equal("New label", result.FriendlyName);
        Assert.Equal("new-token", result.SharedGatewayToken);
        Assert.Null(result.SetupManagedDistroName);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:18790")]
    [InlineData("wss://127.0.0.1:18789")]
    [InlineData("ws://remote.example:18789")]
    public void Repointing_ClearsNativeOwnership(string url)
    {
        var record = NativeRecord();
        var result = (record with { Url = url }).PreserveAdvancedFields(record);
        Assert.Null(result.NativePackageFamilyName);
        Assert.False(result.RequiresV2Signature);
        Assert.Null(result.SetupManagedDistroName);
    }

    [Fact]
    public void AddingTunnel_ClearsNativeOwnership()
    {
        var record = NativeRecord();
        var result = (record with { SshTunnel = new("user", "host", 18789, 18789) }).PreserveAdvancedFields(record);
        Assert.Null(result.NativePackageFamilyName);
        Assert.False(result.RequiresV2Signature);
    }

    [Fact]
    public void LegacyLookingLabel_NeverMakesNativeGatewayWslOwned()
    {
        var record = NativeRecord() with { FriendlyName = "Local (OpenClawGateway)" };
        Assert.Null(GatewayRecordEditing.ResolveManagedDistroName(record));
        Assert.False(GatewayRecordEditing.IsSetupManagedLocalRecord(record));
    }
}
