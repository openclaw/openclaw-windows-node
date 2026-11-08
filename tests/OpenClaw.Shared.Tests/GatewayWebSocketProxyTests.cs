using System.Net.WebSockets;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class GatewayWebSocketProxyTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:18789/")]
    [InlineData("ws://localhost:18789/")]
    [InlineData("ws://[::1]:18789/")]
    public void Loopback_ClearsTheProcessProxy(string url)
    {
        using var socket = new ClientWebSocket();
        Assert.NotNull(socket.Options.Proxy);
        WebSocketClientBase.ConfigureConnectProxy(socket, new Uri(url));
        Assert.Null(socket.Options.Proxy);
    }

    [Theory]
    [InlineData("wss://gateway.example/")]
    [InlineData("ws://203.0.113.10:18789/")]
    public void Remote_KeepsTheProcessProxy(string url)
    {
        using var socket = new ClientWebSocket();
        var before = socket.Options.Proxy;
        WebSocketClientBase.ConfigureConnectProxy(socket, new Uri(url));
        Assert.Same(before, socket.Options.Proxy);
    }
}
