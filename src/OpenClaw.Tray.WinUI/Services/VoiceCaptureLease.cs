namespace OpenClawTray.Services;

public enum VoiceCaptureKind
{
    PushToTalk,
    VoiceChat,
    WakeListening,
    ListenOnce,
    FixedDuration
}

public sealed class VoiceCaptureBusyException : InvalidOperationException
{
    public VoiceCaptureBusyException(VoiceCaptureKind activeCapture)
        : base($"The microphone is busy with {activeCapture}.")
    {
        ActiveCapture = activeCapture;
    }

    public VoiceCaptureKind ActiveCapture { get; }
}

internal sealed class VoiceCaptureLeaseGate
{
    public static VoiceCaptureLeaseGate Shared { get; } = new();

    private readonly object _gate = new();
    private VoiceCaptureKind? _activeCapture;

    public event Action? Available;

    public IDisposable Acquire(VoiceCaptureKind capture)
    {
        lock (_gate)
        {
            if (_activeCapture is { } active)
                throw new VoiceCaptureBusyException(active);

            _activeCapture = capture;
            return new Lease(this, capture);
        }
    }

    private void Release(VoiceCaptureKind capture)
    {
        Action? available;
        lock (_gate)
        {
            if (_activeCapture != capture)
                return;

            _activeCapture = null;
            available = Available;
        }

        available?.Invoke();
    }

    private sealed class Lease : IDisposable
    {
        private VoiceCaptureLeaseGate? _owner;
        private readonly VoiceCaptureKind _capture;

        public Lease(VoiceCaptureLeaseGate owner, VoiceCaptureKind capture)
        {
            _owner = owner;
            _capture = capture;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_capture);
    }
}
