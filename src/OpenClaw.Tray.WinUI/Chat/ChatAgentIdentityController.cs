using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClawTray.Chat;

/// <summary>Mount-local selected-agent display metadata, independent of placement discovery.</summary>
internal sealed class ChatAgentIdentityController(
    IAgentIdentityGateway gateway, IUiDispatcher dispatcher) : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private string? _agentId;
    private long? _epoch;
    private int _generation;
    private bool _disposed;

    private AgentDisplayIdentity? _identity;
    internal AgentDisplayIdentity? Identity =>
        !_disposed && _epoch.HasValue && gateway.IdentityConnectionEpoch == _epoch && gateway.CanReadAgentIdentity
            ? _identity : null;
    internal long Revision { get; private set; }
    internal event Action? Changed;

    internal void ApplyInputs(ChatComposerInputs inputs)
    {
        if (_disposed) return;
        var agentId = inputs.CurrentThread.AgentId ?? "main";
        var epoch = inputs.ConnectionState == "connected" && gateway.CanReadAgentIdentity
            ? gateway.IdentityConnectionEpoch : null;
        if (_agentId == agentId && _epoch == epoch) return;
        _agentId = agentId;
        _epoch = epoch;
        var generation = ++_generation;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _identity = null;
        Notify();
        if (epoch is not { } current) return;
        _cancellation = new();
        _ = LoadAsync(agentId, current, generation, _cancellation.Token);
    }

    private async Task LoadAsync(string agentId, long epoch, int generation, CancellationToken token)
    {
        try
        {
            var identity = await gateway.GetAgentIdentityAsync(agentId, epoch, token).ConfigureAwait(false);
            if (!dispatcher.TryEnqueue(() =>
            {
                if (_disposed || token.IsCancellationRequested || generation != _generation ||
                    gateway.IdentityConnectionEpoch != epoch || identity.AgentId != _agentId)
                    return;
                _identity = identity;
                Notify();
            }))
                System.Diagnostics.Trace.TraceWarning("[chat] Agent identity dispatcher is unavailable.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"[chat] Agent identity lookup failed ({ex.GetType().Name}); retaining catalog fallback.");
        }
    }

    private void Notify()
    {
        Revision++;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
