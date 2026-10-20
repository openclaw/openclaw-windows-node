using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

[Trait("Category", "Integration")]
public sealed class SessionRosterWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySessionSummary_DoesNotRemoveSessionsButAuthoritativeEmptyRosterDoes(bool dictionary)
    {
        using var temp = new TempDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new LoopbackWebSocketServer(useManagedWebSocket: true);
        await server.StartAsync();
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "fixture-shared-token", identityPath: temp.Path,
            logger: NullLogger.Instance);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => connected.TrySetResult();
        var updates = Channel.CreateUnbounded<SessionInfo[]>();
        client.SessionsUpdated += (_, sessions) => updates.Writer.TryWrite(sessions);

        await client.ConnectAsync().WaitAsync(deadline.Token);
        await server.SendTextAsync(
            """{"type":"event","event":"connect.challenge","payload":{"nonce":"roster-wire-nonce"}}""",
            deadline.Token);
        using var connect = JsonDocument.Parse(await server.ReceiveTextAsync(deadline.Token));
        Assert.Equal("connect", connect.RootElement.GetProperty("method").GetString());
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res",
            id = connect.RootElement.GetProperty("id").GetString(),
            ok = true,
            payload = new { type = "hello-ok", protocol = 4 }
        }), deadline.Token);
        await connected.Task.WaitAsync(deadline.Token);

        object roster = dictionary
            ? new Dictionary<string, object>
            {
                ["agent:coordinator:dashboard:proof"] = new { status = "idle", agentId = "coordinator" }
            }
            : new[] { new { key = "agent:coordinator:dashboard:proof", status = "idle", agentId = "coordinator" } };
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = "legacy-roster", ok = true, payload = new { sessions = roster }
        }), deadline.Token);
        Assert.Equal("agent:coordinator:dashboard:proof",
            Assert.Single(await updates.Reader.ReadAsync(deadline.Token)).Key);

        await server.SendTextAsync(
            """{"type":"res","id":"legacy-health","ok":true,"payload":{"sessions":{"path":"fixture-store","count":1,"recent":[]}}}""",
            deadline.Token);
        var barrier = client.SendWizardRequestAsync("fixture.barrier", new { }, 5000);
        while (true)
        {
            using var request = JsonDocument.Parse(await server.ReceiveTextAsync(deadline.Token));
            if (request.RootElement.GetProperty("method").GetString() != "fixture.barrier")
                continue;
            await server.SendTextAsync(JsonSerializer.Serialize(new
            {
                type = "res",
                id = request.RootElement.GetProperty("id").GetString(),
                ok = true,
                payload = new { passed = true }
            }), deadline.Token);
            break;
        }
        await barrier.WaitAsync(deadline.Token);
        Assert.Equal("agent:coordinator:dashboard:proof", Assert.Single(client.GetSessionList()).Key);
        Assert.False(updates.Reader.TryRead(out _));

        object empty = dictionary ? new Dictionary<string, object>() : Array.Empty<object>();
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = "legacy-empty-roster", ok = true, payload = new { sessions = empty }
        }), deadline.Token);
        Assert.Empty(await updates.Reader.ReadAsync(deadline.Token));
        Assert.Empty(client.GetSessionList());
    }
}
