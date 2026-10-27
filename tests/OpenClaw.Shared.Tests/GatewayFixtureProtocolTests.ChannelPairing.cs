using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task ChannelPairingFixture_IsOptInAndReconcilesBothAccountScopedMutations()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChannelPairing(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var gateway = connected.Client.CreateChannelPairingGateway();
        var epoch = gateway.ConnectionEpoch!.Value;
        var snapshot = await gateway.ListAsync(epoch);
        Assert.Equal(2, snapshot.Requests.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "channels.pairing.dismiss", new { channel = "telegram", accountId = "personal", requestId = "dismiss" }));
        Assert.Equal(2, (await gateway.ListAsync(epoch)).Requests.Count);
        await gateway.ResolveAsync(snapshot.Requests.Single(row => row.RequestId == "approve"), true, epoch);
        var remaining = Assert.Single((await gateway.ListAsync(epoch)).Requests);
        Assert.Equal("dismiss", remaining.RequestId);
        await gateway.ResolveAsync(remaining, false, epoch);
        Assert.Empty((await gateway.ListAsync(epoch)).Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "config.patch", new { raw = "{}", baseHash = "synthetic" }));

        await using var browse = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var browseClient = await ConnectedClient.OpenAsync(browse, token);
        var readOnly = browseClient.Client.CreateChannelPairingGateway();
        await Assert.ThrowsAsync<NotSupportedException>(() => readOnly.ListAsync(readOnly.ConnectionEpoch!.Value));
    }
}
