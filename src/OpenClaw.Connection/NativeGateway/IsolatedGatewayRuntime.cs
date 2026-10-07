using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
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
    private readonly TimeSpan _inspectionTimeout;
    private readonly TimeProvider _timeProvider;
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan StartupConfirmationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, VerifiedGateway> _proofs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ownedStarts = new(StringComparer.Ordinal);
    private bool _disposed;
    private sealed record VerifiedGateway(GatewayRecord Record, NativeGatewayPackage Package, IsolatedGatewayStatus Status);

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
        Func<string, bool> aliasExists,
        TimeSpan? inspectionTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _resolver = resolver;
        _client = client;
        _capture = capture;
        _captureSequences = captureSequences;
        _aliasExists = aliasExists;
        _inspectionTimeout = inspectionTimeout ?? TimeSpan.FromSeconds(5);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        Uri endpoint = RequireRecord(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureRunningCoreAsync(record, endpoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureRunningCoreAsync(GatewayRecord record, Uri endpoint, CancellationToken cancellationToken)
    {
        NativeGatewayPackage? startedThisCall = null;
        _proofs.Remove(record.Id);
        try
        {
            NativeGatewayPackage package = await ResolveAsync(record, cancellationToken).ConfigureAwait(false);
            IsolatedGatewayStatus status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            status = await RepairConfigurationAsync(
                record, endpoint, package, status, cancellationToken).ConfigureAwait(false);
            if (status.State is "not-started" or "stopped")
            {
                var conflict = InspectSnapshot(endpoint.Port, null);
                if (conflict.Kind != GatewayEndpointProvenanceKind.NoListener)
                    throw new NativeGatewayListenerException(conflict);
                startedThisCall = package;
                _ownedStarts.Add(package.PackageFamilyName);
                status = await StartAndWaitAsync(package, endpoint.Port, cancellationToken).ConfigureAwait(false);
            }
            if (status.State != "running" || status.Port != endpoint.Port)
                throw new NativeGatewayContractException(
                    "The package-managed Gateway is not running on Companion's selected port. " +
                    "Check clawctl gateway-service status before connecting.");

            var provenance = InspectSnapshot(endpoint.Port, status);
            if (provenance.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
                throw new NativeGatewayListenerException(provenance);
            cancellationToken.ThrowIfCancellationRequested();
            _proofs[record.Id] = new(record, package, status);
        }
        catch
        {
            if (startedThisCall is not null)
            {
                try
                {
                    await _client.StopAsync(startedThisCall, CancellationToken.None).ConfigureAwait(false);
                    _ownedStarts.Remove(startedThisCall.PackageFamilyName);
                }
                catch (Exception cleanupFailure) when (IsInspectionFailure(cleanupFailure))
                {
                    Trace.TraceError($"Isolated Gateway start rollback failed ({cleanupFailure.GetType().Name}); stop ownership retained.");
                }
            }
            throw;
        }
    }

    private async Task<IsolatedGatewayStatus> RepairConfigurationAsync(
        GatewayRecord record,
        Uri endpoint,
        NativeGatewayPackage package,
        IsolatedGatewayStatus status,
        CancellationToken cancellationToken)
    {
        bool staleSession = status.SessionState == "stale";
        bool missingConfiguration = status.SessionState == "running" &&
            status.ReadinessState == "absent" &&
            status.ReadinessReason == "config-file-missing";
        if (!staleSession && !missingConfiguration)
            return status;

        if (string.IsNullOrEmpty(record.SharedGatewayToken))
            throw new NativeGatewayContractException(
                "Companion cannot restore the package-managed Gateway because its saved token is missing. " +
                "Remove this connection and add the local native Gateway again.");
        if (staleSession)
            await _client.SetupAsync(package, cancellationToken).ConfigureAwait(false);

        // A superseded attempt must not reach the package's persistent credential write.
        cancellationToken.ThrowIfCancellationRequested();
        IsolatedGatewayConfiguration restored = await _client.RestoreAsync(
            package, endpoint.Port, record.SharedGatewayToken, cancellationToken).ConfigureAwait(false);
        if (restored.Port != endpoint.Port ||
            !string.Equals(restored.Token, record.SharedGatewayToken, StringComparison.Ordinal))
        {
            throw new NativeGatewayContractException(
                "The Gateway package did not restore Companion's saved endpoint credential. " +
                "Remove this connection and add the local native Gateway again.");
        }
        return await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IsolatedGatewayStatus> StartAndWaitAsync(
        NativeGatewayPackage package, int port, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(StartupTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // A failed/ambiguous start response is not a pending-start acknowledgement.
            // In particular, older packages omit the lifecycle state on exit 1.
            await _client.StartAsync(package, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            // A late successful acknowledgement still gets one bounded status window.
            // This can extend the total startup ceiling by at most ten seconds.
            var remaining = StartupTimeout - _timeProvider.GetElapsedTime(startedAt);
            deadline.CancelAfter(remaining > StartupConfirmationTimeout ? remaining : StartupConfirmationTimeout);
            while (true)
            {
                var status = await _client.StatusAsync(package, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (status.State is not ("starting" or "unhealthy"))
                    return status;

                // Only this call's acknowledged start may wait without listener proof.
                // Recheck a transition racing the status snapshot once. Only a fresh
                // Running attestation may proceed to the full listener identity checks.
                var provenance = InspectSnapshot(port, null);
                if (provenance.Kind != GatewayEndpointProvenanceKind.NoListener)
                {
                    var confirmation = await _client.StatusAsync(package, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    if (confirmation.State == "running")
                        return confirmation;
                    throw new NativeGatewayListenerException(provenance);
                }
                await Task.Delay(StartupPollInterval, _timeProvider, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new NativeGatewayStartupTimeoutException(error);
        }
        catch (TimeoutException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NativeGatewayStartupTimeoutException(error);
        }
    }

    public async Task<GatewayEndpointProvenance> InspectAsync(
        GatewayRecord record, CancellationToken cancellationToken)
    {
        Uri endpoint = RequireRecord(record);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_inspectionTimeout);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            _proofs.Remove(record.Id);
            NativeGatewayPackage package = await ResolveAsync(record, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            IsolatedGatewayStatus status = await _client.StatusAsync(package, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (status.State == "unknown")
            {
                Trace.TraceWarning("The Gateway package could not establish its service state. Credentials were not sent.");
                return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                    Detail: "The Gateway package could not inspect its service. Check the package and retry. Credentials were not sent.",
                    FailureReason: GatewayEndpointProvenanceFailureReason.InspectionUnavailable);
            }
            if (status.State != "running" || status.Port != endpoint.Port)
            {
                return InspectSnapshot(endpoint.Port, null);
            }
            var provenance = InspectSnapshot(endpoint.Port, status);
            deadline.Token.ThrowIfCancellationRequested();
            if (provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway)
                _proofs[record.Id] = new(record, package, status);
            return provenance;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning("Isolated Gateway ownership inspection timed out. Credentials were not sent.");
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: entered
                    ? "Gateway ownership inspection timed out. Retry after checking the package. Credentials were not sent."
                    : "Gateway lifecycle is busy. Retry after it finishes. Credentials were not sent.",
                FailureReason: GatewayEndpointProvenanceFailureReason.InspectionUnavailable);
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            Trace.TraceWarning($"Isolated Gateway ownership inspection failed ({ex.GetType().Name}). Credentials were not sent.");
            return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                Detail: "Gateway ownership inspection failed. Check the installed Gateway package and retry. Credentials were not sent.",
                FailureReason: GatewayEndpointProvenanceFailureReason.InspectionUnavailable);
        }
        finally
        {
            if (entered)
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
            return _proofs.TryGetValue(record.Id, out var proof) &&
                proof.Record.NativePackageFamilyName == record.NativePackageFamilyName &&
                GatewayRecordEditing.AreEquivalentLoopbackEndpoints(proof.Record.Url, record.Url) &&
                _aliasExists(proof.Package.ClawCtlAliasPath)
                ? InspectSnapshot(endpoint.Port, proof.Status)
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
            _proofs.Clear();
            await StopOwnedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        Uri endpoint = RequireRecord(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeGatewayPackage package = await ResolveAsync(record, cancellationToken).ConfigureAwait(false);
            IsolatedGatewayStatus status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            var provenance = InspectSnapshot(endpoint.Port,
                status.State == "running" && status.Port == endpoint.Port ? status : null);
            if (status.State == "running" && status.Port != endpoint.Port ||
                provenance.Kind is not (GatewayEndpointProvenanceKind.ExpectedManagedGateway or GatewayEndpointProvenanceKind.NoListener))
                throw new NativeGatewayListenerException(provenance);

            bool owned = _ownedStarts.Contains(package.PackageFamilyName);
            _proofs.Remove(record.Id);
            await _client.StopAsync(package, cancellationToken).ConfigureAwait(false);
            status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            if (status.State is not ("stopped" or "not-started"))
                throw new NativeGatewayContractException(
                    "The Gateway package has not confirmed that its service stopped. Retry restart after checking its status.");
            _ownedStarts.Remove(package.PackageFamilyName);
            await EnsureRunningCoreAsync(record, endpoint, cancellationToken).ConfigureAwait(false);
            // An explicit restart must not turn a pre-existing service into a detach-owned start.
            if (owned)
                _ownedStarts.Add(package.PackageFamilyName);
            else
                _ownedStarts.Remove(package.PackageFamilyName);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopOwnedAsync(CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        foreach (string family in _ownedStarts.ToArray())
        {
            try
            {
                NativeGatewayPackage package = await _resolver.ResolveAsync(family, cancellationToken).ConfigureAwait(false);
                NativeGatewayPaths.ValidatePackage(package, family);
                await _client.StopAsync(package, cancellationToken).ConfigureAwait(false);
                _ownedStarts.Remove(family);
            }
            catch (Exception ex) when (IsInspectionFailure(ex))
            {
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("One or more Companion-owned Gateways could not be stopped.", failures);
    }

    private static bool IsInspectionFailure(Exception ex) =>
        ex is InvalidOperationException or IOException or Win32Exception or COMException or UnauthorizedAccessException or TimeoutException;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _proofs.Clear();
            await StopOwnedAsync(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
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
                "In Companion, open Connections, select this old profile and choose Remove, then choose Add Gateway > Install a local native Gateway. " +
                "This explicitly creates a new isolated profile instead of sending the old profile's credentials to the package.");
        _active = legacy;
        await legacy.EnsureRunningAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public Task<GatewayEndpointProvenance> InspectAsync(
        GatewayRecord record, CancellationToken cancellationToken) =>
        record.NativeRuntimeContract == NativeGatewayPackageClient.IsolatedContract
            ? isolated.InspectAsync(record, cancellationToken)
            : record.NativeRuntimeContract is null && ReferenceEquals(_active, legacy)
                ? legacy.InspectAsync(record, cancellationToken)
                : Task.FromResult(Unestablished(record));

    public GatewayEndpointProvenance Inspect(GatewayRecord record) =>
        record.NativeRuntimeContract == NativeGatewayPackageClient.IsolatedContract
            ? isolated.Inspect(record)
            : record.NativeRuntimeContract is null && ReferenceEquals(_active, legacy)
                ? legacy.Inspect(record)
                : Unestablished(record);

    public async Task RestartAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        if (record.NativeRuntimeContract == NativeGatewayPackageClient.IsolatedContract)
        {
            _active = isolated;
            await isolated.RestartAsync(record, cancellationToken).ConfigureAwait(false);
            return;
        }
        await EnsureRunningAsync(record, cancellationToken).ConfigureAwait(false);
        await legacy.RestartAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(legacy.StopAsync(cancellationToken), isolated.StopAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        try { await legacy.DisposeAsync().ConfigureAwait(false); }
        finally { await isolated.DisposeAsync().ConfigureAwait(false); }
    }

    private static GatewayEndpointProvenance Unestablished(GatewayRecord record) =>
        new(GatewayEndpointProvenanceKind.UnknownListener, NativeGatewayPaths.ValidateRecord(record).Port,
            Detail: "Gateway ownership has not been established for this profile. Credentials were not sent.");
}
