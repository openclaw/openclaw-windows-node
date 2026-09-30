using System.Net;
using OpenClaw.Shared;

namespace OpenClaw.Connection.NativeGateway;

/// <summary>Uses the package's isolated-session lifecycle and checks its live listener attribution.</summary>
public sealed class IsolatedGatewayRuntime : INativeGatewayRuntime
{
    private readonly INativeGatewayPackageResolver _resolver;
    private readonly NativeGatewayPackageClient _client;
    private readonly Func<WindowsTcpListenerSnapshotResult> _capture;
    private readonly Func<IReadOnlyDictionary<int, ulong>> _captureSequences;
    private readonly Func<string, bool> _aliasExists;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GatewayRecord? _record;
    private NativeGatewayPackage? _package;
    private IsolatedGatewayStatus? _proof;
    private bool _startedHere;
    private bool _disposed;

    public IsolatedGatewayRuntime(INativeGatewayPackageResolver resolver)
        : this(resolver, new NativeGatewayPackageClient(), WindowsTcpListenerSnapshot.Capture,
            WindowsProcessSequenceSnapshot.Capture, File.Exists)
    {
    }

    internal IsolatedGatewayRuntime(
        INativeGatewayPackageResolver resolver,
        NativeGatewayPackageClient client,
        Func<WindowsTcpListenerSnapshotResult> capture,
        Func<IReadOnlyDictionary<int, ulong>> captureSequences,
        Func<string, bool> aliasExists)
    {
        _resolver = resolver;
        _client = client;
        _capture = capture;
        _captureSequences = captureSequences;
        _aliasExists = aliasExists;
    }

    public async Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        Uri endpoint = RequireRecord(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeGatewayPackage package = await ResolveAsync(record, cancellationToken).ConfigureAwait(false);
            _package = package;
            IsolatedGatewayStatus status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            if (status.State is "not-started" or "stopped")
            {
                _proof = null;
                var conflict = InspectSnapshot(endpoint.Port, null);
                if (conflict.Kind != GatewayEndpointProvenanceKind.NoListener)
                    throw new NativeGatewayListenerException(conflict);
                _startedHere = true;
                await _client.StartAsync(package, cancellationToken).ConfigureAwait(false);
                status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            }
            if (status.State != "running" || status.Port != endpoint.Port)
                throw new NativeGatewayContractException(
                    "The package-managed Gateway is not running on Companion's selected port. " +
                    "Check clawctl gateway-service status before connecting.");

            var provenance = InspectSnapshot(endpoint.Port, status);
            if (provenance.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
                throw new NativeGatewayListenerException(provenance);
            _record = record;
            _proof = status;
        }
        catch
        {
            _proof = null;
            if (_startedHere && _package is not null)
            {
                await _client.StopAsync(_package, CancellationToken.None).ConfigureAwait(false);
                _startedHere = false;
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GatewayEndpointProvenance> InspectAsync(
        GatewayRecord record, CancellationToken cancellationToken)
    {
        Uri endpoint = RequireRecord(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeGatewayPackage package = await ResolveAsync(record, cancellationToken).ConfigureAwait(false);
            IsolatedGatewayStatus status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            if (status.State != "running" || status.Port != endpoint.Port)
            {
                _proof = null;
                return InspectSnapshot(endpoint.Port, null);
            }
            var provenance = InspectSnapshot(endpoint.Port, status);
            _proof = provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway ? status : null;
            _record = record;
            _package = package;
            return provenance;
        }
        finally
        {
            _gate.Release();
        }
    }

    public GatewayEndpointProvenance Inspect(GatewayRecord record)
    {
        Uri endpoint = RequireRecord(record);
        if (!_gate.Wait(0))
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "Gateway lifecycle is busy. Credentials were not sent.");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _record?.Id == record.Id &&
                _record.NativePackageFamilyName == record.NativePackageFamilyName &&
                GatewayRecordEditing.AreEquivalentLoopbackEndpoints(_record.Url, record.Url) &&
                _package is not null && _aliasExists(_package.ClawCtlAliasPath)
                ? InspectSnapshot(endpoint.Port, _proof)
                : new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                    Detail: "No verified Gateway package listener is recorded for this Companion.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _proof = null;
            if (_startedHere && _package is not null)
            {
                NativeGatewayPackage package = await _resolver.ResolveAsync(
                    _package.PackageFamilyName, cancellationToken).ConfigureAwait(false);
                await _client.StopAsync(package, cancellationToken).ConfigureAwait(false);
                _startedHere = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _proof = null;
            if (_startedHere && _package is not null)
            {
                NativeGatewayPackage package = await _resolver.ResolveAsync(
                    _package.PackageFamilyName, CancellationToken.None).ConfigureAwait(false);
                await _client.StopAsync(package, CancellationToken.None).ConfigureAwait(false);
                _startedHere = false;
            }
            _record = null;
            _package = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeGatewayPackage> ResolveAsync(
        GatewayRecord record, CancellationToken cancellationToken)
    {
        NativeGatewayPackage package = await _resolver.ResolveAsync(
            record.NativePackageFamilyName!, cancellationToken).ConfigureAwait(false);
        NativeGatewayPaths.ValidatePackage(package, record.NativePackageFamilyName!);
        return package;
    }

    private static Uri RequireRecord(GatewayRecord record)
    {
        Uri endpoint = NativeGatewayPaths.ValidateRecord(record);
        if (record.NativeRuntimeContract != NativeGatewayPackageClient.IsolatedContract)
            throw new NativeGatewayContractException(
                "This Gateway profile does not use the isolated-session contract. Reconfigure it in Companion.");
        return endpoint;
    }

    private GatewayEndpointProvenance InspectSnapshot(int port, IsolatedGatewayStatus? status)
    {
        WindowsTcpListenerSnapshotResult first = _capture();
        if (!first.Ipv4Complete || !first.Ipv6Complete)
            return Unknown("The Gateway listener snapshot is incomplete.");
        WindowsTcpListenerInfo[] listeners = first.Listeners.Where(listener => listener.Port == port).ToArray();
        if (listeners.Length == 0)
            return status is null
                ? new(GatewayEndpointProvenanceKind.NoListener, port)
                : Unknown("The package-managed Gateway is not listening on its verified port.");
        if (status?.AgentUserSid is not { Length: > 0 })
            return Unknown("A listener exists, but the package did not attest to it.");
        IReadOnlyDictionary<int, ulong> firstSequences;
        try
        {
            firstSequences = _captureSequences();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            return Unknown(ex.Message, GatewayEndpointProvenanceFailureReason.ProcessIdentityUnavailable);
        }
        if (listeners.Any(listener => !IPAddress.IsLoopback(listener.Address) ||
            !firstSequences.TryGetValue(listener.ProcessId, out ulong sequence) ||
            !status.Listeners.Any(owned => owned.Port == port &&
                owned.ProcessId == listener.ProcessId && owned.SequenceNumber == sequence)))
            return Unknown("The listener is not owned by the verified isolated Gateway session.");
        WindowsTcpListenerSnapshotResult second = _capture();
        WindowsTcpListenerInfo[] current = second.Listeners.Where(listener => listener.Port == port).ToArray();
        IReadOnlyDictionary<int, ulong> secondSequences;
        try
        {
            secondSequences = _captureSequences();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            return Unknown(ex.Message, GatewayEndpointProvenanceFailureReason.ProcessIdentityUnavailable);
        }
        if (!second.Ipv4Complete || !second.Ipv6Complete || current.Length != listeners.Length ||
            !listeners.All(listener => current.Any(now => now.Address.Equals(listener.Address) &&
                now.ProcessId == listener.ProcessId)) ||
            current.Any(listener => !secondSequences.TryGetValue(listener.ProcessId, out ulong sequence) ||
                !firstSequences.TryGetValue(listener.ProcessId, out ulong initial) ||
                initial != sequence))
            return Unknown("The Gateway listener changed during ownership verification.");
        WindowsTcpListenerInfo owner = listeners.First();
        IsolatedGatewayListener identity = status.Listeners.First(listener =>
            listener.Port == port && listener.ProcessId == owner.ProcessId);
        return new(GatewayEndpointProvenanceKind.ExpectedManagedGateway, port,
            ProcessId: owner.ProcessId,
            ProcessStartTimeUtc: identity.ProcessStartTimeUtc,
            Detail: "Verified listener in the package-managed isolated session.");

        GatewayEndpointProvenance Unknown(
            string detail,
            GatewayEndpointProvenanceFailureReason reason = GatewayEndpointProvenanceFailureReason.None) =>
            new(GatewayEndpointProvenanceKind.UnknownListener, port, Detail: detail, FailureReason: reason);
    }

}

/// <summary>Retains the legacy host-owned runtime only for an explicitly detected legacy package.</summary>
public sealed class NativeGatewayRuntimeRouter(
    INativeGatewayRuntime legacy,
    INativeGatewayRuntime isolated,
    INativeGatewayPackageResolver resolver,
    NativeGatewayPackageClient client) : INativeGatewayRuntime
{
    private INativeGatewayRuntime? _active;

    public static NativeGatewayRuntimeRouter Create(
        GatewayRegistry registry, INativeGatewayPackageResolver resolver, IOpenClawLogger logger) =>
        new(new NativeGatewayRuntime(registry, resolver, logger),
            new IsolatedGatewayRuntime(resolver), resolver, new NativeGatewayPackageClient());

    public async Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        if (record.NativeRuntimeContract == NativeGatewayPackageClient.IsolatedContract)
        {
            _active = isolated;
            await isolated.EnsureRunningAsync(record, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (record.NativeRuntimeContract is not null)
            throw new InvalidOperationException("This Gateway record has an unsupported runtime contract.");
        NativeGatewayPackage package = await resolver.ResolveAsync(
            record.NativePackageFamilyName!, cancellationToken).ConfigureAwait(false);
        if (await client.DetectAsync(package, cancellationToken).ConfigureAwait(false) !=
            NativeGatewayContract.Legacy)
            throw new NativeGatewayContractException(
                "This Companion profile was created for a same-user Gateway. " +
                "Set up the isolated Gateway again instead of sending it this profile's credentials.");
        _active = legacy;
        await legacy.EnsureRunningAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public Task<GatewayEndpointProvenance> InspectAsync(
        GatewayRecord record, CancellationToken cancellationToken) =>
        Select(record).InspectAsync(record, cancellationToken);

    public GatewayEndpointProvenance Inspect(GatewayRecord record) => Select(record).Inspect(record);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _active?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await legacy.DisposeAsync().ConfigureAwait(false);
        await isolated.DisposeAsync().ConfigureAwait(false);
    }

    private INativeGatewayRuntime Select(GatewayRecord record) =>
        record.NativeRuntimeContract == NativeGatewayPackageClient.IsolatedContract
            ? isolated
            : record.NativeRuntimeContract is null && ReferenceEquals(_active, legacy)
                ? legacy
                : throw new InvalidOperationException("Gateway ownership has not been established for this profile.");
}
