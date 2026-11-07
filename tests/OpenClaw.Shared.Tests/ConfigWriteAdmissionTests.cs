// Exercises the production final-send guard with real loopback frames and replacement connections.
// Run ConfigWriteAdmissionTests in Shared.Tests.
using System.Reflection;
using System.Net.WebSockets;

namespace OpenClaw.Shared.Tests;

[Collection("WebSocketClientBase")]
public sealed class ConfigWriteAdmissionTests
{
    private const int DefaultDeadlineSeconds = 10;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_ADMISSION_TEST_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? seconds : DefaultDeadlineSeconds);
    private const string Patch = "{\"method\":\"config.patch\",\"id\":\"captured-profile\"}";
    private const string Sentinel = "still-connected";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileInvalidationWhileQueued_RejectsBeforeNetworkIo(bool invalidateProfile)
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = new SendClient(server.WebSocketUrl);
        await client.ConnectAsync();
        var epoch = client.Epoch;
        var admission = new ConfigWriteAdmission();
        var gate = SendGate(client);
        await gate.WaitAsync();
        Task<bool> queued;
        try
        {
            queued = client.SendPatchAsync(Patch, epoch, admission);
            Assert.False(queued.IsCompleted);
            if (invalidateProfile) admission.Invalidate();
            Assert.Equal(epoch, client.Epoch);
        }
        finally { gate.Release(); }

        var admitted = await queued.WaitAsync(Deadline);
        Assert.True(await client.SendSentinelAsync(Sentinel).WaitAsync(Deadline));
        // The sentinel is an ordering barrier on the same real socket. A stale
        // config.patch cannot hide behind an empty collection or a short delay.
        Assert.Equal(invalidateProfile ? Sentinel : Patch, await server.ReceiveTextAsync().WaitAsync(Deadline));
        if (!invalidateProfile) Assert.Equal(Sentinel, await server.ReceiveTextAsync().WaitAsync(Deadline));
        Assert.Equal(!invalidateProfile, admitted);
    }

    [Fact]
    public async Task ReconnectWhilePatchQueued_DoesNotWriteOnReplacementSocket()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = new SendClient(server.WebSocketUrl);
        await client.ConnectAsync();
        var oldEpoch = client.Epoch;
        var gate = SendGate(client);
        await gate.WaitAsync();
        Task<bool> queued;
        try
        {
            queued = client.SendPatchAsync(Patch, oldEpoch, new ConfigWriteAdmission());
            Assert.False(queued.IsCompleted);
            await client.ConnectAsync().WaitAsync(Deadline);
            Assert.NotEqual(oldEpoch, client.Epoch);
        }
        finally { gate.Release(); }
        Assert.False(await queued.WaitAsync(Deadline));
        await server.WaitForAcceptedCountAsync(2, Deadline);
        Assert.True(await client.SendSentinelAsync(Sentinel).WaitAsync(Deadline));
        Assert.Equal(Sentinel, await server.ReceiveTextFromConnectionAsync(1).WaitAsync(Deadline));
    }

    private static SemaphoreSlim SendGate(WebSocketClientBase client) =>
        (SemaphoreSlim)typeof(WebSocketClientBase).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;

    private sealed class SendClient(string url) : WebSocketClientBase(url, "test-value")
    {
        protected override int ReceiveBufferSize => 8192;
        protected override string ClientRole => "config-admission-test";
        protected override bool ShouldAutoReconnect() => false;
        protected override Task ProcessMessageAsync(string json) => Task.CompletedTask;
        internal long Epoch => ConnectionGeneration;
        internal Task<bool> SendPatchAsync(string frame, long epoch, ConfigWriteAdmission admission) =>
            SendRawAsync(frame, epoch, CancellationToken.None, admission);
        internal Task<bool> SendSentinelAsync(string frame) => SendRawAsync(frame, Epoch, CancellationToken.None);
    }
}
