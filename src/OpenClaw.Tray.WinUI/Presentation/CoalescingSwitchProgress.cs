using OpenClaw.Connection.LocalAi;

namespace OpenClawTray.Presentation;

/// <summary>
/// Bounds model-switch progress delivered to the UI thread. The installer reports after
/// every buffer read (thousands of times while hashing a multi-GB file); forwarding each
/// report would flood the dispatcher and freeze the window. Byte updates are delivered
/// at most once per <see cref="MinimumInterval"/>, phase changes immediately, and at most
/// one delivery is ever queued; the queued delivery always carries the latest value.
/// Reports may arrive on any thread; <paramref name="deliver"/> runs on the UI thread.
/// </summary>
internal sealed class CoalescingSwitchProgress(
    IUiDispatcher dispatcher,
    Action<LocalAiModelSwitchProgress> deliver,
    Func<long>? getTimestampMs = null) : IProgress<LocalAiModelSwitchProgress>
{
    internal const long MinimumInterval = 100;

    private readonly Lock _gate = new();
    private readonly Func<long> _now = getTimestampMs ?? (static () => Environment.TickCount64);
    private LocalAiModelSwitchProgress? _latest;
    private LocalAiModelSwitchPhase? _deliveredPhase;
    private long _lastDeliveredAt = long.MinValue;
    private bool _queued;

    public void Report(LocalAiModelSwitchProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _latest = value;
            if (_queued)
                return;
            bool phaseChanged = value.Phase != _deliveredPhase;
            if (!phaseChanged && _now() - _lastDeliveredAt < MinimumInterval)
                return;
            _queued = true;
        }

        if (!dispatcher.TryEnqueue(Flush))
        {
            lock (_gate)
                _queued = false;
        }
    }

    private void Flush()
    {
        LocalAiModelSwitchProgress value;
        lock (_gate)
        {
            _queued = false;
            value = _latest!;
            _deliveredPhase = value.Phase;
            _lastDeliveredAt = _now();
        }
        deliver(value);
    }
}
