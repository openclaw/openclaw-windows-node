using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class DeferredFlyoutShowStateTests
{
    [Fact]
    public void HideOnAlreadyClosedFlyout_DoesNotBlockLaterRequests()
    {
        var state = new DeferredFlyoutShowState();
        // Leaving Settings calls Hide() on a closed search flyout: Closing without Opened or Closed.
        state.Closing(cancelled: false);
        Assert.False(state.TryDefer());
    }

    [Fact]
    public void RequestDuringRealClose_IsDeferredAndReplayedOnceAfterSettle()
    {
        var state = new DeferredFlyoutShowState();
        state.Opened();
        state.Closing(cancelled: false);
        Assert.True(state.TryDefer());
        Assert.True(state.TryDefer());
        state.Closed();
        Assert.True(state.Settle());
        Assert.False(state.Settle());
        Assert.False(state.TryDefer());
    }

    [Fact]
    public void ClosedWithoutSettle_StillDefersUntilTheCloseFinishes()
    {
        var state = new DeferredFlyoutShowState();
        state.Opened();
        state.Closing(cancelled: false);
        state.Closed();
        // WinUI still ignores ShowAt until the turn after Closed.
        Assert.True(state.TryDefer());
        Assert.True(state.Settle());
    }

    [Fact]
    public void Cancel_DropsTheQueuedReplay()
    {
        var state = new DeferredFlyoutShowState();
        state.Opened();
        state.Closing(cancelled: false);
        Assert.True(state.TryDefer());
        state.Cancel();
        state.Closed();
        Assert.False(state.Settle());
        Assert.False(state.TryDefer());
    }

    [Fact]
    public void CancelledClosing_DoesNotDefer()
    {
        var state = new DeferredFlyoutShowState();
        state.Opened();
        state.Closing(cancelled: true);
        Assert.False(state.TryDefer());
    }

    [Fact]
    public void SettleWithoutRequest_DoesNotReplay_AndReopenClearsClosing()
    {
        var state = new DeferredFlyoutShowState();
        state.Opened();
        state.Closing(cancelled: false);
        state.Closed();
        Assert.False(state.Settle());
        state.Opened();
        Assert.False(state.TryDefer());
    }
}
