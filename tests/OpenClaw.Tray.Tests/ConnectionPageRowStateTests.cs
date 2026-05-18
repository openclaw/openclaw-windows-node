using OpenClaw.Connection;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Behavior tests for the saved-gateway row state predicates. These guard
/// the regression where the overflow menu offered Disconnect during
/// Disconnecting (re-entering teardown), and the related cleanup that
/// gave the active row a proper Error badge instead of dropping back to
/// [Connect] indistinguishably from inactive rows.
/// </summary>
public sealed class ConnectionPageRowStateTests
{
    [Theory]
    [InlineData(OverallConnectionState.Connected)]
    [InlineData(OverallConnectionState.Ready)]
    [InlineData(OverallConnectionState.Degraded)]
    [InlineData(OverallConnectionState.Connecting)]
    [InlineData(OverallConnectionState.PairingRequired)]
    public void CanDisconnectFromBadge_TrueForLiveAndPendingStates(OverallConnectionState state)
    {
        Assert.True(ConnectionPageRowState.CanDisconnectFromBadge(state),
            $"{state} should expose Disconnect (state has a live/pending connection to tear down).");
    }

    [Fact]
    public void CanDisconnectFromBadge_FalseWhileDisconnecting()
    {
        // Disconnecting is the exact state that USED to receive Disconnect
        // and race the connection manager — this is the regression guard.
        Assert.False(ConnectionPageRowState.CanDisconnectFromBadge(OverallConnectionState.Disconnecting),
            "Disconnecting must NOT offer Disconnect — teardown is already in flight.");
    }

    [Fact]
    public void CanDisconnectFromBadge_FalseWhenIdle()
    {
        // Idle rows render [Connect], not a badge — there's nothing to
        // disconnect from.
        Assert.False(ConnectionPageRowState.CanDisconnectFromBadge(OverallConnectionState.Idle),
            "Idle rows render [Connect]; Disconnect is meaningless.");
    }

    [Fact]
    public void CanDisconnectFromBadge_FalseInErrorState()
    {
        // In Error the row renders [Connect] so the user can retry. The
        // status strip up top already shouts "Can't reach gateway" and
        // the Recovery card right beneath it offers Disconnect — adding
        // it to the row overflow too would duplicate the action.
        Assert.False(ConnectionPageRowState.CanDisconnectFromBadge(OverallConnectionState.Error),
            "Error rows render [Connect]; Disconnect lives on the Recovery card above.");
    }

    [Theory]
    [InlineData(OverallConnectionState.Connected)]
    [InlineData(OverallConnectionState.Ready)]
    [InlineData(OverallConnectionState.Degraded)]
    [InlineData(OverallConnectionState.Connecting)]
    [InlineData(OverallConnectionState.PairingRequired)]
    [InlineData(OverallConnectionState.Disconnecting)]
    public void HasActiveRowBadge_TrueForLiveAndTransientStates(OverallConnectionState state)
    {
        Assert.True(ConnectionPageRowState.HasActiveRowBadge(state),
            $"{state} should render a status badge so the user can see the live state.");
    }

    [Fact]
    public void HasActiveRowBadge_FalseWhenIdle()
    {
        Assert.False(ConnectionPageRowState.HasActiveRowBadge(OverallConnectionState.Idle),
            "Idle has no live connection — the row falls back to [Connect].");
    }

    [Fact]
    public void HasActiveRowBadge_FalseInErrorState()
    {
        // Error explicitly falls back to [Connect] so the user has an
        // actionable retry per gateway. The strip + Recovery card up top
        // already carry the failure signal; the row should be the action
        // surface, not a redundant status surface.
        Assert.False(ConnectionPageRowState.HasActiveRowBadge(OverallConnectionState.Error),
            "Error rows render [Connect] so the user can explicitly retry.");
    }
}
