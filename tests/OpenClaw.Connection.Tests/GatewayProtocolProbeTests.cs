using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayProtocolProbeTests
{
    internal const string Challenge = """{"type":"event","event":"connect.challenge","payload":{"nonce":"fixture","ts":1}}""";
    private static readonly TimeSpan s_generousTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(Challenge, nameof(GatewayProtocolProbeResult.Gateway))]
    [InlineData("""{"type":"event","event":"tick"}""", nameof(GatewayProtocolProbeResult.OtherProtocol))]
    [InlineData("""{"type":"res","event":"connect.challenge"}""", nameof(GatewayProtocolProbeResult.OtherProtocol))]
    [InlineData("<html>sandbox</html>", nameof(GatewayProtocolProbeResult.OtherProtocol))]
    public async Task OnlyTheGatewayChallengeIdentifiesTheProtocolEndpoint(string firstFrame, string expected)
    {
        using var server = new SilentWebSocketServer(firstFrame);

        Assert.Equal(Enum.Parse<GatewayProtocolProbeResult>(expected), await GatewayProtocolProbe.ProbeAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/"), s_generousTimeout, CancellationToken.None));
    }

    [Theory]
    [InlineData("404 Not Found", nameof(GatewayProtocolProbeResult.OtherProtocol))]
    [InlineData("503 Service Unavailable", nameof(GatewayProtocolProbeResult.Unavailable))]
    [InlineData("429 Too Many Requests", nameof(GatewayProtocolProbeResult.Unavailable))]
    public async Task RejectedUpgradeDistinguishesTheSandboxFromABusyGateway(string status, string expected)
    {
        // The MCP App sandbox host answers a WebSocket upgrade with 404; the Gateway itself refuses
        // with 503 while its WebSocket admission is briefly closed.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task respond = RespondWithStatusAsync(listener, status);

        Assert.Equal(Enum.Parse<GatewayProtocolProbeResult>(expected), await GatewayProtocolProbe.ProbeAsync(
            new Uri($"ws://127.0.0.1:{port}/"), s_generousTimeout, CancellationToken.None));
        await respond;
    }

    [Fact]
    public async Task SilentListenerIsUnavailableAtTheProbeDeadline()
    {
        using var server = new SilentWebSocketServer();

        Assert.Equal(GatewayProtocolProbeResult.Unavailable, await GatewayProtocolProbe.ProbeAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/"), TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAProbeResult()
    {
        using var server = new SilentWebSocketServer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatewayProtocolProbe.ProbeAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/"), s_generousTimeout, cancellation.Token));
    }

    private static async Task RespondWithStatusAsync(TcpListener listener, string status)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        NetworkStream stream = client.GetStream();
        var request = new StringBuilder();
        var buffer = new byte[4096];
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
                return;
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
    }
}

/// <summary>Replaces the process-wide default proxy, so it must not run beside other network tests.</summary>
[CollectionDefinition(nameof(ProcessProxyCollection), DisableParallelization = true)]
public sealed class ProcessProxyCollection;

[Collection(nameof(ProcessProxyCollection))]
public sealed class GatewayProtocolProbeProxyTests
{
    [Fact]
    public async Task ProbeAnswersFromTheLocalListenerNotTheProcessProxy()
    {
        // A proxy that is effective for loopback would otherwise answer for the listener whose
        // identity Companion verifies, and could hand back a challenge the listener never sent.
        using var proxy = new SilentWebSocketServer(GatewayProtocolProbeTests.Challenge);
        using var sandbox = new SilentWebSocketServer("""{"type":"event","event":"tick"}""");
        IWebProxy previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy(new Uri($"http://127.0.0.1:{proxy.Port}/"), BypassOnLocal: false);
        try
        {
            Assert.False(HttpClient.DefaultProxy.IsBypassed(new Uri($"http://127.0.0.1:{sandbox.Port}/")));

            GatewayProtocolProbeResult result = await GatewayProtocolProbe.ProbeAsync(
                new Uri($"ws://127.0.0.1:{sandbox.Port}/"), TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(GatewayProtocolProbeResult.OtherProtocol, result);
            Assert.Equal(0, proxy.AcceptedConnections);
            Assert.Equal(1, sandbox.AcceptedConnections);
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
        }
    }
}
