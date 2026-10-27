using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    private static readonly string[] ChannelPairingMethods =
        ["channels.pairing.list", "channels.pairing.approve", "channels.pairing.dismiss"];

    private static void ConfigureChannelPairing(LoopbackGatewayServer server, bool advertise = true, bool pairingScope = true)
    {
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            features = new { methods = advertise ? ChannelPairingMethods : [] },
            auth = new { scopes = pairingScope ? new[] { "operator.read", "operator.pairing" } : ["operator.read"] }
        });
        server.OnMethod("channels.pairing.list", _ => JsonSerializer.Deserialize<JsonElement>(ChannelPairingTests.PendingJson));
        server.OnMethod("channels.pairing.approve", _ => new
        {
            requestId = "req-1", senderId = "sender-7",
            notification = "not-requested", commandOwnerBootstrap = "not-requested"
        });
        server.OnMethod("channels.pairing.dismiss", _ => new { requestId = "req-1", senderId = "sender-7" });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChannelPairing_RealClient_AuthenticatesAndSendsOnlyAccountScopedObservedRequest(bool approve)
    {
        using var server = new LoopbackGatewayServer();
        ConfigureChannelPairing(server);
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "synthetic-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var session = new ChannelPairingSession(client.CreateChannelPairingGateway(), () => true);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        await session.ResolveAsync(row, approve);

        using var connect = JsonDocument.Parse(server.FrameFor("connect"));
        Assert.Equal("operator", connect.RootElement.GetProperty("params").GetProperty("role").GetString());
        Assert.Equal("synthetic-token", connect.RootElement.GetProperty("params").GetProperty("auth").GetProperty("token").GetString());
        Assert.Contains("operator.pairing", client.GrantedOperatorScopes);
        using var list = JsonDocument.Parse(server.FrameFor("channels.pairing.list"));
        Assert.Empty(list.RootElement.GetProperty("params").EnumerateObject());
        using var mutation = JsonDocument.Parse(server.FrameFor(approve ? "channels.pairing.approve" : "channels.pairing.dismiss"));
        var parameters = mutation.RootElement.GetProperty("params");
        Assert.Equal(3, parameters.EnumerateObject().Count());
        Assert.Equal("telegram", parameters.GetProperty("channel").GetString());
        Assert.Equal("work", parameters.GetProperty("accountId").GetString());
        Assert.Equal("req-1", parameters.GetProperty("requestId").GetString());
        Assert.NotEqual(list.RootElement.GetProperty("id").GetString(), mutation.RootElement.GetProperty("id").GetString());
        Assert.Empty(session.Snapshot.Requests);
        _output.WriteLine($"Authenticated synthetic loopback: channels.pairing.list {{}} then {(approve ? "approve" : "dismiss")} {{channel:telegram,accountId:work,requestId:req-1}}; terminal identity matched.");
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ChannelPairing_RealClient_UnsupportedOrMissingScope_NeverDispatches(bool advertise, bool pairingScope)
    {
        using var server = new LoopbackGatewayServer();
        ConfigureChannelPairing(server, advertise, pairingScope);
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var gateway = client.CreateChannelPairingGateway();
        var failure = await Record.ExceptionAsync(() => gateway.ListAsync(gateway.ConnectionEpoch!.Value));
        if (!advertise) Assert.IsType<NotSupportedException>(failure);
        else Assert.IsType<UnauthorizedAccessException>(failure);
        Assert.False(server.HasFrame("channels.pairing.list"));
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("wrong-request")]
    [InlineData("wrong-sender")]
    [InlineData("malformed")]
    [InlineData("timeout")]
    public async Task ChannelPairing_RealClient_UnconfirmedResolveRetainsRowAndRequiresFreshRead(string outcome)
    {
        using var server = new LoopbackGatewayServer();
        ConfigureChannelPairing(server);
        server.OnMethod("channels.pairing.approve", _ => outcome switch
        {
            "denied" => LoopbackResponse.Fail("missing scope: operator.pairing"),
            "wrong-request" => new { requestId = "other", senderId = "sender-7", notification = "not-requested", commandOwnerBootstrap = "not-requested" },
            "wrong-sender" => new { requestId = "req-1", senderId = "other", notification = "not-requested", commandOwnerBootstrap = "not-requested" },
            _ => new { }
        });
        if (outcome == "timeout") server.SilenceMethod("channels.pairing.approve");
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var session = new ChannelPairingSession(
            client.CreateChannelPairingGateway(timeoutMs: outcome == "timeout" ? 300 : 15000), () => true);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        var failure = await Record.ExceptionAsync(() => session.ResolveAsync(row, true));
        if (outcome == "timeout") Assert.IsType<TimeoutException>(failure);
        else if (outcome == "denied")
            Assert.Contains("missing scope: operator.pairing", Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        else Assert.IsType<InvalidDataException>(failure);
        Assert.Same(row, Assert.Single(session.Snapshot.Requests));
        Assert.True(session.RequiresRefresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResolveAsync(row, true));
        Assert.Single(server.AllFrames, frame => frame.Contains("\"method\":\"channels.pairing.approve\"", StringComparison.Ordinal));
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ChannelPairing_RealClient_AcceptedIsNotTerminal_AndFinalFailureKeepsPending(bool approve, bool success)
    {
        using var server = new LoopbackGatewayServer();
        ConfigureChannelPairing(server);
        var method = approve ? "channels.pairing.approve" : "channels.pairing.dismiss";
        server.SilenceMethod(method);
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var session = new ChannelPairingSession(client.CreateChannelPairingGateway(), () => true);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        var mutation = session.ResolveAsync(row, approve);
        using var frame = JsonDocument.Parse(await server.WaitFrameAsync(method, 0, 10000));
        var id = frame.RootElement.GetProperty("id").GetString();
        await server.SendTextToCurrentSocketAsync(JsonSerializer.Serialize(new { type = "res", id, ok = true, payload = new { status = "accepted" } }));
        // An independent response behind the admission frame fences receive processing without a timing sleep.
        await client.CreateChannelPairingGateway().ListAsync(client.SessionMutationConnectionEpoch!.Value);
        Assert.False(mutation.IsCompleted);
        Assert.True(session.Busy);
        if (success)
        {
            object payload = approve
                ? new { requestId = "req-1", senderId = "sender-7", notification = "not-requested", commandOwnerBootstrap = "not-requested" }
                : new { requestId = "req-1", senderId = "sender-7" };
            await server.SendTextToCurrentSocketAsync(JsonSerializer.Serialize(new { type = "res", id, ok = true, payload }));
            await mutation;
            Assert.Empty(session.Snapshot.Requests);
        }
        else
        {
            await server.SendTextToCurrentSocketAsync(JsonSerializer.Serialize(new { type = "res", id, ok = false, error = new { code = "UNAVAILABLE", message = "synthetic terminal failure" } }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mutation);
            Assert.Same(row, Assert.Single(session.Snapshot.Requests));
        }
        Assert.True(session.RequiresRefresh);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task ChannelPairing_RealClient_ReconnectInvalidatesOldEpochBeforeWire()
    {
        using var server = new LoopbackGatewayServer();
        ConfigureChannelPairing(server);
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var gateway = client.CreateChannelPairingGateway();
        var epoch = gateway.ConnectionEpoch!.Value;
        var row = Assert.Single((await gateway.ListAsync(epoch)).Requests);
        await client.DisconnectAsync();
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        Assert.NotEqual(epoch, gateway.ConnectionEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ResolveAsync(row, true, epoch));
        Assert.False(server.HasFrame("channels.pairing.approve"));
        await client.DisconnectAsync();
    }
}
