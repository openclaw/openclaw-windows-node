using OpenClaw.Shared;
using System.Diagnostics;
using System.Net;

namespace OpenClaw.Connection.NativeGateway;

internal sealed record NativeGatewayStartSpec(
    string ExecutablePath,
    string WorkingDirectory,
    int Port,
    IReadOnlyDictionary<string, string> Environment,
    string? PackageFamilyName = null);

internal interface INativeGatewayProcess : IAsyncDisposable
{
    bool HasExited { get; }
    bool Owns(WindowsTcpListenerInfo listener);
}

internal interface INativeGatewayProcessHost
{
    Task<INativeGatewayProcess> StartAsync(NativeGatewayStartSpec spec, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit, serialized supervision for an installed MSIX gateway. Listener ownership, not a
/// successful TCP connection, authorizes reuse. No timers, adoption, package acquisition, or
/// state/config mutation occur here.
/// </summary>
public sealed class NativeGatewayRuntime : INativeGatewayRuntime
{
    private readonly GatewayRegistry _registry;
    private readonly INativeGatewayPackageResolver _packageResolver;
    private readonly IOpenClawLogger _logger;
    private readonly INativeGatewayProcessHost _host;
    private readonly Func<WindowsTcpListenerSnapshotResult> _capture;
    private readonly TimeSpan _startupTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _startLock = new();
    private CancellationTokenSource? _starting;
    private long _stopGeneration;
    private INativeGatewayProcess? _process;
    private GatewayRecord? _record;
    private int _disposed;

    public NativeGatewayRuntime(
        GatewayRegistry registry,
        INativeGatewayPackageResolver packageResolver,
        IOpenClawLogger logger)
        : this(registry, packageResolver, logger, new WindowsNativeGatewayProcessHost(),
            WindowsTcpListenerSnapshot.Capture, TimeSpan.FromMinutes(2))
    {
    }

    internal NativeGatewayRuntime(
        GatewayRegistry registry,
        INativeGatewayPackageResolver packageResolver,
        IOpenClawLogger logger,
        INativeGatewayProcessHost host,
        Func<WindowsTcpListenerSnapshotResult> capture,
        TimeSpan startupTimeout)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host;
        _capture = capture;
        _startupTimeout = startupTimeout;
    }

    public async Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Read(ref _stopGeneration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            lock (_startLock)
            {
                if (generation != _stopGeneration)
                    throw new OperationCanceledException("Native gateway start was superseded by a stop request.");
                _starting = linked;
            }
            var endpoint = NativeGatewayPaths.ValidateRecord(record);
            if (!IsCurrent(record))
                await StopOwnedAsync().ConfigureAwait(false);

            // Resolve on every request, even idempotent reuse. Package removal or replacement
            // must not silently keep authorizing a listener under stale package metadata.
            var package = await _packageResolver.ResolveAsync(record.NativePackageFamilyName!, linked.Token).ConfigureAwait(false);
            NativeGatewayPaths.ValidatePackage(package, record.NativePackageFamilyName!);
            linked.Token.ThrowIfCancellationRequested();
            if (_process is { HasExited: false } &&
                InspectCore(record, endpoint).Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway)
            {
                return;
            }

            await StopOwnedAsync().ConfigureAwait(false);
            var beforeStart = InspectCore(record, endpoint);
            if (beforeStart.Kind != GatewayEndpointProvenanceKind.NoListener)
                throw new NativeGatewayListenerException(beforeStart);
            var directory = NativeGatewayPaths.GetStateDirectory(_registry, record.Id);
            if (!Directory.Exists(directory) || !File.Exists(NativeGatewayPaths.GetConfigPath(_registry, record.Id)))
                throw new InvalidOperationException("Native gateway setup must create its state and configuration before starting.");

            var environment = NativeGatewayPaths.GetEnvironment(_registry, record.Id, _packageResolver.ResolveDataPath);
            _process = await _host.StartAsync(new NativeGatewayStartSpec(
                package.OpenClawAliasPath, environment["OPENCLAW_STATE_DIR"], endpoint.Port,
                environment, package.PackageFamilyName), linked.Token).ConfigureAwait(false);
            _record = record;
            var started = Stopwatch.StartNew();
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (_process.HasExited)
                    throw new InvalidOperationException("The native gateway exited before its owned listener became ready.");
                var provenance = InspectCore(record, endpoint);
                if (provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway)
                {
                    _logger.Info("Native gateway started with verified process ownership.");
                    return;
                }
                if (provenance.Kind != GatewayEndpointProvenanceKind.NoListener)
                    throw new NativeGatewayListenerException(provenance);
                if (started.Elapsed >= _startupTimeout)
                    throw new TimeoutException("The native gateway did not open its owned loopback listener in time.");
                await Task.Delay(TimeSpan.FromMilliseconds(100), linked.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Cleanup is intentionally not cancellable: cancellation must not orphan a child.
            await StopOwnedAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_startLock)
                _starting = null;
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_startLock)
        {
            Interlocked.Increment(ref _stopGeneration);
            _starting?.Cancel();
        }
        // Once accepted, stopping is non-cancellable so cancellation while waiting for the
        // serialized operation cannot leave a previously running child behind.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopOwnedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GatewayEndpointProvenance> InspectAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return InspectCore(record, NativeGatewayPaths.ValidateRecord(record));
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsCurrent(GatewayRecord record) =>
        _record is not null && string.Equals(_record.Id, record.Id, StringComparison.Ordinal) &&
        string.Equals(_record.NativePackageFamilyName, record.NativePackageFamilyName, StringComparison.Ordinal) &&
        GatewayRecordEditing.AreEquivalentLoopbackEndpoints(_record.Url, record.Url);

    private GatewayEndpointProvenance InspectCore(GatewayRecord record, Uri endpoint)
    {
        var first = _capture();
        if (!first.Ipv4Complete || !first.Ipv6Complete)
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "The native listener snapshot is incomplete.");
        var listeners = first.Listeners.Where(l => l.Port == endpoint.Port).ToArray();
        if (listeners.Length == 0)
            return new(GatewayEndpointProvenanceKind.NoListener, endpoint.Port);
        if (!IsCurrent(record) || _process is null || _process.HasExited)
        {
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "There is no active native gateway lifecycle job for this record.");
        }
        if (listeners.Any(l => !IPAddress.IsLoopback(l.Address)))
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "The native gateway port has a non-loopback listener.");
        if (listeners.Any(l => !_process.Owns(l)))
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "The listener is not a verified workload of the owned Gateway package launcher.");
        if (!listeners.Any(l => MatchesEndpoint(l.Address, endpoint)))
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "The native gateway is not listening on the requested loopback address.");

        // Snapshot again after membership/lifetime checks to reject observed PID recycling or
        // listener replacement. No cached PIDs or executable names confer trust.
        var second = _capture();
        var current = second.Listeners.Where(l => l.Port == endpoint.Port).ToArray();
        if (!second.Ipv4Complete || !second.Ipv6Complete || listeners.Length != current.Length ||
            !listeners.All(l => current.Any(c => c.Address.Equals(l.Address) &&
                c.ProcessId == l.ProcessId && c.ProcessStartTimeUtc == l.ProcessStartTimeUtc)) ||
            _process.HasExited || current.Any(l => !_process.Owns(l)))
        {
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "The native listener changed during ownership verification.",
                FailureReason: GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged);
        }
        var owner = listeners.First(l => MatchesEndpoint(l.Address, endpoint));
        return new(GatewayEndpointProvenanceKind.ExpectedManagedGateway, endpoint.Port,
            ProcessId: owner.ProcessId, ProcessStartTimeUtc: owner.ProcessStartTimeUtc,
            Detail: "Verified native gateway package workload.");
    }

    private static bool MatchesEndpoint(IPAddress address, Uri endpoint) =>
        string.Equals(endpoint.DnsSafeHost.TrimEnd('.'), "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.IsLoopback(address)
            : IPAddress.TryParse(endpoint.DnsSafeHost, out var expected) && expected.Equals(address);

    private async Task StopOwnedAsync()
    {
        var process = _process;
        _process = null;
        _record = null;
        if (process is not null)
            await process.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopOwnedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
