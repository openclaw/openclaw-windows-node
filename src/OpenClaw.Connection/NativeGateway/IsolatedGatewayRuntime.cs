using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private readonly Func<Uri, TimeSpan, CancellationToken, Task<GatewayProtocolProbeResult>> _probeGatewayProtocol;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _inspectionTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly IOpenClawLogger _logger;

    /// <summary>
    /// Whole seconds; read once at process start. Invalid or out-of-range values warn and keep the default.
    /// Kept under the log sanitizer's long-token redaction length so the name stays readable in logs.
    /// </summary>
    public const string StartupTimeoutEnvironmentVariable = "OPENCLAW_NATIVE_GATEWAY_START_TIMEOUT";
    /// <summary>
    /// Includes the start command. Slow ARM64/x64 hosts reached a verified listener after about
    /// 210 seconds (#1969); the outer setup and credential handoff deadlines derive from this value.
    /// </summary>
    internal const int DefaultStartupTimeoutSeconds = 300;
    internal const int MinimumStartupTimeoutSeconds = 30;
    internal const int MaximumStartupTimeoutSeconds = 3600;
    internal static readonly TimeSpan StartupTimeout =
        ResolveStartupTimeout(Environment.GetEnvironmentVariable(StartupTimeoutEnvironmentVariable));
    internal static readonly TimeSpan StartupConfirmationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Whole seconds; read once at process start. Invalid or out-of-range values warn and keep the default.</summary>
    public const string InspectionTimeoutEnvironmentVariable = "OPENCLAW_NATIVE_GATEWAY_INSPECT_TIMEOUT";
    /// <summary>
    /// Bounds lifecycle-gate wait, package resolution and one package status read for ownership proof.
    /// A running ARM64 Gateway's status read took over 11 seconds, so five seconds denied a verified listener.
    /// The default stays inside the credential handoff's 20-second inspection and handshake allowance.
    /// </summary>
    internal const int DefaultInspectionTimeoutSeconds = 15;
    internal const int MinimumInspectionTimeoutSeconds = 5;
    internal const int MaximumInspectionTimeoutSeconds = 120;
    internal static readonly TimeSpan InspectionTimeout =
        ResolveInspectionTimeout(Environment.GetEnvironmentVariable(InspectionTimeoutEnvironmentVariable));
    /// <summary>One finite allowance for native preparation, listener proof and operator admission.</summary>
    public static TimeSpan AdmissionTimeout => NativeGatewayEndpointSecurity.CredentialHandoffTimeout;
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, VerifiedGateway> _proofs = new(StringComparer.Ordinal);
    // Null retains shipping v1's legacy lifecycle behavior. Receipt-capable starts
    // retain the caller-selected ID before dispatch, including a lost acknowledgement.
    private readonly Dictionary<string, string?> _ownedStarts = new(StringComparer.Ordinal);
    // Owned starts not yet verified running. A completed start keeps detach ownership in
    // _ownedStarts, but a later external restart of it is not Companion's pending lifecycle.
    private readonly HashSet<string> _pendingOwnedStarts = new(StringComparer.Ordinal);
    // External starts whose observation already spent a full startup budget while still starting.
    private readonly HashSet<string> _expiredExternalStarts = new(StringComparer.Ordinal);
    private bool _disposed;
    private sealed record VerifiedGateway(GatewayRecord Record, NativeGatewayPackage Package, IsolatedGatewayStatus Status);

    public IsolatedGatewayRuntime(INativeGatewayPackageResolver resolver, IOpenClawLogger? logger = null)
        : this(resolver, new NativeGatewayPackageClient(logger), WindowsTcpListenerSnapshot.Capture,
            WindowsProcessSequenceSnapshot.Capture, File.Exists, logger: logger)
    {
    }

    internal IsolatedGatewayRuntime(
        INativeGatewayPackageResolver resolver,
        NativeGatewayPackageClient client,
        Func<WindowsTcpListenerSnapshotResult> capture,
        Func<IReadOnlyDictionary<int, ulong>> captureSequences,
        Func<string, bool> aliasExists,
        TimeSpan? inspectionTimeout = null,
        TimeProvider? timeProvider = null,
        IOpenClawLogger? logger = null,
        Func<Uri, TimeSpan, CancellationToken, Task<GatewayProtocolProbeResult>>? probeGatewayProtocol = null)
    {
        _resolver = resolver;
        _client = client;
        _capture = capture;
        _captureSequences = captureSequences;
        _aliasExists = aliasExists;
        _probeGatewayProtocol = probeGatewayProtocol ?? GatewayProtocolProbe.ProbeAsync;
        _inspectionTimeout = inspectionTimeout ?? InspectionTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    internal static TimeSpan ResolveStartupTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromSeconds(DefaultStartupTimeoutSeconds);
        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) &&
            seconds is >= MinimumStartupTimeoutSeconds and <= MaximumStartupTimeoutSeconds)
            return TimeSpan.FromSeconds(seconds);
        Trace.TraceWarning(
            $"Ignoring {StartupTimeoutEnvironmentVariable}='{raw}': expected whole seconds from " +
            $"{MinimumStartupTimeoutSeconds} to {MaximumStartupTimeoutSeconds}. " +
            $"Using the default {DefaultStartupTimeoutSeconds}s.");
        return TimeSpan.FromSeconds(DefaultStartupTimeoutSeconds);
    }

    internal static TimeSpan ResolveInspectionTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromSeconds(DefaultInspectionTimeoutSeconds);
        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) &&
            seconds is >= MinimumInspectionTimeoutSeconds and <= MaximumInspectionTimeoutSeconds)
            return TimeSpan.FromSeconds(seconds);
        Trace.TraceWarning(
            $"Ignoring {InspectionTimeoutEnvironmentVariable}='{raw}': expected whole seconds from " +
            $"{MinimumInspectionTimeoutSeconds} to {MaximumInspectionTimeoutSeconds}. " +
            $"Using the default {DefaultInspectionTimeoutSeconds}s.");
        return TimeSpan.FromSeconds(DefaultInspectionTimeoutSeconds);
    }

    public async Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken)
    {
        Uri endpoint = RequireRecord(record);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
        {
            // Another role held the lifecycle (for example while observing an external restart).
            // Nothing was started, so the caller may observe again later.
            throw new NativeGatewayObservationCanceledException(error, cancellationToken);
        }
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
        // Decided before any await: a deadline during resolution, status or repair must not turn a
        // retained Companion-owned start into a retryable observation. ResolveAsync later verifies
        // that the package family matches the record.
        string family = record.NativePackageFamilyName!;
        bool ownsPendingStart = _ownedStarts.ContainsKey(family) && _pendingOwnedStarts.Contains(family);
        _proofs.Remove(record.Id);
        try
        {
            NativeGatewayPackage package = await ResolveAsync(record, cancellationToken).ConfigureAwait(false);
            IsolatedGatewayStatus status = await ReadInitialStatusAsync(package, endpoint.Port, cancellationToken).ConfigureAwait(false);
            _logger.Info($"Gateway service status: {status.Describe()}.");
            if (status.State != "starting")
                _expiredExternalStarts.Remove(package.PackageFamilyName);
            // Never rewrite configuration underneath a start that another owner is running.
            if (status.State != "starting")
                status = await RepairConfigurationAsync(
                    record, endpoint, package, status, cancellationToken).ConfigureAwait(false);
            // A retained start (lost acknowledgement or failed rollback) is still Companion's own
            // lifecycle; it is never reclassified as an external start that may be retried.
            if (status.State is "not-started" or "stopped")
            {
                var conflict = InspectSnapshot(endpoint.Port, null);
                if (conflict.Kind != GatewayEndpointProvenanceKind.NoListener)
                {
                    _logger.Warn($"Port {endpoint.Port} is already in use before start. {conflict.Detail ?? conflict.Kind.ToString()}");
                    throw new NativeGatewayListenerException(conflict);
                }
                startedThisCall = package;
                string? attemptId = status.SupportsLaunchReceipts ? Guid.NewGuid().ToString("N") : null;
                _ownedStarts[package.PackageFamilyName] = attemptId;
                _pendingOwnedStarts.Add(package.PackageFamilyName);
                _logger.Info($"Starting the package-managed Gateway on port {endpoint.Port}.");
                status = await StartAndWaitAsync(package, endpoint.Port, attemptId, cancellationToken).ConfigureAwait(false);
            }
            else if (status.State == "starting" && !ownsPendingStart &&
                _expiredExternalStarts.Contains(package.PackageFamilyName))
            {
                // The previous observation already spent a full startup budget on this external
                // start. Report it once per reconnect backoff instead of polling for another budget.
                _logger.Info("The external Gateway start is still pending after a full startup budget.");
            }
            else if (status.State == "starting")
            {
                status = await WaitForExistingStartAsync(
                    package, endpoint.Port, external: !ownsPendingStart, cancellationToken).ConfigureAwait(false);
            }
            EndpointRole role = await ClassifyEndpointAsync(status, endpoint, cancellationToken).ConfigureAwait(false);
            if (role == EndpointRole.Unconfirmed)
                throw new NativeGatewayEndpointUnverifiedException(UnconfirmedEndpointMessage(endpoint.Port));
            if (role == EndpointRole.NotServing)
            {
                _logger.Warn($"The Gateway is not running on port {endpoint.Port}: {status.Describe()}.");
                // Another owner (for example clawctl restart) may still be changing a lifecycle this
                // call never requested. Companion's own failed start and a wrong port stay terminal.
                throw new NativeGatewayContractException(
                    "The package-managed Gateway is not running on Companion's selected port. " +
                    "Check clawctl gateway-service status before connecting.",
                    awaitingExternalLifecycle: startedThisCall is null && !ownsPendingStart && status.State != "running");
            }

            var provenance = InspectSnapshot(endpoint.Port, status);
            if (provenance.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
            {
                _logger.Warn($"Listener verification failed on port {endpoint.Port}. {provenance.Detail}");
                throw new NativeGatewayListenerException(provenance);
            }
            _logger.Info($"Verified the Gateway listener on port {endpoint.Port} (PID {provenance.ProcessId}).");
            cancellationToken.ThrowIfCancellationRequested();
            _proofs[record.Id] = new(record, package, status);
            _pendingOwnedStarts.Remove(package.PackageFamilyName);
        }
        catch (Exception error)
        {
            if (startedThisCall is not null && _ownedStarts.TryGetValue(startedThisCall.PackageFamilyName, out var attemptId))
            {
                if (error is NativeGatewayEndpointUnverifiedException)
                {
                    // The package reports this start running; only proof of Companion's endpoint is missing.
                    // Stopping it would not supply that proof and would restart-loop the Gateway on every retry.
                    _logger.Warn("Keeping the Gateway start requested by this attempt: the package reports it running " +
                        "but Companion could not verify its endpoint. Credentials were not sent.");
                    throw;
                }
                try
                {
                    _logger.Info($"Rolling back the Gateway start requested by this attempt ({error.GetType().Name}).");
                    await StopLaunchAsync(startedThisCall, attemptId, CancellationToken.None).ConfigureAwait(false);
                    _ownedStarts.Remove(startedThisCall.PackageFamilyName);
                    _pendingOwnedStarts.Remove(startedThisCall.PackageFamilyName);
                }
                catch (Exception cleanupFailure) when (IsInspectionFailure(cleanupFailure))
                {
                    _logger.Warn($"Isolated Gateway start rollback failed ({cleanupFailure.GetType().Name}); stop ownership retained.");
                }
            }
            // A listener failure that ended this call's own start must not invite a reconnect to
            // start and roll back again, even when the same observation is retryable passively.
            if (startedThisCall is not null && error is NativeGatewayListenerException { DuringOwnedStart: false } listener)
                throw new NativeGatewayListenerException(listener.Provenance, duringOwnedStart: true);
            // A deadline that expired while only observing (no start of Companion's own was
            // involved) leaves nothing to retry dangerously; it is distinguishable from a rollback.
            if (startedThisCall is null && !ownsPendingStart &&
                error is OperationCanceledException and not NativeGatewayObservationCanceledException)
                throw new NativeGatewayObservationCanceledException(error, cancellationToken);
            throw;
        }
    }

    private enum EndpointRole { NotServing, Gateway, Unconfirmed }

    /// <summary>
    /// A status without a port attests several session-owned listeners, such as the MCP App sandbox
    /// host beside the Gateway or a plugin's loopback proxy. Session ownership does not make
    /// Companion's saved port the Gateway
    /// endpoint, and saved configuration is not the running endpoint, so the listener there must
    /// serve the Gateway protocol now. A reported port already identifies the endpoint.
    /// </summary>
    private async Task<EndpointRole> ClassifyEndpointAsync(
        IsolatedGatewayStatus status, Uri endpoint, CancellationToken cancellationToken)
    {
        if (!status.IsRunningOn(endpoint.Port))
            return EndpointRole.NotServing;
        if (status.Port is not null)
            return EndpointRole.Gateway;
        switch (await _probeGatewayProtocol(endpoint, _inspectionTimeout, cancellationToken).ConfigureAwait(false))
        {
            case GatewayProtocolProbeResult.Gateway:
                return EndpointRole.Gateway;
            case GatewayProtocolProbeResult.Unavailable:
                _logger.Warn($"The Gateway listener on port {endpoint.Port} did not answer the protocol check.");
                return EndpointRole.Unconfirmed;
            default:
                _logger.Warn($"Port {endpoint.Port} belongs to the Gateway session but does not serve the Gateway protocol.");
                return EndpointRole.NotServing;
        }
    }

    private static string UnconfirmedEndpointMessage(int port) =>
        NativeGatewayLocalization.Format(NativeGatewayLocalization.EndpointProbeUnansweredFormatKey, port);

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
        _logger.Info(staleSession
            ? "The isolated Gateway session is stale. Reprovisioning it and restoring Companion's saved endpoint."
            : "The Gateway configuration file is missing. Restoring Companion's saved endpoint.");

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
        NativeGatewayPackage package, int port, string? attemptId, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        bool acknowledged = false;
        var progress = new StartupProgress();
        using var deadline = new CancellationTokenSource(StartupTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            _logger.Info($"Native Gateway start requested on port {port} (budget {(int)StartupTimeout.TotalSeconds}s).");
            // A failed/ambiguous start response is not a pending-start acknowledgement.
            // In particular, older packages omit the lifecycle state on exit 1.
            if (attemptId is null)
                await _client.StartAsync(package, linked.Token).ConfigureAwait(false);
            else
            {
                var launch = await _client.StartAsync(package, attemptId, linked.Token).ConfigureAwait(false);
                if (launch.Disposition == "adopted")
                    _ownedStarts.Remove(package.PackageFamilyName);
            }
            linked.Token.ThrowIfCancellationRequested();
            acknowledged = true;
            _logger.Info($"Native Gateway start acknowledged after {_timeProvider.GetElapsedTime(startedAt).TotalSeconds:0.0}s.");
            // A late successful acknowledgement still gets one bounded status window.
            // This can extend the total startup ceiling by at most ten seconds.
            var remaining = StartupTimeout - _timeProvider.GetElapsedTime(startedAt);
            deadline.CancelAfter(remaining > StartupConfirmationTimeout ? remaining : StartupConfirmationTimeout);
            return await PollPendingStartAsync(
                package, port, progress, listenerMayPrecedeRunning: false, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw await CreateTimeoutAsync(package, acknowledged, progress.LastStatus, error, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw await CreateTimeoutAsync(package, acknowledged, progress.LastStatus, error, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Observes a start this call did not request within the same startup budget, without taking start,
    /// stop or rollback ownership. <paramref name="external"/> is false for a start Companion still owns,
    /// whose timeout stays terminal. Only a fresh running attestation followed by the caller's full
    /// listener proof can authorize credentials.
    /// </summary>
    private async Task<IsolatedGatewayStatus> WaitForExistingStartAsync(
        NativeGatewayPackage package, int port, bool external, CancellationToken cancellationToken)
    {
        var progress = new StartupProgress();
        using var deadline = new CancellationTokenSource(StartupTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        _logger.Info($"The Gateway is already starting; waiting for it on port {port} " +
            $"(budget {(int)StartupTimeout.TotalSeconds}s) without taking ownership.");
        try
        {
            return await PollPendingStartAsync(
                package, port, progress, listenerMayPrecedeRunning: external, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested ||
            error is TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            var timeout = new NativeGatewayStartupTimeoutException(
                NativeGatewayStartupTimeoutPhase.Readiness, StartupTimeout, progress.LastStatus, error,
                existingStart: true)
            { ExternalStart = external };
            if (external)
                _expiredExternalStarts.Add(package.PackageFamilyName);
            _logger.Warn(timeout.Message);
            throw timeout;
        }
    }

    /// <summary>
    /// A status read that times out before Companion decides anything is an unavailable inspection,
    /// the same result a passive ownership inspection reports. It never authorizes or starts anything.
    /// </summary>
    private async Task<IsolatedGatewayStatus> ReadInitialStatusAsync(
        NativeGatewayPackage package, int port, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn("The Gateway package status read timed out. Credentials were not sent.");
            throw new NativeGatewayListenerException(new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.UnknownListener, port,
                Detail: "Gateway ownership inspection timed out. Retry after checking the package. Credentials were not sent.",
                FailureReason: GatewayEndpointProvenanceFailureReason.InspectionUnavailable));
        }
    }

    private sealed class StartupProgress
    {
        public IsolatedGatewayStatus? LastStatus;
        public string? LastSummary;
    }

    private async Task<IsolatedGatewayStatus> PollPendingStartAsync(
        NativeGatewayPackage package, int port, StartupProgress progress, bool listenerMayPrecedeRunning,
        CancellationToken token)
    {
        while (true)
        {
            var status = await _client.StatusAsync(package, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            progress.LastStatus = status;
            var summary = status.Describe();
            if (summary != progress.LastSummary)
            {
                _logger.Info($"Native Gateway startup status: {summary}.");
                progress.LastSummary = summary;
            }
            if (status.State is not ("starting" or "unhealthy"))
                return status;

            // A pending start never receives listener trust. Recheck a transition racing the
            // status snapshot once. Only a fresh Running attestation may proceed to the full
            // listener identity checks.
            var provenance = InspectSnapshot(port, null);
            if (provenance.Kind != GatewayEndpointProvenanceKind.NoListener)
            {
                var confirmation = await _client.StatusAsync(package, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                progress.LastStatus = confirmation;
                if (confirmation.State == "running")
                    return confirmation;
                // An external restart can bind its port before the package reports running. Keep
                // observing within the same budget; the listener stays untrusted until full proof.
                if (listenerMayPrecedeRunning && confirmation.State == "unknown")
                    throw new NativeGatewayListenerException(new GatewayEndpointProvenance(
                        GatewayEndpointProvenanceKind.UnknownListener, port,
                        Detail: "The Gateway package could not report its service state during a restart. Credentials were not sent.",
                        FailureReason: GatewayEndpointProvenanceFailureReason.InspectionUnavailable));
                if (!listenerMayPrecedeRunning || confirmation.State is not ("starting" or "unhealthy"))
                    throw new NativeGatewayListenerException(provenance);
            }
            await Task.Delay(StartupPollInterval, _timeProvider, token).ConfigureAwait(false);
        }
    }

    private async Task<NativeGatewayStartupTimeoutException> CreateTimeoutAsync(
        NativeGatewayPackage package,
        bool acknowledged,
        IsolatedGatewayStatus? lastStatus,
        Exception error,
        CancellationToken cancellationToken)
    {
        // An unacknowledged start has no polled status; one bounded read explains what the package saw.
        // After an acknowledged start a hung status read is the likely cause, so never snapshot again.
        if (!acknowledged && lastStatus is null)
            lastStatus = await TryReadTimeoutStatusAsync(package, cancellationToken).ConfigureAwait(false);
        var timeout = new NativeGatewayStartupTimeoutException(
            acknowledged ? NativeGatewayStartupTimeoutPhase.Readiness : NativeGatewayStartupTimeoutPhase.StartRequest,
            StartupTimeout, lastStatus, error);
        _logger.Warn(timeout.Message);
        return timeout;
    }

    private async Task<IsolatedGatewayStatus?> TryReadTimeoutStatusAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(_inspectionTimeout, _timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            return await _client.StatusAsync(package, bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return null;
        }
    }

    public async Task<GatewayEndpointProvenance> InspectAsync(
        GatewayRecord record, CancellationToken cancellationToken)
    {
        Uri endpoint = RequireRecord(record);
        using var timer = new CancellationTokenSource(_inspectionTimeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
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
            EndpointRole role = await ClassifyEndpointAsync(status, endpoint, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (role == EndpointRole.Unconfirmed)
                return new(GatewayEndpointProvenanceKind.UnknownListener, endpoint.Port,
                    Detail: UnconfirmedEndpointMessage(endpoint.Port),
                    FailureReason: GatewayEndpointProvenanceFailureReason.InspectionUnavailable);
            if (role == EndpointRole.NotServing)
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
                Detail: ex is NativeGatewayEndpointUnverifiedException unverified
                    ? unverified.Message
                    : "Gateway ownership inspection failed. Check the installed Gateway package and retry. Credentials were not sent.",
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

    /// <summary>
    /// The package service outlives Companion, so a successor adopts it through its own
    /// status and listener proof instead of a cold restart.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _proofs.Clear();
            _ownedStarts.Clear();
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
            _logger.Info("Restarting the package-managed Gateway.");
            NativeGatewayPackage package = await ResolveAsync(record, cancellationToken).ConfigureAwait(false);
            IsolatedGatewayStatus status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            // A listener whose role could not be observed is still attested to the session; restarting
            // it is the requested remedy, and the restart verifies the endpoint before credentials.
            bool attested = await ClassifyEndpointAsync(status, endpoint, cancellationToken).ConfigureAwait(false)
                is not EndpointRole.NotServing;
            var provenance = InspectSnapshot(endpoint.Port, attested ? status : null);
            if (status.State == "running" && !attested ||
                provenance.Kind is not (GatewayEndpointProvenanceKind.ExpectedManagedGateway or GatewayEndpointProvenanceKind.NoListener))
                throw new NativeGatewayListenerException(provenance);

            bool owned = _ownedStarts.TryGetValue(package.PackageFamilyName, out var attemptId);
            if (!owned && status.State is not ("running" or "stopped" or "not-started"))
                throw new NativeGatewayContractException(
                    "Companion cannot restart an unready Gateway launch that it does not own. Check Gateway status before retrying.");
            _proofs.Remove(record.Id);
            await StopLaunchAsync(package, owned ? attemptId : null, cancellationToken).ConfigureAwait(false);
            status = await _client.StatusAsync(package, cancellationToken).ConfigureAwait(false);
            _logger.Info($"Gateway stop reported: {status.Describe()}.");
            if (status.State is not ("stopped" or "not-started"))
                throw new NativeGatewayContractException(
                    "The Gateway package has not confirmed that its service stopped. Retry restart after checking its status.");
            _ownedStarts.Remove(package.PackageFamilyName);
            try
            {
                await EnsureRunningCoreAsync(record, endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (NativeGatewayEndpointUnverifiedException) when (!owned)
            {
                // The unverified restart is kept running; it still replaced a service Companion did not own.
                _ownedStarts.Remove(package.PackageFamilyName);
                throw;
            }
            // An explicit restart must not turn a pre-existing service into a detach-owned start.
            if (!owned)
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
        foreach (var (family, attemptId) in _ownedStarts.ToArray())
        {
            try
            {
                NativeGatewayPackage package = await _resolver.ResolveAsync(family, cancellationToken).ConfigureAwait(false);
                NativeGatewayPaths.ValidatePackage(package, family);
                await StopLaunchAsync(package, attemptId, cancellationToken).ConfigureAwait(false);
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

    private Task StopLaunchAsync(NativeGatewayPackage package, string? attemptId, CancellationToken ct) =>
        attemptId is null ? _client.StopAsync(package, ct) : _client.StopAsync(package, attemptId, ct);

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
            return Unknown("A listener exists, but the package did not attest to it." + Owner(listeners[0]));
        IReadOnlyDictionary<int, ulong> firstSequences;
        try
        {
            firstSequences = _captureSequences();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            return Unknown(ex.Message, GatewayEndpointProvenanceFailureReason.ProcessIdentityUnavailable);
        }
        // The port can carry several listeners (for example an attested IPv4 listener beside an
        // unattested IPv6 one). Name the first listener that fails a check, not the first listener.
        WindowsTcpListenerInfo? failing = listeners.FirstOrDefault(listener =>
            !IPAddress.IsLoopback(listener.Address) ||
            !firstSequences.TryGetValue(listener.ProcessId, out ulong sequence) ||
            !status.Listeners.Any(owned => owned.Port == port &&
                owned.ProcessId == listener.ProcessId && owned.SequenceNumber == sequence));
        if (failing is not null)
            return Unknown("The listener is not owned by the verified isolated Gateway session." + Owner(failing));
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
            return Unknown("The Gateway listener changed during ownership verification.",
                GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged);
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
        string Owner(WindowsTcpListenerInfo l) => $" Listening process: {l.ProcessName ?? "unknown"} (PID {l.ProcessId}).";
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
            new IsolatedGatewayRuntime(resolver, logger), resolver, new NativeGatewayPackageClient(logger));

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

    public Task ReleaseAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(legacy.ReleaseAsync(cancellationToken), isolated.ReleaseAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        try { await legacy.DisposeAsync().ConfigureAwait(false); }
        finally { await isolated.DisposeAsync().ConfigureAwait(false); }
    }

    private static GatewayEndpointProvenance Unestablished(GatewayRecord record) =>
        new(GatewayEndpointProvenanceKind.UnknownListener, NativeGatewayPaths.ValidateRecord(record).Port,
            Detail: "Gateway ownership has not been established for this profile. Credentials were not sent.");
}
