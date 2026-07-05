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
