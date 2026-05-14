using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.IO;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class TrayTooltipBuilderTests : IDisposable
{
    private static readonly DateTime FixedTime = new(2024, 1, 15, 10, 30, 45);

    private readonly string _tempDir;

    public TrayTooltipBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    private static TrayStateSnapshot BaseConnected(
        ChannelHealth[]? channels = null,
        GatewayNodeInfo[]? nodes = null,
        string? authFailure = null,
        AgentActivity? activity = null) => new TrayStateSnapshot
    {
        Status = ConnectionStatus.Connected,
        Channels = channels ?? [],
        Nodes = nodes ?? [],
        AuthFailureMessage = authFailure,
        CurrentActivity = activity,
        LastCheckTime = FixedTime
    };

    [Fact]
    public void Build_ConnectedWithChannelsAndNodes_ContainsExpectedSegments()
    {
        var snapshot = BaseConnected(
            channels: [new ChannelHealth { Status = "ok" }, new ChannelHealth { Status = "stopped" }],
            nodes: [new GatewayNodeInfo { IsOnline = true }]);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("OpenClaw Tray - Connected", result);
        Assert.Contains("Channels 1/2", result);
        Assert.Contains("Nodes 1/1", result);
        Assert.Contains("Warnings 0", result);
        Assert.Contains("Last 10:30:45", result);
    }

    [Fact]
    public void Build_ActivityWithDisplayText_OverridesStandardFormat()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Exec, Label = "running tests", IsMain = true };
        var snapshot = BaseConnected(activity: activity);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("💻 running tests", result);
        Assert.Contains("Connected", result);
        Assert.DoesNotContain("Channels", result);
    }

    [Fact]
    public void Build_ActivityIdle_DoesNotOverrideStandardFormat()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Idle };
        var snapshot = BaseConnected(
            channels: [new ChannelHealth { Status = "ok" }],
            activity: activity);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Channels", result);
    }

    [Fact]
    public void Build_StatusNotConnected_CountsOneWarning()
    {
        var snapshot = new TrayStateSnapshot
        {
            Status = ConnectionStatus.Disconnected,
            Channels = [new ChannelHealth { Status = "ok" }],
            Nodes = [],
            LastCheckTime = FixedTime
        };

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Warnings 1", result);
    }

    [Fact]
    public void Build_AuthFailureMessage_CountsOneWarning()
    {
        var snapshot = BaseConnected(
            channels: [new ChannelHealth { Status = "ok" }],
            authFailure: "token expired");

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Warnings 1", result);
    }

    [Fact]
    public void Build_ConnectedWithNoChannels_CountsOneWarning()
    {
        var snapshot = BaseConnected(channels: []);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Warnings 1", result);
    }

    [Fact]
    public void Build_MultipleWarningConditions_SumsCorrectly()
    {
        // Disconnected (+1) + auth failure (+1); no-channels warning only fires when Connected
        var snapshot = new TrayStateSnapshot
        {
            Status = ConnectionStatus.Disconnected,
            AuthFailureMessage = "expired",
            Channels = [],
            Nodes = [],
            LastCheckTime = FixedTime
        };

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Warnings 2", result);
    }

    [Fact]
    public void Build_NoNodesNoFallback_ShowsZeroNodes()
    {
        var snapshot = BaseConnected(
            channels: [new ChannelHealth { Status = "ok" }],
            nodes: []);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Nodes 0/0", result);
    }

    [Fact]
    public void Build_NoGatewayNodesButLocalFallback_UsesLocalNode()
    {
        var snapshot = new TrayStateSnapshot
        {
            Status = ConnectionStatus.Connected,
            Channels = [new ChannelHealth { Status = "ok" }],
            Nodes = [],
            LocalNodeFallback = new GatewayNodeInfo { IsOnline = true },
            LastCheckTime = FixedTime
        };

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Nodes 1/1", result);
    }

    [Fact]
    public void Build_LocalFallbackOffline_CountsAsOffline()
    {
        var snapshot = new TrayStateSnapshot
        {
            Status = ConnectionStatus.Connected,
            Channels = [new ChannelHealth { Status = "ok" }],
            Nodes = [],
            LocalNodeFallback = new GatewayNodeInfo { IsOnline = false },
            LastCheckTime = FixedTime
        };

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Nodes 0/1", result);
    }

    [Fact]
    public void Build_MultipleNodes_CountsOnlineCorrectly()
    {
        var snapshot = BaseConnected(nodes:
        [
            new GatewayNodeInfo { IsOnline = true },
            new GatewayNodeInfo { IsOnline = false },
            new GatewayNodeInfo { IsOnline = true }
        ]);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Nodes 2/3", result);
    }

    [Fact]
    public void Build_ChannelCounting_OnlyHealthyStatusesCountAsReady()
    {
        var snapshot = BaseConnected(channels:
        [
            new ChannelHealth { Status = "ok" },
            new ChannelHealth { Status = "running" },
            new ChannelHealth { Status = "stopped" },
            new ChannelHealth { Status = "error" }
        ]);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Channels 2/4", result);
    }

    [Fact]
    public void Build_LongTooltip_IsTruncatedToShellLimit()
    {
        var activity = new AgentActivity
        {
            Kind = ActivityKind.Exec,
            Label = new string('x', 200),
            IsMain = true
        };
        var snapshot = BaseConnected(activity: activity);

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.True(result.Length <= TrayTooltipFormatter.MaxShellTooltipLength);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void Build_NullSettings_ShowsUnknownGatewayTopology()
    {
        var snapshot = BaseConnected();

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Unknown gateway", result);
    }

    [Fact]
    public void Build_SshTunnelSettings_ReflectsMacOverSshTopology()
    {
        var settings = new SettingsManager(_tempDir)
        {
            UseSshTunnel = true,
            SshTunnelHost = "myhost.com"
        };
        var snapshot = new TrayStateSnapshot
        {
            Status = ConnectionStatus.Connected,
            Settings = settings,
            Channels = [],
            Nodes = [],
            LastCheckTime = FixedTime
        };

        var result = new TrayTooltipBuilder(snapshot).Build();

        Assert.Contains("Mac over SSH", result);
    }
}
