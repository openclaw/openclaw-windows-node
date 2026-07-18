using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Shared.Tests;

[Collection("WebSocketClientBase")]
public sealed class WebSocketCloseSerializationTests
{
    [Fact]
    public async Task CloseWebSocketAsync_WaitsForInFlightSend()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = new CloseRaceTestClient(server.WebSocketUrl);

        await client.ConnectAsync();
        await WaitForConditionAsync(() => server.AcceptedCount == 1, TimeSpan.FromSeconds(2));

        var sendLock = GetSendLock(client);
        await sendLock.WaitAsync();
        var lockHeld = true;
        try
        {
            var serverTask = ReceiveUntilCloseAndAcknowledgeAsync(GetAcceptedSocket(server));
            var sendTask = client.SendAsync("payload");
            Assert.False(sendTask.IsCompleted);

            var closeTask = client.CloseAsync();
            var prematureClose = await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(closeTask, prematureClose);

            sendLock.Release();
            lockHeld = false;
            await Task.WhenAll(sendTask, closeTask, serverTask).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await serverTask);
        }
        finally
        {
            if (lockHeld)
                sendLock.Release();
        }
    }

    [Fact]
    public async Task CloseWebSocketAsync_DisposeCancelsWaitBehindInFlightSend()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = new CloseRaceTestClient(server.WebSocketUrl);

        await client.ConnectAsync();
        await WaitForConditionAsync(() => server.AcceptedCount == 1, TimeSpan.FromSeconds(2));

        var sendLock = GetSendLock(client);
        await sendLock.WaitAsync();
        try
        {
            var sendTask = client.SendAsync("payload");
            Assert.False(sendTask.IsCompleted);
            var closeTask = client.CloseAsync();
            Assert.False(closeTask.IsCompleted);

            client.Dispose();

            await sendTask.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForCloseCompletionAsync(closeTask);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<bool> ReceiveUntilCloseAndAcknowledgeAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        var receivedText = false;
        while (true)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                CancellationToken.None);
            if (result.MessageType != WebSocketMessageType.Close)
            {
                receivedText |= result.MessageType == WebSocketMessageType.Text;
                continue;
            }

            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "test close",
                CancellationToken.None);
            return receivedText;
        }
    }

    private static async Task WaitForCloseCompletionAsync(Task closeTask)
    {
        var closeCompletion = await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(closeTask, closeCompletion);
        if (closeTask.IsFaulted)
            Assert.IsAssignableFrom<OperationCanceledException>(closeTask.Exception!.GetBaseException());
    }

    private static SemaphoreSlim GetSendLock(WebSocketClientBase client) =>
        (SemaphoreSlim)typeof(WebSocketClientBase)
            .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

    private static WebSocket GetAcceptedSocket(LoopbackWebSocketServer server)
    {
        var sockets = (List<WebSocket>)typeof(LoopbackWebSocketServer)
            .GetField("_acceptedSockets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!;
        lock (sockets)
        {
            return sockets[0];
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not met before the timeout.");

            await Task.Delay(25);
        }
    }

    private sealed class CloseRaceTestClient : WebSocketClientBase
    {
        public CloseRaceTestClient(string gatewayUrl)
            : base(gatewayUrl, "test-value")
        {
        }

        protected override int ReceiveBufferSize => 8192;
        protected override string ClientRole => "close-race-test";
        protected override bool ShouldAutoReconnect() => false;
        protected override Task ProcessMessageAsync(string json) => Task.CompletedTask;

        public Task SendAsync(string message) => SendRawAsync(message);
        public Task CloseAsync() => CloseWebSocketAsync();
    }
}
