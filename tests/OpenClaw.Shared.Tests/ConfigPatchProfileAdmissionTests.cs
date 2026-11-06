// Exercises the real Gateway client and wire-level profile event while config.patch is queued.
// Run CapturedProfileChangeWhilePatchQueued_IsRejectedBeforeConfigFrame in Shared.Tests.
using OpenClaw.TestSupport;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public partial class OpenClawGatewayClientTests
{
    private const int ConfigProfileDefaultDeadlineSeconds = 15;
    private static readonly TimeSpan ConfigProfileDeadline = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_ADMISSION_TEST_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? seconds : ConfigProfileDefaultDeadlineSeconds);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedProfileChangeWhilePatchQueued_IsRejectedBeforeConfigFrame(bool changeProfile)
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var identity = new TempDirectory("config-profile-admission-");
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var epoch = Assert.IsType<long>(client.SessionMutationConnectionEpoch);
        var captured = Assert.IsType<ConfigWriteAdmission>(client.ConfigWriteAdmission);
        var writes = new ConcurrentQueue<JsonElement>();
        using var responseLifetime = new CancellationTokenSource();
        var responder = RespondToConfigProfileAsync(server, writes, responseLifetime.Token);
        using var staleConfig = JsonDocument.Parse("""{"gateway":{"port":18001}}""");
        var gate = (SemaphoreSlim)typeof(WebSocketClientBase).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        try
        {
            await gate.WaitAsync();
            Task<ConfigPatchResult> queued;
            try
            {
                queued = client.PatchConfigOnConnectionAsync(staleConfig.RootElement, "base-hash", epoch, captured);
                Assert.False(queued.IsCompleted);
                if (changeProfile)
                {
                    var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    client.SelfProfileChanged += (_, _) => invalidated.TrySetResult();
                    await server.SendTextAsync("""{"type":"event","event":"sessions.changed","payload":{"reason":"profile-identity"}}""");
                    await invalidated.Task.WaitAsync(ConfigProfileDeadline);
                    Assert.Equal(epoch, client.SessionMutationConnectionEpoch);
                    Assert.False(captured.IsCurrent);
                }
            }
            finally { gate.Release(); }

            if (changeProfile)
            {
                var rejected = await queued.WaitAsync(ConfigProfileDeadline);
                Assert.Empty(writes);
                Assert.False(rejected.Ok);
                Assert.False(rejected.TimedOut);
                Assert.False(rejected.LooksLikeStaleBaseHash);
                Assert.Equal(0, helper.GetPendingRequestCount());
            }
            else
            {
                Assert.True((await queued.WaitAsync(ConfigProfileDeadline)).Ok);
                var submitted = Assert.Single(writes).GetProperty("params");
                Assert.Equal("base-hash", submitted.GetProperty("baseHash").GetString());
                using var raw = JsonDocument.Parse(submitted.GetProperty("raw").GetString()!);
                Assert.Equal(18001, raw.RootElement.GetProperty("gateway").GetProperty("port").GetInt32());
            }

            if (changeProfile)
            {
                using var freshConfig = JsonDocument.Parse("""{"gateway":{"port":18002}}""");
                Assert.True((await client.PatchConfigOnConnectionAsync(freshConfig.RootElement, "fresh-hash", epoch,
                    Assert.IsType<ConfigWriteAdmission>(client.ConfigWriteAdmission)).WaitAsync(ConfigProfileDeadline)).Ok);
                var submitted = Assert.Single(writes).GetProperty("params");
                Assert.Equal("fresh-hash", submitted.GetProperty("baseHash").GetString());
                using var raw = JsonDocument.Parse(submitted.GetProperty("raw").GetString()!);
                Assert.Equal(18002, raw.RootElement.GetProperty("gateway").GetProperty("port").GetInt32());
            }
        }
        finally
        {
            responseLifetime.Cancel();
            try { await responder; } catch (OperationCanceledException) when (responseLifetime.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task CapturedProfileFromReplacedClient_IsRejectedEvenWhenConnectionEpochsMatch()
    {
        using var firstServer = new LoopbackWebSocketServer();
        using var secondServer = new LoopbackWebSocketServer();
        await firstServer.StartAsync();
        await secondServer.StartAsync();
        using var firstIdentity = new TempDirectory("config-owner-first-");
        using var secondIdentity = new TempDirectory("config-owner-second-");
        var first = new GatewayClientTestHelper(gatewayUrl: firstServer.WebSocketUrl, identityPath: firstIdentity.Path);
        var second = new GatewayClientTestHelper(gatewayUrl: secondServer.WebSocketUrl, identityPath: secondIdentity.Path);
        using var firstClient = first.Client;
        using var secondClient = second.Client;
        await firstClient.ConnectAsync();
        await secondClient.ConnectAsync();
        first.MarkHandshakeReady();
        second.MarkHandshakeReady();
        var epoch = Assert.IsType<long>(secondClient.SessionMutationConnectionEpoch);
        Assert.Equal(firstClient.SessionMutationConnectionEpoch, secondClient.SessionMutationConnectionEpoch);
        var priorOwner = Assert.IsType<ConfigWriteAdmission>(firstClient.ConfigWriteAdmission);
        var writes = new ConcurrentQueue<JsonElement>();
        using var lifetime = new CancellationTokenSource();
        var responder = RespondToConfigProfileAsync(secondServer, writes, lifetime.Token);
        using var config = JsonDocument.Parse("""{"gateway":{"port":18003}}""");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => secondClient.PatchConfigOnConnectionAsync(
                config.RootElement, "owner-hash", epoch, priorOwner).WaitAsync(ConfigProfileDeadline));
            Assert.Empty(writes);
            Assert.Equal(0, second.GetPendingRequestCount());
            Assert.True((await secondClient.PatchConfigOnConnectionAsync(config.RootElement, "owner-hash", epoch,
                Assert.IsType<ConfigWriteAdmission>(secondClient.ConfigWriteAdmission)).WaitAsync(ConfigProfileDeadline)).Ok);
            Assert.Equal("owner-hash", Assert.Single(writes).GetProperty("params").GetProperty("baseHash").GetString());
        }
        finally
        {
            lifetime.Cancel();
            try { await responder; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static async Task RespondToConfigProfileAsync(LoopbackWebSocketServer server, ConcurrentQueue<JsonElement> writes, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var request = JsonDocument.Parse(await server.ReceiveTextAsync(token));
            var root = request.RootElement;
            if (root.GetProperty("method").GetString() == "config.patch") writes.Enqueue(root.Clone());
            await server.SendTextAsync(JsonSerializer.Serialize(new
            {
                type = "res", id = root.GetProperty("id").GetString(), ok = true, payload = new { }
            }), token);
        }
    }
}
