using OpenClaw.SetupEngine.UI.Pages;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiSetupAvailabilityCoordinatorTests
{
    [Fact]
    public void ProbeFailure_BecomesUnknownAndRetryable()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var checking = coordinator.StartProbe();

        Assert.True(coordinator.TryApplyProbeFailure(
            checking.Generation,
            "Probe failed.",
            out var unknown));

        Assert.Equal(LocalAiSetupAvailabilityStatus.Unknown, unknown.Status);
        Assert.True(unknown.CanRecheck);
        Assert.Equal("Probe failed.", unknown.Reason);
    }

    [Fact]
    public void RetryAfterUnknown_CanRecoverToAvailable()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var first = coordinator.StartProbe();
        Assert.True(coordinator.TryApplyProbeFailure(first.Generation, "Probe failed.", out var unknown));

        Assert.True(coordinator.TryStartRecheck(out var retry));
        Assert.True(retry.IsChecking);
        Assert.NotEqual(unknown.Generation, retry.Generation);
        Assert.True(coordinator.TryApplyAvailable(retry.Generation, out var available));

        Assert.True(available.IsAvailable);
        Assert.False(available.CanRecheck);
        Assert.Null(available.Reason);
    }

    [Fact]
    public void InFlightRetry_DoesNotStartSecondProbe()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var checking = coordinator.StartProbe();

        Assert.False(coordinator.TryStartRecheck(out var current));

        Assert.Equal(checking.Generation, current.Generation);
        Assert.True(current.IsChecking);
    }

    [Fact]
    public void StaleCompletion_CannotOverwriteCurrentProbe()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var stale = coordinator.StartProbe();
        var current = coordinator.StartProbe();

        Assert.False(coordinator.TryApplyUnsupported(stale.Generation, "Unsupported.", out _));
        Assert.True(coordinator.TryApplyAvailable(current.Generation, out var available));

        Assert.True(available.IsAvailable);
        Assert.True(coordinator.Current.IsAvailable);
    }

    [Fact]
    public void CancelledProbe_CannotOverwriteNextProbe()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var cancelled = coordinator.StartProbe();
        coordinator.CancelCurrent();
        var current = coordinator.StartProbe();

        Assert.False(coordinator.TryApplyProbeFailure(cancelled.Generation, "Cancelled.", out _));
        Assert.True(coordinator.TryApplyAvailable(current.Generation, out var available));

        Assert.True(available.IsAvailable);
    }

    [Fact]
    public void ConfirmedUnsupported_RemainsDefinitiveAndNotRetryable()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var checking = coordinator.StartProbe();

        Assert.True(coordinator.TryApplyUnsupported(
            checking.Generation,
            "No qualified NVIDIA GPU was detected.",
            out var unsupported));

        Assert.True(unsupported.IsUnsupported);
        Assert.False(unsupported.CanRecheck);
        Assert.Equal("No qualified NVIDIA GPU was detected.", unsupported.Reason);
    }

    [Fact]
    public void RecheckAfterConfirmedUnsupported_DoesNotStartProbe()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var checking = coordinator.StartProbe();
        Assert.True(coordinator.TryApplyUnsupported(
            checking.Generation,
            "No qualified NVIDIA GPU was detected.",
            out var unsupported));

        Assert.False(coordinator.TryStartRecheck(out var current));

        Assert.Equal(unsupported.Generation, current.Generation);
        Assert.True(current.IsUnsupported);
        Assert.False(coordinator.Current.IsChecking);
    }
}
