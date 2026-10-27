using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    [Theory]
    [InlineData("channels.status", false)]
    [InlineData("channels.status", true)]
    [InlineData("channels.start", false)]
    [InlineData("channels.start", true)]
    [InlineData("channels.stop", false)]
    [InlineData("channels.stop", true)]
    [InlineData("channels.logout", false)]
    [InlineData("channels.logout", true)]
    public async Task ExistingChannels_RealClient_WireAndDeniedAcknowledgementAudit(string method, bool denied)
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            auth = new { scopes = new[] { "operator.read", "operator.admin" } },
            features = new { methods = new[] { method } }
        });
        server.OnMethod(method, _ => denied ? LoopbackResponse.Fail("synthetic permission denial") : method switch
        {
            "channels.status" => JsonSerializer.Deserialize<JsonElement>(
                """{"ts":1792771200000,"channelOrder":["telegram"],"channelLabels":{"telegram":"Telegram"},"channels":{"telegram":{"configured":true,"running":true}}}"""),
            "channels.start" => new { channel = "telegram", accountId = "work", started = true, outcome = "started" },
            "channels.stop" => new { channel = "telegram", accountId = "work", stopped = true },
            _ => new { channel = "telegram", accountId = "work", cleared = true, loggedOut = true }
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "synthetic-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        switch (method)
        {
            case "channels.status":
                var snapshot = await client.GetChannelsStatusAsync(probe: true, timeoutMs: 12000);
                Assert.Equal(denied, snapshot is null);
                if (!denied) Assert.Equal("telegram", Assert.Single(snapshot!.ChannelOrder));
                break;
            case "channels.start":
                var started = await client.StartChannelDetailedAsync("telegram");
                Assert.Equal(!denied, started!.Ok);
                Assert.Equal(!denied, started.Started);
                if (!denied) Assert.Equal("work", started.AccountId);
                break;
            case "channels.stop":
                Assert.Equal(!denied, await client.StopChannelAsync("telegram"));
                break;
            default:
                Assert.Equal(!denied, await client.LogoutChannelAsync("telegram"));
                break;
        }
        using var frame = JsonDocument.Parse(server.FrameFor(method));
        var parameters = frame.RootElement.GetProperty("params");
        if (method == "channels.status")
        {
            Assert.True(parameters.GetProperty("probe").GetBoolean());
            Assert.Equal(10000, parameters.GetProperty("timeoutMs").GetInt32());
            Assert.Equal(2, parameters.EnumerateObject().Count());
        }
        else
        {
            Assert.Equal("telegram", parameters.GetProperty("channel").GetString());
            Assert.Single(parameters.EnumerateObject());
            // This audit records current default-account behavior, not explicit account selection support.
            Assert.False(parameters.TryGetProperty("accountId", out _));
        }
        _output.WriteLine($"{method}: authenticated synthetic socket, exact current payload and {(denied ? "denied" : "successful")} acknowledgement exercised. Default-account/stale-dialog gaps remain separate.");
        await client.DisconnectAsync();
    }
}
