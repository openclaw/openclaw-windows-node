namespace OpenClaw.TestSupport.Gateway;

/// <summary>Host-side controls, never Gateway RPCs or production UI switches.</summary>
public sealed class FixturePlayback
{
    public static IReadOnlyList<string> Scenarios { get; } = Array.AsReadOnly(new[]
    {
        "streaming", "chat-deltas", "tools", "tool-error", "agent-activity", "question-choice", "question-text", "question-multi",
        "approval", "approval-deny-only", "slow", "send-error", "stream-error", "disconnect"
    });

    private readonly object _sync = new();
    private TaskCompletionSource _changed = NewSignal();
    private string _next = "streaming";
    private bool _paused;
    private int _steps;

    public void Select(string scenario)
    {
        if (!Scenarios.Contains(scenario, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown interaction. Choose: {string.Join(", ", Scenarios)}", nameof(scenario));
        lock (_sync) _next = scenario;
    }

    internal string TakeNext()
    {
        lock (_sync)
        {
            var next = _next;
            _next = "streaming";
            return next;
        }
    }

    public void Pause()
    {
        lock (_sync) { _paused = true; _steps = 0; }
    }

    public void Resume()
    {
        lock (_sync) { _paused = false; _steps = 0; Signal(); }
    }

    public void Step()
    {
        lock (_sync) { _paused = true; _steps++; Signal(); }
    }

    internal async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (!_paused) return;
                if (_steps > 0) { _steps--; return; }
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct);
        }
    }

    private void Signal()
    {
        var old = _changed;
        _changed = NewSignal();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
