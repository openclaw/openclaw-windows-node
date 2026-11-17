using System;
using System.Threading.Tasks;
using OpenClaw.Shared.Cron;

namespace OpenClawTray.Automations;

// Hands one "show this automation" request from the WinUI page to the mounted Reactor root.
// The Hub search handoff can arrive before the root's first effect has attached, so a request
// made with no root waits there; a newer request replaces it. UI thread only.
//
// Usage:
//   var reveals = new AutomationsRevealChannel();                    // page, once per mount
//   using var attached = reveals.Attach(id => RevealInInventory(id)); // root, from an effect
//   var outcome = await reveals.RevealAsync(jobId);                  // page, from the search handoff
internal sealed class AutomationsRevealChannel : IDisposable
{
    private Func<string, Task<AutomationRevealOutcome>>? _handler;
    private (string JobId, TaskCompletionSource<AutomationRevealOutcome> Result)? _pending;
    private bool _disposed;

    /// <summary>
    /// Asks the attached root to reveal <paramref name="jobId"/>. With no root yet the request waits
    /// for one; it resolves <see cref="AutomationRevealOutcome.Superseded"/> when a newer request or
    /// <see cref="Dispose"/> replaces it.
    /// </summary>
    public Task<AutomationRevealOutcome> RevealAsync(string jobId)
    {
        if (_disposed) return Task.FromResult(AutomationRevealOutcome.Superseded);
        if (_handler is { } handler) return handler(jobId);
        _pending?.Result.TrySetResult(AutomationRevealOutcome.Superseded);
        var result = new TaskCompletionSource<AutomationRevealOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = (jobId, result);
        return result.Task;
    }

    /// <summary>Serves requests with <paramref name="handler"/> until the returned registration is disposed, starting with a waiting one.</summary>
    public IDisposable Attach(Func<string, Task<AutomationRevealOutcome>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_disposed) return Registration.None;
        _handler = handler;
        if (_pending is { } waiting)
        {
            _pending = null;
            _ = ServeAsync(handler, waiting.JobId, waiting.Result);
        }
        return new Registration(() =>
        {
            if (ReferenceEquals(_handler, handler)) _handler = null;
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _handler = null;
        _pending?.Result.TrySetResult(AutomationRevealOutcome.Superseded);
        _pending = null;
    }

    private static async Task ServeAsync(
        Func<string, Task<AutomationRevealOutcome>> handler, string jobId, TaskCompletionSource<AutomationRevealOutcome> result)
    {
        try { result.TrySetResult(await handler(jobId)); }
        catch (Exception ex) { result.TrySetException(ex); }
    }

    private sealed class Registration(Action release) : IDisposable
    {
        public static readonly IDisposable None = new Registration(static () => { });
        private Action? _release = release;

        public void Dispose()
        {
            _release?.Invoke();
            _release = null;
        }
    }
}
