namespace OpenClaw.Connection.Tests;

public sealed class SshTunnelRecoveryBudgetTests
{
    private static readonly SshTunnelConfig Tunnel =
        new("user", "host.example", 18789, 45678);

    [Fact]
    public void TryReserve_BoundsAndBacksOffRepeatedFailures()
    {
        var budget = new SshTunnelRecoveryBudget();
        var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var tunnelExit = new SshTunnelExit(
            255,
            Tunnel,
            Generation: 7,
            SshTunnelOwner.GatewayConnectionManager);

        Assert.True(budget.TryReserve(tunnelExit, now, out var first));
        Assert.Equal(TimeSpan.FromSeconds(3), first);
        Assert.True(budget.TryReserve(tunnelExit, now.AddSeconds(3), out var second));
        Assert.Equal(TimeSpan.FromSeconds(10), second);
        Assert.True(budget.TryReserve(tunnelExit, now.AddSeconds(13), out var third));
        Assert.Equal(TimeSpan.FromSeconds(30), third);
        Assert.False(budget.TryReserve(tunnelExit, now.AddSeconds(43), out var exhausted));
        Assert.Equal(TimeSpan.Zero, exhausted);
    }

    [Fact]
    public void TryReserve_ResetsAfterHealthyWindow()
    {
        var budget = new SshTunnelRecoveryBudget();
        var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var tunnelExit = new SshTunnelExit(
            255,
            Tunnel,
            Generation: 7,
            SshTunnelOwner.Settings);

        Assert.True(budget.TryReserve(tunnelExit, now, out _));
        Assert.True(budget.TryReserve(tunnelExit, now.AddMinutes(5), out var delay));
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void TryReserve_ResetsForDifferentTunnelOwner()
    {
        var budget = new SshTunnelRecoveryBudget();
        var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var managerExit = new SshTunnelExit(
            255,
            Tunnel,
            Generation: 7,
            SshTunnelOwner.GatewayConnectionManager);
        var settingsExit = managerExit with
        {
            Generation = 8,
            Owner = SshTunnelOwner.Settings
        };

        Assert.True(budget.TryReserve(managerExit, now, out _));
        Assert.True(budget.TryReserve(managerExit, now.AddSeconds(3), out _));
        Assert.True(budget.TryReserve(settingsExit, now.AddSeconds(4), out var delay));
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void ReportRecovered_RearmsMatchingTunnel()
    {
        var budget = new SshTunnelRecoveryBudget();
        var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var tunnelExit = new SshTunnelExit(
            255,
            Tunnel,
            Generation: 7,
            SshTunnelOwner.GatewayConnectionManager);

        Assert.True(budget.TryReserve(tunnelExit, now, out _));
        Assert.True(budget.TryReserve(tunnelExit, now.AddSeconds(3), out _));

        budget.ReportRecovered(tunnelExit);

        Assert.True(budget.TryReserve(tunnelExit, now.AddSeconds(13), out var delay));
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void ReportRecovered_DoesNotRearmDifferentOwner()
    {
        var budget = new SshTunnelRecoveryBudget();
        var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var managerExit = new SshTunnelExit(
            255,
            Tunnel,
            Generation: 7,
            SshTunnelOwner.GatewayConnectionManager);
        var settingsExit = managerExit with { Owner = SshTunnelOwner.Settings };

        Assert.True(budget.TryReserve(managerExit, now, out _));
        budget.ReportRecovered(settingsExit);

        Assert.True(budget.TryReserve(managerExit, now.AddSeconds(3), out var delay));
        Assert.Equal(TimeSpan.FromSeconds(10), delay);
    }

    [Fact]
    public void Reset_RearmsCurrentTunnel()
    {
        var budget = new SshTunnelRecoveryBudget();
        var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var tunnelExit = new SshTunnelExit(
            255,
            Tunnel,
            Generation: 7,
            SshTunnelOwner.Settings);

        Assert.True(budget.TryReserve(tunnelExit, now, out _));
        budget.Reset();

        Assert.True(budget.TryReserve(tunnelExit, now.AddSeconds(3), out var delay));
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }
}
