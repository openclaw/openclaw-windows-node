using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Regression tests for the #1021 sibling sites: when the client-lifetime token is
/// cancelled while a chat.send or exec.approval.resolve response is pending, the
/// failure must surface as a cancellation, not as a gateway timeout. The
/// wizard-request site has its own fix and tests (#1018).
/// </summary>
[Collection("WebSocketClientBase")]
public sealed class GatewayRequestShutdownClassificationTests
{
    [Fact]
    public async Task SendChatMessage_ShutdownDuringWait_SurfacesCancellationNotTimeout()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = CreateClient(server);
        await ConnectAsync(client, server);

        // Start the request, then cancel the lifetime token only after the server has
        // observed the request frame — by then the request has passed its connectivity
        // guards and is parked on the response wait, so the cancelled timeout delay is
        // the only thing that can complete it.
        var requestTask = client.SendChatMessageAsync("hello", sessionKey: "agent:main:test");
        await WaitForFrameContainingAsync(server, "chat.send", TimeSpan.FromSeconds(2));
        CancelLifetime(client);

        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4),
            "shutdown must classify immediately instead of waiting out the 5s chat.send budget");
    }

    [Fact]
    public async Task ResolveExecApproval_ShutdownDuringWait_SurfacesCancellationNotTimeout()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = CreateClient(server);
        await ConnectAsync(client, server);

        var requestTask = client.ResolveExecApprovalAsync("approval-1", "allow-once");
        await WaitForFrameContainingAsync(server, "exec.approval.resolve", TimeSpan.FromSeconds(2));
        CancelLifetime(client);

        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10),
            "shutdown must classify immediately instead of waiting out the 15s approval budget");
    }

    private static OpenClawGatewayClient CreateClient(LoopbackWebSocketServer server)
    {
        var identityPath = Path.Combine(
            Path.GetTempPath(),
            "GatewayRequestShutdownClassificationTests",
            Guid.NewGuid().ToString("N"));
        return new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", new TestLogger(), identityPath: identityPath);
    }

    private static async Task ConnectAsync(OpenClawGatewayClient client, LoopbackWebSocketServer server)
    {
        await client.ConnectAsync();
        var start = DateTime.UtcNow;
        while (server.AcceptedCount < 1)
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(2))
                throw new TimeoutException("Loopback server did not accept the connection.");

            // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
            await Task.Delay(25);
        }
    }

    /// <summary>Reads server-side frames (skipping the connect handshake traffic) until
    /// one contains the marker, proving the request frame reached the wire.</summary>
    private static async Task WaitForFrameContainingAsync(
        LoopbackWebSocketServer server, string marker, TimeSpan timeout)
    {
        var socket = GetAcceptedSocket(server);
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var message = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException($"Unexpected {result.MessageType} frame before '{marker}'.");

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            if (message.ToString().Contains(marker, StringComparison.Ordinal))
                return;
        }
    }

    // Models the shutdown race from #1021: the lifetime token is cancelled while this
    // request's pending completion has not been faulted — the state a request reaches
    // when it registers after ClearPendingRequests has already swept the pending maps.
    private static void CancelLifetime(OpenClawGatewayClient client)
    {
        var field = typeof(WebSocketClientBase).GetField(
            "_cts", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        ((CancellationTokenSource)field!.GetValue(client)!).Cancel();
    }

    private static WebSocket GetAcceptedSocket(LoopbackWebSocketServer server)
    {
        var field = typeof(LoopbackWebSocketServer).GetField(
            "_acceptedSockets", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var sockets = (List<WebSocket>)field!.GetValue(server)!;
        lock (sockets)
        {
            Assert.NotEmpty(sockets);
            return sockets[0];
        }
    }
}
