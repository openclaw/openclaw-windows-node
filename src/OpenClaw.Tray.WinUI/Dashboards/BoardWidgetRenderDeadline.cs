// Owns the acknowledgement deadline for one delivered board document, independently
// of proxy startup and HTTP retries. All calls and scheduled callbacks use the UI thread.
// Usage: deadline.Start(renderId) immediately before delivery; TryComplete(renderId)
// accepts only that delivery. Cancel on host teardown; Dispose at owner disposal.
using OpenClaw.Shared.Dashboards;

namespace OpenClawTray.Dashboards;

/// <summary>A cancellable, delivery-scoped wait for sandbox-resource-loaded.</summary>
internal sealed class BoardWidgetRenderDeadline(
    Func<TimeSpan, Action, IDisposable> schedule, Action expired) : IDisposable
{
    private IDisposable? _timer;
    private string? _renderId;
    private int _generation;

    /// <summary>Gives every new delivery the full existing load budget, even after slow HTTP retries.</summary>
    public void Start(string renderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(renderId);
        Cancel();
        _renderId = renderId;
        var generation = _generation;
        _timer = schedule(BoardWidgetDocumentLoader.DefaultTimeout, () =>
        {
            // Stopping a dispatcher timer does not retract a callback already queued.
            if (generation != _generation || _renderId != renderId)
                return;
            Cancel();
            expired();
        });
    }

    /// <summary>Only the pending delivery can complete the wait, never a stale or late acknowledgement.</summary>
    public bool TryComplete(string renderId)
    {
        if (_renderId is null || !string.Equals(_renderId, renderId, StringComparison.Ordinal))
            return false;
        Cancel();
        return true;
    }

    /// <summary>Invalidates both the pending render identity and queued timeout callbacks.</summary>
    public void Cancel()
    {
        _generation++;
        _renderId = null;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Cancel();
}
