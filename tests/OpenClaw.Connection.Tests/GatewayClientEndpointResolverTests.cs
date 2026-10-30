using System.Net;
using System.Net.Sockets;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayClientEndpointResolverTests
{
    [Fact]
    public void Resolve_UsesRecordUrlIncludingCustomPort()
    {
        var record = new GatewayRecord
        {
            Id = "local-custom-port",
            Url = "ws://localhost:27555",
        };

        Assert.Equal("ws://localhost:27555", GatewayClientEndpointResolver.Resolve(record));
    }

    [Fact]
    public void Resolve_UsesLocalForwardForTunnelBackedRecord()
    {
        var record = new GatewayRecord
        {
            Id = "mixed-managed-wsl-ssh",
            Url = "ws://remote.internal:18789",
            SetupManagedDistroName = "OpenClawGateway",
            SshTunnel = new SshTunnelConfig(
                "user",
                "remote.internal",
                RemotePort: 18789,
                LocalPort: 45678),
        };

        Assert.Equal("ws://localhost:45678", GatewayClientEndpointResolver.Resolve(record));
    }

    [Fact]
    public void Resolve_PinsHeldDashboardPortToIpv4Loopback()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        DashboardForwardPortGuard.Hold(port);
        try
        {
            var record = new GatewayRecord
            {
                Id = "held-forward",
                Url = "ws://remote.internal:18789",
                SshTunnel = new SshTunnelConfig("user", "remote.internal", 18789, port),
            };

            Assert.Equal($"ws://127.0.0.1:{port}", GatewayClientEndpointResolver.Resolve(record));
        }
        finally
        {
            DashboardForwardPortGuard.Release(port);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Resolve_RejectsInvalidTunnelLocalPort(int localPort)
    {
        var record = new GatewayRecord
        {
            Id = "invalid-tunnel",
            Url = "wss://remote.example",
            SshTunnel = new SshTunnelConfig(
                "user",
                "remote.example",
                RemotePort: 18789,
                LocalPort: localPort),
        };

        var error = Assert.Throws<InvalidOperationException>(() => GatewayClientEndpointResolver.Resolve(record));

        Assert.Contains("local port", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
