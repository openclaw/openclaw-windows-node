using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Services;

public sealed class VoiceCaptureLeaseGateTests
{
    [Fact]
    public void Acquire_WhenOwned_ThrowsTypedBusyError()
    {
        var gate = new VoiceCaptureLeaseGate();
        using var lease = gate.Acquire(VoiceCaptureKind.WakeListening);

        var error = Assert.Throws<VoiceCaptureBusyException>(
            () => gate.Acquire(VoiceCaptureKind.PushToTalk));

        Assert.Equal(VoiceCaptureKind.WakeListening, error.ActiveCapture);
    }

    [Fact]
    public void Dispose_ReleasesOnceAndRaisesAvailability()
    {
        var gate = new VoiceCaptureLeaseGate();
        var available = 0;
        gate.Available += () => available++;
        var lease = gate.Acquire(VoiceCaptureKind.ListenOnce);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, available);
        using var next = gate.Acquire(VoiceCaptureKind.FixedDuration);
    }

    [Fact]
    public void SharedGate_EnforcesProcessWideCaptureOwnership()
    {
        var firstServiceGate = VoiceCaptureLeaseGate.Shared;
        var secondServiceGate = VoiceCaptureLeaseGate.Shared;
        using var lease = firstServiceGate.Acquire(VoiceCaptureKind.WakeListening);

        var error = Assert.Throws<VoiceCaptureBusyException>(
            () => secondServiceGate.Acquire(VoiceCaptureKind.FixedDuration));

        Assert.Equal(VoiceCaptureKind.WakeListening, error.ActiveCapture);
    }
}
