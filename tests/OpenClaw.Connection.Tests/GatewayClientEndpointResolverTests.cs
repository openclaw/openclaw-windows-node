using OpenClaw.Shared;

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

    [Fact]
    public void TryResolveDashboardEndpoint_SshRecordDoesNotOpenSavedUrlWhenTunnelIsDown()
    {
        var record = SshRecord();

        var opened = GatewayClientEndpointResolver.TryResolveDashboardEndpoint(
            record,
            tunnel: null,
            out var endpoint,
            out var appendSharedToken);

        Assert.False(opened);
        Assert.False(appendSharedToken);
        Assert.DoesNotContain("gateway.example", endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared-secret", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveDashboardEndpoint_SshRecordUsesTunnelOnlyWhenThatForwardIsUp()
    {
        var record = SshRecord();
        var tunnel = UpTunnel(localPort: 45678);

        var opened = GatewayClientEndpointResolver.TryResolveDashboardEndpoint(
            record,
            tunnel,
            out var endpoint,
            out var appendSharedToken,
            listenerOwned: true);

        Assert.True(opened);
        Assert.Equal("ws://localhost:45678", endpoint);
        Assert.True(appendSharedToken);
        Assert.DoesNotContain("gateway.example", endpoint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolveDashboardEndpoint_SshRecordRejectsDifferentSshServerPort()
    {
        var record = SshRecord(sshPort: 2222);
        var tunnel = UpTunnel(localPort: 45678, currentSshPort: 22);

        var opened = GatewayClientEndpointResolver.TryResolveDashboardEndpoint(
            record,
            tunnel,
            out var endpoint,
            out var appendSharedToken,
            listenerOwned: true);

        Assert.False(opened);
        Assert.False(appendSharedToken);
        Assert.Equal("", endpoint);
        Assert.DoesNotContain("gateway.example", endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared-secret", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveDashboardEndpoint_SshRecordDoesNotOpenWhenListenerIsNotOwned()
    {
        var record = SshRecord();
        var tunnel = UpTunnel(localPort: 45678, currentSshPort: 22);

        var opened = GatewayClientEndpointResolver.TryResolveDashboardEndpoint(
            record,
            tunnel,
            out var endpoint,
            out var appendSharedToken,
            listenerOwned: false);

        Assert.False(opened);
        Assert.False(appendSharedToken);
        Assert.Equal("", endpoint);
        Assert.DoesNotContain("gateway.example", endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared-secret", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveDashboardEndpoint_SshRecordRejectsADifferentLocalForward()
    {
        var record = SshRecord();

        var opened = GatewayClientEndpointResolver.TryResolveDashboardEndpoint(
            record,
            UpTunnel(localPort: 9999),
            out var endpoint,
            out var appendSharedToken);

        Assert.False(opened);
        Assert.False(appendSharedToken);
        Assert.DoesNotContain("gateway.example", endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared-secret", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveDashboardEndpoint_DirectRecordKeepsSavedUrl()
    {
        var record = new GatewayRecord
        {
            Id = "direct",
            Url = "ws://127.0.0.1:18789",
            SharedGatewayToken = "shared-secret",
        };

        var opened = GatewayClientEndpointResolver.TryResolveDashboardEndpoint(
            record,
            tunnel: null,
            out var endpoint,
            out var appendSharedToken);

        Assert.True(opened);
        Assert.Equal("ws://127.0.0.1:18789", endpoint);
        Assert.True(appendSharedToken);
    }

    private static GatewayRecord SshRecord(int sshPort = 22) => new()
    {
        Id = "ssh",
        Url = "ws://gateway.example:18789",
        SharedGatewayToken = "shared-secret",
        SshTunnel = new SshTunnelConfig("user", "gateway.example", 18789, 45678, SshPort: sshPort),
    };

    private static SshTunnelSnapshot UpTunnel(int localPort, int currentSshPort = 22) => new(
        IsRunning: true,
        CurrentUser: "user",
        CurrentHost: "gateway.example",
        CurrentRemotePort: 18789,
        CurrentLocalPort: localPort,
        CurrentBrowserProxyRemotePort: 0,
        CurrentBrowserProxyLocalPort: 0,
        StartedAtUtc: new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
        LastError: null,
        Status: TunnelStatus.Up,
        CurrentSshPort: currentSshPort);
}
