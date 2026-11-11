using System.Diagnostics;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI;

/// <summary>Retains initial native AI reads on the preparation page until a single authenticated transfer.</summary>
public sealed class NativeAiPreparation : IAsyncDisposable
{
    private readonly GatewayAiPreparation _gateway;
    private readonly ISetupLocalAiHost? _host;
    private readonly IGatewayAiSetupTransport _transport;
    private readonly CancellationToken _cancellationToken;
    private LocalAiOnboardingObservation? _observation;
    private NativeSetupConsole? _console;
    private bool _nativeConfigured;
    private bool _released;

    private NativeAiPreparation(GatewayAiPreparation gateway, ISetupLocalAiHost? host, CancellationToken ct)
    {
        _gateway = gateway;
        _host = host;
        _transport = gateway.Transport;
        _cancellationToken = ct;
    }

    internal static async Task<NativeAiPreparation> PrepareAsync(
        GatewayAiPreparation gateway, ISetupLocalAiHost? host, GatewayRecord? isolatedRecord,
        Func<CancellationToken, Task>? authorize, CancellationToken lifetimeToken, CancellationToken ct)
    {
        NativeAiPreparation? preparation = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            preparation = new(gateway, host, ct);
            if (isolatedRecord is not null)
            {
                if (host is INativeSetupLocalAiHost native)
                {
                    native.ConfigureNative(isolatedRecord, preparation._transport,
                        authorize ?? throw new InvalidOperationException("The native setup authority is unavailable."));
                    preparation._nativeConfigured = true;
                }
                preparation._console = new();
                await preparation._console.StartAsync(preparation._transport, lifetimeToken, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (host is not null)
            {
                preparation._observation = new(host);
                await preparation._observation.RefreshAsync(null, ct).WaitAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            _ = gateway.Transport;
            return preparation;
        }
        catch
        {
            if (preparation is not null) await preparation.DisposeAsync();
            else await gateway.DisposeAsync();
            throw;
        }
    }

    internal ((IGatewayAiSetupTransport Transport, IAsyncDisposable Owner, GatewayAiSetupClient Client,
        GatewayAiDiscoveryFailure? DiscoveryFailure) Gateway, LocalAiOnboardingObservation? Observation,
        NativeSetupConsole? Console, bool NativeConfigured) Take()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_released) throw new SetupNativeOwnershipException();
        // Keep the engine's route, generation, connection and scope checks authoritative.
        var gateway = _gateway.Take();
        _released = true;
        return (gateway, _observation, _console, _nativeConfigured);
    }

    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        _console?.Dispose();
        try
        {
            if (_observation is not null) await _observation.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_nativeConfigured && _host is INativeSetupLocalAiHost native)
                    native.ReleaseNative(_transport);
            }
            finally { await _gateway.DisposeAsync(); }
        }
    }
}

/// <summary>One console cursor and bounded live buffer survive the preparation-to-choices handoff.</summary>
internal sealed class NativeSetupConsole : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<string> _output = new();
    private WizardConsoleTail? _tail;
    private GatewayLogTailIssue? _issue;
    private bool _closed;
    public event Action? Changed;

    public (string[] Output, GatewayLogTailIssue? Issue) Snapshot
    {
        get { lock (_sync) return (_output.ToArray(), _issue); }
    }

    public async Task StartAsync(IGatewayAiSetupTransport transport, CancellationToken lifetimeToken, CancellationToken ct)
    {
        var tail = new WizardConsoleTail(logger: NullLogger.Instance,
            gatewayLogTail: WizardConsoleTail.CreateGatewayLogReader(transport.RequestAsync));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _tail = tail;
        }
        using var cancellation = ct.Register(Dispose);
        try
        {
            ct.ThrowIfCancellationRequested();
            await tail.StartGatewayAsync(Append, ReportIssue, lifetimeToken).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || lifetimeToken.IsCancellationRequested)
        {
            tail.Dispose();
            Dispose();
            throw;
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Isolated setup console could not start ({0}).", error.GetType().Name);
            tail.Stop();
            ReportIssue(GatewayLogTailIssue.Unavailable);
        }
    }

    private void Append(string message)
    {
        lock (_sync)
        {
            if (_closed) return;
            _output.Enqueue(message);
            while (_output.Count > 40) _output.Dequeue();
        }
        Changed?.Invoke();
    }

    private void ReportIssue(GatewayLogTailIssue issue)
    {
        lock (_sync)
        {
            if (_closed || _issue == GatewayLogTailIssue.Skipped) return;
            _issue = issue;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            Changed = null;
            _tail?.Dispose();
            _tail = null;
        }
    }
}
