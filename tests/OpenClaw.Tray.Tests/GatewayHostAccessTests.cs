using OpenClaw.Connection;

namespace OpenClaw.Tray.Tests;

public class GatewayHostAccessTests
{
    [Fact]
    public void Classify_UsesWslManagedDistro_WhenRecordWasCreatedBySetup()
    {
        var record = new GatewayRecord
        {
            Id = "local",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        };

        var access = GatewayHostAccessClassifier.Classify(record);

        Assert.Equal(GatewayTerminalTarget.Wsl, access.TerminalTarget);
        Assert.True(access.CanOpenTerminal);
        Assert.True(access.CanControlWslGateway);
        Assert.Equal("OpenClawGateway", access.DistroName);
    }

    [Fact]
    public void Classify_UsesSshTerminal_WhenRecordHasOnlySshTunnel()
    {
        var record = new GatewayRecord
        {
            Id = "ssh",
            Url = "ws://127.0.0.1:18789",
            SshTunnel = new SshTunnelConfig("alice", "gateway.example.test", 18789, 18789),
        };

        var access = GatewayHostAccessClassifier.Classify(record);

        Assert.Equal(GatewayTerminalTarget.Ssh, access.TerminalTarget);
        Assert.True(access.CanOpenTerminal);
        Assert.False(access.CanControlWslGateway);
        Assert.Equal("alice", access.SshUser);
        Assert.Equal("gateway.example.test", access.SshHost);
    }

    [Fact]
    public void Classify_UsesNativeTerminal_WhenRecordWasCreatedByNativeSetup()
    {
        var record = new GatewayRecord
        {
            Id = "native",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SetupManagedNativeTaskName = "OpenClaw Gateway (OpenClawGateway)",
        };

        var access = GatewayHostAccessClassifier.Classify(record);

        Assert.Equal(GatewayTerminalTarget.Native, access.TerminalTarget);
        Assert.True(access.CanOpenTerminal);
        Assert.False(access.CanControlWslGateway);
        Assert.True(access.CanControlNativeGateway);
        Assert.Equal("OpenClaw Gateway (OpenClawGateway)", access.NativeTaskName);
    }

    [Fact]
    public void Classify_PrefersWslTerminal_WhenRecordHasWslAndSshMetadata()
    {
        var record = new GatewayRecord
        {
            Id = "mixed",
            Url = "ws://127.0.0.1:18789",
            SetupManagedDistroName = "OpenClawGateway",
            SshTunnel = new SshTunnelConfig("alice", "gateway.example.test", 18789, 18789),
        };

        var access = GatewayHostAccessClassifier.Classify(record);

        Assert.Equal(GatewayTerminalTarget.Wsl, access.TerminalTarget);
        Assert.True(access.CanControlWslGateway);
        Assert.Equal("OpenClawGateway", access.DistroName);
    }

    [Fact]
    public void Classify_DisablesTerminal_WhenRecordHasNoHostAccessMetadata()
    {
        var record = new GatewayRecord
        {
            Id = "remote",
            Url = "wss://gateway.example.test",
        };

        var access = GatewayHostAccessClassifier.Classify(record);

        Assert.Equal(GatewayTerminalTarget.None, access.TerminalTarget);
        Assert.False(access.CanOpenTerminal);
        Assert.False(access.CanControlWslGateway);
        Assert.False(access.CanControlNativeGateway);
        Assert.NotNull(access.DisabledReason);
    }
}
