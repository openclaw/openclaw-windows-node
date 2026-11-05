namespace OpenClawTray.Services;

/// <summary>Retains playback mute intent independently of capture lifetime.
/// Capture registrations and playback leases never own or stop each other.</summary>
internal sealed class PlaybackMuteState : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Action<bool>> _captures = new();
    private bool _requested;
    private int _leases;
    private bool _disposed;

    public bool IsMuted
    {
        get { lock (_gate) return !_disposed && (_requested || _leases > 0); }
        set
        {
            lock (_gate)
            {
                if (_disposed) return;
                _requested = value;
                Apply();
            }
        }
    }

    // Callbacks only set AudioPipeline.IsMuted. Keeping that write under the
    // gate makes registration, release and unregister atomic with mute changes.
    public IDisposable RegisterCapture(Action<bool> setMuted)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            setMuted(_requested || _leases > 0);
            _captures.Add(setMuted);
            return new Release(() => { lock (_gate) _captures.Remove(setMuted); });
        }
    }

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ++_leases;
            Apply();
            return new Release(() =>
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    --_leases;
                    Apply();
                }
            });
        }
    }

    private void Apply()
    {
        var muted = _requested || _leases > 0;
        foreach (var capture in _captures) capture(muted);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _requested = false;
            _leases = 0;
            _captures.Clear();
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
