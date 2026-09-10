// <summary>
// Owns the native llama-server router process for the lifetime of the Windows companion:
// starts it via the managed process host, polls health until ready, publishes/quiesces the
// endpoint through ILocalAiEndpointLifecycle, and supervises restarts with bounded attempts,
// timeouts, and capped rotating logs.
// Usage:
//   var runtime = new LlamaServerRuntimeService(new LlamaServerRuntimeOptions { Paths = paths });
//   runtime.StateChanged += (_, args) => UpdateUi(args.Snapshot);
//   LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync(cancellationToken);
//   if (snapshot.State is LocalAiRuntimeState.Healthy) { /* endpoint ready at snapshot.Endpoint */ }
//   await using var _ = runtime; // StopAsync/RestartAsync/RefreshAsync also available on ILocalAiRuntime
// </summary>
using OpenClaw.Shared;
using System.Net;
using System.Text;

namespace OpenClaw.Connection.LocalAi;

public sealed record LlamaServerRuntimeOptions
{
    public required LocalAiPaths Paths { get; init; }
    public Uri InitialEndpoint { get; init; } = new("http://127.0.0.1:18803/v1");
    public ILocalAiEndpointLifecycle EndpointLifecycle { get; init; } = NullLocalAiEndpointLifecycle.Instance;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxRestartAttempts { get; init; } = 2;
    public long MaxLogBytes { get; init; } = 8 * 1024 * 1024;
    public int LogBackupCount { get; init; } = 2;
    public int MaxLogLineCharacters { get; init; } = 16 * 1024;
}

internal interface ILlamaServerRuntimePlatform
{
    DateTimeOffset UtcNow { get; }
    WindowsTcpListenerSnapshotResult CaptureListeners();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemLlamaServerRuntimePlatform : ILlamaServerRuntimePlatform
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public WindowsTcpListenerSnapshotResult CaptureListeners() => WindowsTcpListenerSnapshot.Capture();
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Owns the native llama-server router for the lifetime of the Windows companion.
/// The router starts without a model; the first inference request triggers the
/// model load defined by the verified preset.
/// </summary>
public sealed class LlamaServerRuntimeService : ILocalAiRuntime
{
    private readonly LlamaServerRuntimeOptions _options;
    private readonly LocalAiManifestStore _manifestStore;
    private readonly IOpenClawLogger _logger;
    private readonly ILocalAiManagedProcessHost _processHost;
    private readonly ILlamaServerRuntimePlatform _platform;
    private readonly ILlamaServerClient _client;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _exitTasksGate = new();
    private readonly HashSet<Task> _exitTasks = [];
    private readonly object _snapshotGate = new();
    private LocalAiRuntimeSnapshot _snapshot;
    private ILocalAiManagedProcess? _managedProcess;
    private LocalAiResolvedInstall? _install;
    private long _generation;
    private int _restartAttempts;
    private bool _stopping;
    private bool _explicitStopRequested;
    private bool _gatewayRouteRequiresResolution;
    private bool _disposed;
    private bool _acceptExitTasks = true;
    private int _disposeStarted;

    public LlamaServerRuntimeService(LlamaServerRuntimeOptions options, IOpenClawLogger? logger = null)
        : this(
            options,
            logger ?? NullLogger.Instance,
            new WindowsLocalAiManagedProcessHost(logger ?? NullLogger.Instance),
            new SystemLlamaServerRuntimePlatform(),
            new LlamaServerClient())
    {
    }

    internal LlamaServerRuntimeService(
        LlamaServerRuntimeOptions options,
        IOpenClawLogger logger,
        ILocalAiManagedProcessHost processHost,
        ILlamaServerRuntimePlatform platform,
        ILlamaServerClient client)
    {
        _options = ValidateOptions(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _manifestStore = new LocalAiManifestStore(options.Paths);
        _snapshot = LocalAiRuntimeSnapshot.Initial(options.InitialEndpoint, platform.UtcNow);
    }

    public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;

    public LocalAiRuntimeSnapshot Snapshot
    {
        get { lock (_snapshotGate) return _snapshot; }
    }

    public async Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _explicitStopRequested = false;
            _restartAttempts = 0;
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _explicitStopRequested = true;
            LocalAiRuntimeSnapshot stopped = await StopCoreAsync(
                    LocalAiQuiesceReason.Teardown,
                    cancellationToken)
                .ConfigureAwait(false);
            _explicitStopRequested = stopped.State == LocalAiRuntimeState.Failed &&
                (_gatewayRouteRequiresResolution || _managedProcess is { HasExited: false });
            return stopped;
        }
        catch (Exception ex)
        {
            bool processStillRunning = _managedProcess is { HasExited: false };
            _explicitStopRequested = _gatewayRouteRequiresResolution || processStillRunning;
            if (Snapshot.State == LocalAiRuntimeState.Stopping)
            {
                string outcome = ex is OperationCanceledException ? "canceled" : "interrupted";
                if (processStillRunning)
                {
                    PublishManagedFailure(
                        $"Local AI stop was {outcome}; the managed listener remains running.");
                }
                else
                {
                    PublishTerminalCleanupFailure($"Local AI stop was {outcome}.");
                }
            }
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _explicitStopRequested = false;
            LocalAiResolvedInstall? restartInstall = _install;
            try
            {
                LocalAiRuntimeSnapshot stopped = await StopCoreAsync(
                        LocalAiQuiesceReason.EndpointCycle,
                        cancellationToken)
                    .ConfigureAwait(false);
                restartInstall ??= _install;
                if (_managedProcess is not null || stopped.State == LocalAiRuntimeState.Failed)
                    return stopped;

                _restartAttempts = 0;
                LocalAiRuntimeSnapshot restarted = await EnsureStartedCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (restarted.State is LocalAiRuntimeState.Failed or LocalAiRuntimeState.NotInstalled &&
                    restartInstall is not null)
                {
                    LocalAiResolvedInstall cleanupInstall = _install ?? restartInstall;
                    bool withdrawn = await WithdrawRouteAsync(
                        cleanupInstall,
                        "after restart startup did not complete").ConfigureAwait(false);
                    if (!withdrawn)
                    {
                        return _managedProcess is { HasExited: false }
                            ? PublishManagedFailure(
                                "Local AI restart did not complete and gateway routing could not be safely disabled; the managed listener remains running.")
                            : PublishTerminalCleanupFailure(
                                "Local AI restart did not complete and gateway routing could not be safely disabled.");
                    }
                    if (_managedProcess is { HasExited: false })
                    {
                        ++_generation;
                        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                        return PublishTerminalCleanupFailure("Local AI restart did not complete.");
                    }
                }
                return restarted;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalAiResolvedInstall? interruptedInstall = restartInstall ?? _install;
                if (interruptedInstall is not null)
                    await CompleteInterruptedRestartAsync(interruptedInstall, "interrupted").ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                LocalAiResolvedInstall? canceledInstall = restartInstall ?? _install;
                if (canceledInstall is not null)
                    await CompleteInterruptedRestartAsync(canceledInstall, "canceled").ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<LocalAiRuntimeSnapshot> EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        if (!await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        LocalAiResolvedInstall install = _install!;
        if (_managedProcess is { HasExited: false })
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        if (_managedProcess is not null)
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);

        WindowsTcpListenerSnapshotResult beforeStart = _platform.CaptureListeners();
        if (!beforeStart.Ipv4Complete)
        {
            return await FailStartupAsync(
                    LocalAiRuntimeState.Conflict,
                    "TCP listener ownership could not be determined.",
                    install,
                    stopUnsafeListener: true)
                .ConfigureAwait(false);
        }

        int requestedPort = install.Manifest.RequestedPort;
        if (requestedPort != LocalAiPortPolicy.Automatic &&
            FindEndpointListeners(beforeStart, requestedPort).Count > 0)
        {
            return await FailStartupAsync(
                    LocalAiRuntimeState.Conflict,
                    "The configured llama-server port is already in use.",
                    install)
                .ConfigureAwait(false);
        }

        LocalAiEndpointLifecycleResult quiesced;
        try
        {
            quiesced = await QuiesceRouteAsync(
                    install,
                    LocalAiQuiesceReason.EndpointCycle,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelStartupAsync(install, terminalTeardownRequired: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("The Local AI gateway provider withdrawal failed before startup.", ex);
            bool withdrawn = await WithdrawRouteAsync(
                    install,
                    "after endpoint-cycle withdrawal was interrupted")
                .ConfigureAwait(false);
            return withdrawn
                ? Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message))
                : PublishTerminalCleanupFailure(
                    $"Local AI startup failed: {Sanitize(ex.Message)} Gateway routing could not be safely disabled.");
        }
        if (!quiesced.Success)
        {
            return await FailStartupAsync(
                    LocalAiRuntimeState.Failed,
                    quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled.",
                    install)
                .ConfigureAwait(false);
        }

        LlamaServerRouterLaunchPlan launchPlan;
        try
        {
            ValidateInstalledFiles(install);
            LocalAiPortPolicy.Validate(requestedPort);
            launchPlan = LlamaServerRouterConfiguration.Build(
                _options.Paths,
                install,
                requestedPort);
            await WritePresetAtomicallyAsync(launchPlan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelStartupAsync(install, terminalTeardownRequired: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error("Could not prepare the managed llama-server router.", ex);
            return await FailStartupAsync(
                    LocalAiRuntimeState.Failed,
                    Sanitize(ex.Message),
                    install)
                .ConfigureAwait(false);
        }

        long generation = ++_generation;
        Publish(LocalAiRuntimeState.Starting, LocalAiOwnership.CompanionManaged, "Starting the local AI router.");
        var spec = new LocalAiProcessStartSpec(
            install.ExecutablePath,
            Path.GetDirectoryName(install.ExecutablePath)!,
            launchPlan.Arguments,
            launchPlan.Environment,
            _options.Paths.StandardOutputLogPath,
            _options.Paths.StandardErrorLogPath,
            _options.MaxLogBytes,
            _options.LogBackupCount,
            _options.MaxLogLineCharacters);

        try
        {
            _managedProcess = await _processHost.StartProcessAsync(
                    spec,
                    exit => OnManagedProcessExited(generation, exit),
                    cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset deadline = _platform.UtcNow + _options.StartupTimeout;
            while (_platform.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_managedProcess.HasExited)
                    throw new InvalidOperationException("Managed llama-server exited during startup.");

                EndpointOwnershipObservation ownership = DiscoverOwnedEndpoint(install, _managedProcess);
                if (!ownership.IsComplete)
                {
                    return await FailStartupAsync(
                            LocalAiRuntimeState.Conflict,
                            "TCP listener ownership could not be determined.",
                            install,
                            stopUnsafeListener: true)
                        .ConfigureAwait(false);
                }
                if (ownership.ConflictDetail is not null)
                {
                    return await FailStartupAsync(
                            LocalAiRuntimeState.Conflict,
                            ownership.ConflictDetail,
                            install,
                            stopUnsafeListener: true)
                        .ConfigureAwait(false);
                }
                if (ownership.Endpoint is not null)
                {
                    LlamaServerRouterProbeResult probe = await _client.ProbeManagedModelAsync(
                            ownership.Endpoint,
                            install.Manifest.ModelAlias,
                            install.ModelPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (probe.IsReadyForManagedModel(install.ModelPath))
                    {
                        LocalAiInstallManifest verifiedManifest = install.Manifest with
                        {
                            Endpoint = ownership.Endpoint.AbsoluteUri,
                        };
                        await _manifestStore.SaveAsync(verifiedManifest, cancellationToken).ConfigureAwait(false);
                        _install = _manifestStore.ResolveAndValidate(verifiedManifest);

                        LocalAiEndpointLifecycleResult published = await PublishRouteAsync(
                                _install,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!published.Success)
                        {
                            return await FailStartupAsync(
                                    LocalAiRuntimeState.Failed,
                                    published.Detail ?? "The Local AI gateway provider could not be safely published.",
                                    _install)
                                .ConfigureAwait(false);
                        }

                        return PublishHealthy(probe);
                    }
                }

                await _platform.DelayAsync(_options.HealthPollInterval, cancellationToken).ConfigureAwait(false);
            }

            return await FailStartupAsync(
                    LocalAiRuntimeState.Failed,
                    "The local AI router did not become healthy before the startup timeout.",
                    install)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelStartupAsync(
                    _install ?? install,
                    terminalTeardownRequired: true)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Managed llama-server startup failed.", ex);
            return await FailStartupAsync(
                    LocalAiRuntimeState.Failed,
                    Sanitize(ex.Message),
                    _install ?? install)
                .ConfigureAwait(false);
        }
    }

    private async Task<LocalAiRuntimeSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_explicitStopRequested)
            return Snapshot;
        if (_gatewayRouteRequiresResolution &&
            (_managedProcess is null || _managedProcess.HasExited))
        {
            return Snapshot;
        }

        if (!await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        try
        {
            ValidateInstalledFiles(_install!);
        }
        catch (InvalidDataException ex)
        {
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }

        if (_managedProcess is null || _managedProcess.HasExited)
        {
            if (_install!.Endpoint is { } persistedEndpoint)
            {
                WindowsTcpListenerSnapshotResult snapshot = _platform.CaptureListeners();
                if (!snapshot.Ipv4Complete)
                    return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "TCP listener ownership could not be determined.");
                if (FindEndpointListeners(snapshot, persistedEndpoint.Port).Count > 0)
                {
                    return Publish(
                        LocalAiRuntimeState.Conflict,
                        LocalAiOwnership.None,
                        "A process not owned by this companion is using the last verified Local AI endpoint.");
                }
            }

            return Publish(
                LocalAiRuntimeState.Stopped,
                LocalAiOwnership.None,
                null,
                modelState: LocalAiModelAvailabilityState.Verified);
        }

        LocalAiResolvedInstall install = _install!;
        EndpointOwnershipObservation ownership = DiscoverOwnedEndpoint(install, _managedProcess);
        if (!ownership.IsComplete)
        {
            LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                    install,
                    LocalAiQuiesceReason.EndpointCycle,
                    stopAfterQuiesce: true,
                    stopUnsafeProcessOnFailure: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return failure ?? Publish(
                LocalAiRuntimeState.Conflict,
                LocalAiOwnership.None,
                "TCP listener ownership could not be determined.");
        }
        if (ownership.ConflictDetail is not null)
        {
            LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                    install,
                    LocalAiQuiesceReason.EndpointCycle,
                    stopAfterQuiesce: true,
                    stopUnsafeProcessOnFailure: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return failure ?? Publish(
                LocalAiRuntimeState.Conflict,
                LocalAiOwnership.None,
                ownership.ConflictDetail);
        }
        if (ownership.Endpoint is null)
        {
            LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                    install,
                    LocalAiQuiesceReason.EndpointCycle,
                    stopAfterQuiesce: false,
                    stopUnsafeProcessOnFailure: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return failure ?? Publish(
                LocalAiRuntimeState.Starting,
                LocalAiOwnership.CompanionManaged,
                "The local AI router has not opened its endpoint yet.",
                _managedProcess.ProcessId,
                _managedProcess.StartedAtUtc);
        }

        LlamaServerRouterProbeResult probe = await _client.ProbeManagedModelAsync(
                ownership.Endpoint,
                install.Manifest.ModelAlias,
                install.ModelPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (probe.IsReadyForManagedModel(install.ModelPath))
        {
            bool endpointChanged = install.Endpoint != ownership.Endpoint;
            if (_snapshot.State != LocalAiRuntimeState.Healthy || endpointChanged)
            {
                if (endpointChanged && _snapshot.State == LocalAiRuntimeState.Healthy)
                {
                    LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                            install,
                            LocalAiQuiesceReason.EndpointCycle,
                            stopAfterQuiesce: false,
                            stopUnsafeProcessOnFailure: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (failure is not null)
                        return failure;
                }

                LocalAiEndpointLifecycleResult published;
                try
                {
                    install = await BindVerifiedEndpointAsync(
                            install,
                            ownership.Endpoint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    published = await PublishRouteAsync(
                            install,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        using var recoveryTimeout = new CancellationTokenSource(_options.ShutdownTimeout);
                        install = await BindVerifiedEndpointAsync(
                                install,
                                ownership.Endpoint,
                                recoveryTimeout.Token)
                            .ConfigureAwait(false);
                        LocalAiEndpointLifecycleResult recovered = await PublishRouteAsync(
                                install,
                                recoveryTimeout.Token)
                            .ConfigureAwait(false);
                        if (recovered.Success)
                        {
                            PublishHealthy(probe);
                        }
                        else
                        {
                            await FailStartupAsync(
                                    LocalAiRuntimeState.Failed,
                                    recovered.Detail ?? "The canceled Local AI endpoint refresh could not restore gateway routing.",
                                    install)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception recoveryException)
                    {
                        await FailStartupAsync(
                                LocalAiRuntimeState.Failed,
                                $"The canceled Local AI endpoint refresh could not restore gateway routing: {Sanitize(recoveryException.Message)}",
                                _install ?? install)
                            .ConfigureAwait(false);
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    await FailStartupAsync(
                            LocalAiRuntimeState.Failed,
                            $"The verified Local AI endpoint could not be republished: {Sanitize(ex.Message)}",
                            _install ?? install)
                        .ConfigureAwait(false);
                    throw;
                }
                if (!published.Success)
                {
                    return await FailStartupAsync(
                            LocalAiRuntimeState.Failed,
                            published.Detail ?? "The verified Local AI endpoint could not be republished.",
                            _install ?? install)
                        .ConfigureAwait(false);
                }
            }

            return PublishHealthy(probe);
        }

        LocalAiRuntimeSnapshot? quiesceFailure = await QuiesceOrStopAsync(
                install,
                LocalAiQuiesceReason.EndpointCycle,
                stopAfterQuiesce: false,
                stopUnsafeProcessOnFailure: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (quiesceFailure is not null)
            return quiesceFailure;

        return Publish(
            LocalAiRuntimeState.Starting,
            LocalAiOwnership.CompanionManaged,
            probe.Detail ?? "The local AI router has not verified the managed model yet.",
            _managedProcess.ProcessId,
            _managedProcess.StartedAtUtc);
    }

    private async Task<LocalAiRuntimeSnapshot?> QuiesceOrStopAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason,
        bool stopAfterQuiesce,
        bool stopUnsafeProcessOnFailure,
        CancellationToken cancellationToken)
    {
        if (stopUnsafeProcessOnFailure && !stopAfterQuiesce)
        {
            throw new ArgumentException(
                "Unsafe-process cleanup is only valid when the endpoint will be stopped.",
                nameof(stopUnsafeProcessOnFailure));
        }

        LocalAiEndpointLifecycleResult quiesced;
        try
        {
            quiesced = await QuiesceRouteAsync(install, reason, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            LocalAiQuiesceReason recoveryReason = stopUnsafeProcessOnFailure
                ? LocalAiQuiesceReason.EndpointCycle
                : LocalAiQuiesceReason.Teardown;
            bool withdrawn = await RetryQuiesceAsync(
                        install,
                        recoveryReason,
                        "after refresh withdrawal was interrupted")
                    .ConfigureAwait(false);
            if (withdrawn)
            {
                ++_generation;
                await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                string detail = recoveryReason == LocalAiQuiesceReason.Teardown
                    ? "The Local AI endpoint cycle was interrupted; terminal gateway routing was restored."
                    : "The Local AI provider was withdrawn after an interrupted endpoint cycle; the managed primary remains selected until Local AI is started or stopped.";
                Publish(
                    LocalAiRuntimeState.Failed,
                    LocalAiOwnership.None,
                    detail);
            }
            else if (stopUnsafeProcessOnFailure)
            {
                ++_generation;
                await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                PublishTerminalCleanupFailure(
                    "The Local AI gateway provider withdrawal did not complete; the untrusted managed listener was stopped.");
            }
            else
            {
                await PublishRefreshCleanupFailureAsync(
                        "The Local AI gateway provider withdrawal did not complete.")
                    .ConfigureAwait(false);
            }
            throw;
        }
        if (quiesced.Success && !stopAfterQuiesce)
            return null;
        if (quiesced.Success)
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        if (stopUnsafeProcessOnFailure)
        {
            bool retried = await RetryQuiesceAsync(
                    install,
                    LocalAiQuiesceReason.EndpointCycle,
                    "after refresh endpoint-cycle withdrawal failed")
                .ConfigureAwait(false);
            if (retried)
            {
                if (stopAfterQuiesce)
                {
                    ++_generation;
                    await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                }
                return null;
            }
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            return PublishTerminalCleanupFailure(
                $"{quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled."} The untrusted managed listener was stopped.");
        }

        bool teardownSucceeded = reason != LocalAiQuiesceReason.Teardown &&
            await WithdrawRouteAsync(
                    install,
                    "after refresh withdrawal failed")
                .ConfigureAwait(false);
        if (!teardownSucceeded)
        {
            return await PublishRefreshCleanupFailureAsync(quiesced.Detail).ConfigureAwait(false);
        }

        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        return Publish(
            LocalAiRuntimeState.Failed,
            LocalAiOwnership.None,
            quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled.");
    }

    private async Task<LocalAiResolvedInstall> BindVerifiedEndpointAsync(
        LocalAiResolvedInstall install,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        if (install.Endpoint == endpoint)
            return install;

        LocalAiInstallManifest verifiedManifest = install.Manifest with
        {
            Endpoint = endpoint.AbsoluteUri,
        };
        await _manifestStore.SaveAsync(verifiedManifest, cancellationToken).ConfigureAwait(false);
        _install = _manifestStore.ResolveAndValidate(verifiedManifest);
        return _install;
    }

    private async Task<bool> TryLoadInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            _install = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error("Could not load the local AI installation manifest.", ex);
            Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
            return false;
        }

        if (_install is not null)
            return true;
        Publish(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None, "Local AI is not installed.");
        return false;
    }

    private static void ValidateInstalledFiles(LocalAiResolvedInstall install)
    {
        if (!File.Exists(install.ExecutablePath))
            throw new InvalidDataException("The managed llama-server executable is missing.");
        var model = new FileInfo(install.ModelPath);
        if (!model.Exists || model.Length != install.Manifest.ModelAsset.SizeBytes)
            throw new InvalidDataException("The managed GGUF model is missing or has an unexpected size.");
    }

    private async Task WritePresetAtomicallyAsync(
        LlamaServerRouterLaunchPlan plan,
        CancellationToken cancellationToken)
    {
        _options.Paths.EnsureDirectories();
        string temporaryPath = Path.Combine(
            _options.Paths.RootDirectory,
            $".{Path.GetFileName(plan.PresetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _ = _options.Paths.ResolveContainedPath(Path.GetFileName(temporaryPath), nameof(temporaryPath));
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(plan.PresetContent);
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            _ = _options.Paths.ResolveContainedPath(
                Path.GetRelativePath(_options.Paths.RootDirectory, plan.PresetPath),
                nameof(plan.PresetPath));
            File.Move(temporaryPath, plan.PresetPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch { }
        }
    }

    private async Task<LocalAiRuntimeSnapshot> StopCoreAsync(
        LocalAiQuiesceReason reason,
        CancellationToken cancellationToken)
    {
        if (_install is null && !await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        LocalAiEndpointLifecycleResult quiesced;
        try
        {
            quiesced = await QuiesceRouteAsync(_install!, reason, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (reason == LocalAiQuiesceReason.Teardown)
            {
                bool withdrawn = await WithdrawRouteAsync(
                        _install!,
                        "after explicit stop withdrawal was interrupted")
                    .ConfigureAwait(false);
                if (withdrawn)
                {
                    ++_generation;
                    await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                    PublishTerminalCleanupFailure("Local AI stop was interrupted.");
                }
                else if (_managedProcess is { HasExited: false })
                {
                    PublishManagedFailure(
                        "Local AI stop was interrupted, but gateway routing could not be safely disabled; the managed listener remains running.");
                }
                else
                {
                    ++_generation;
                    await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                    PublishTerminalCleanupFailure(
                        "Local AI stop was interrupted, but gateway routing could not be safely disabled.");
                }
            }
            throw;
        }
        if (!quiesced.Success)
        {
            if (reason == LocalAiQuiesceReason.EndpointCycle)
            {
                bool withdrawn = await WithdrawRouteAsync(
                        _install!,
                        "after restart withdrawal failed")
                    .ConfigureAwait(false);
                if (withdrawn)
                {
                    ++_generation;
                    await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                    return Publish(
                        LocalAiRuntimeState.Failed,
                        LocalAiOwnership.None,
                        quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled.");
                }
            }
            return Publish(
                LocalAiRuntimeState.Failed,
                _managedProcess is null ? LocalAiOwnership.None : LocalAiOwnership.CompanionManaged,
                quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled.",
                _managedProcess?.ProcessId,
                _managedProcess?.StartedAtUtc);
        }

        if (_managedProcess is null)
        {
            ++_generation;
            return Publish(
                LocalAiRuntimeState.Stopped,
                LocalAiOwnership.None,
                null,
                modelState: LocalAiModelAvailabilityState.Verified);
        }

        _stopping = true;
        ++_generation;
        Publish(LocalAiRuntimeState.Stopping, LocalAiOwnership.CompanionManaged, "Stopping the local AI router.", _managedProcess.ProcessId, _managedProcess.StartedAtUtc);
        try
        {
            await DisposeManagedProcessAsync(cancellationToken).ConfigureAwait(false);
            return Publish(
                LocalAiRuntimeState.Stopped,
                LocalAiOwnership.None,
                null,
                modelState: LocalAiModelAvailabilityState.Verified);
        }
        finally
        {
            _stopping = false;
        }
    }

    private async Task<LocalAiRuntimeSnapshot> FailStartupAsync(
        LocalAiRuntimeState state,
        string detail,
        LocalAiResolvedInstall? routeToWithdraw = null,
        bool stopUnsafeListener = false)
    {
        bool withdrawalFailed = routeToWithdraw is not null &&
            !await WithdrawRouteAsync(routeToWithdraw, "after a failed start").ConfigureAwait(false);
        if (withdrawalFailed && _managedProcess is { HasExited: false })
        {
            if (stopUnsafeListener)
            {
                ++_generation;
                await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                return PublishTerminalCleanupFailure(
                    $"{detail} The Local AI route could not be safely disabled; the untrusted managed listener was stopped.");
            }
            return PublishManagedFailure(
                $"{detail} The Local AI route could not be safely disabled; the managed listener remains running.");
        }

        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        if (withdrawalFailed)
        {
            return PublishTerminalCleanupFailure(
                $"{detail} The Local AI route could not be safely disabled.");
        }
        return Publish(state, LocalAiOwnership.None, detail);
    }

    private async Task CancelStartupAsync(
        LocalAiResolvedInstall install,
        bool terminalTeardownRequired)
    {
        bool withdrawalFailed = terminalTeardownRequired &&
            !await WithdrawRouteAsync(install, "after startup cancellation").ConfigureAwait(false);
        if (withdrawalFailed && _managedProcess is { HasExited: false })
        {
            PublishManagedFailure(
                "Local AI startup was canceled, but gateway routing could not be safely disabled; the managed listener remains running.");
            return;
        }

        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        if (withdrawalFailed)
        {
            PublishTerminalCleanupFailure(
                "Local AI startup was canceled, but gateway routing could not be safely disabled.");
            return;
        }
        Publish(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, "Local AI startup was canceled.");
    }

    private async Task<bool> WithdrawRouteAsync(LocalAiResolvedInstall install, string context)
        => await RetryQuiesceAsync(
                install,
                LocalAiQuiesceReason.Teardown,
                context)
            .ConfigureAwait(false);

    private async Task<bool> RetryQuiesceAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason,
        string context)
    {
        try
        {
            LocalAiEndpointLifecycleResult withdrawn = await QuiesceRouteAsync(
                    install,
                    reason,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!withdrawn.Success)
            {
                _logger.Warn($"{withdrawn.Detail ?? "The Local AI route could not be withdrawn"} {context}.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"The Local AI route could not be withdrawn {context}: {Sanitize(ex.Message)}");
            return false;
        }
    }

    private async Task<LocalAiEndpointLifecycleResult> QuiesceRouteAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason,
        CancellationToken cancellationToken)
    {
        _gatewayRouteRequiresResolution = true;

        LocalAiEndpointLifecycleResult result = await _options.EndpointLifecycle
            .QuiesceAsync(install, reason, cancellationToken)
            .ConfigureAwait(false);
        if (reason == LocalAiQuiesceReason.Teardown && result.Success)
            _gatewayRouteRequiresResolution = false;
        return result;
    }

    private async Task<LocalAiEndpointLifecycleResult> PublishRouteAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken)
    {
        LocalAiEndpointLifecycleResult result = await _options.EndpointLifecycle
            .PublishAsync(install, cancellationToken)
            .ConfigureAwait(false);
        if (result.Success)
            _gatewayRouteRequiresResolution = false;
        return result;
    }

    private LocalAiRuntimeSnapshot PublishManagedFailure(string detail) =>
        Publish(
            LocalAiRuntimeState.Failed,
            LocalAiOwnership.CompanionManaged,
            detail,
            _managedProcess?.ProcessId,
            _managedProcess?.StartedAtUtc);

    private LocalAiRuntimeSnapshot PublishTerminalCleanupFailure(string detail) =>
        Publish(
            LocalAiRuntimeState.Failed,
            LocalAiOwnership.None,
            detail);

    private async Task<LocalAiRuntimeSnapshot> PublishRefreshCleanupFailureAsync(string? detail)
    {
        const string fallbackDetail =
            "The Local AI gateway provider could not be safely disabled during endpoint refresh.";
        if (_managedProcess is { HasExited: false })
        {
            return PublishManagedFailure(
                $"{detail ?? fallbackDetail} The managed listener remains running.");
        }

        ++_generation;
        if (_managedProcess is not null)
            await _managedProcess.DisposeAsync().ConfigureAwait(false);
        _managedProcess = null;
        return PublishTerminalCleanupFailure(detail ?? fallbackDetail);
    }

    private async Task CompleteInterruptedRestartAsync(LocalAiResolvedInstall install, string outcome)
    {
        bool withdrawn = await WithdrawRouteAsync(
                install,
                $"after restart was {outcome}")
            .ConfigureAwait(false);
        if (!withdrawn)
        {
            if (_managedProcess is { HasExited: false })
            {
                PublishManagedFailure(
                    $"Local AI restart was {outcome}, but gateway routing could not be safely disabled; the managed listener remains running.");
            }
            else
            {
                ++_generation;
                await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                PublishTerminalCleanupFailure(
                    $"Local AI restart was {outcome}, but gateway routing could not be safely disabled.");
            }
            return;
        }

        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        PublishTerminalCleanupFailure($"Local AI restart was {outcome}.");
    }

    private async Task DisposeManagedProcessAsync(
        CancellationToken cancellationToken,
        bool preserveForRetry = true)
    {
        ILocalAiManagedProcess? process = _managedProcess;
        _managedProcess = null;
        if (process is null)
            return;
        bool preserved = false;
        try
        {
            await process.StopAsync(_options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (preserveForRetry)
            {
                if (!process.HasExited)
                {
                    _managedProcess = process;
                    preserved = true;
                    PublishManagedFailure(
                        $"The managed listener remains running because it could not be stopped: {Sanitize(ex.Message)}");
                }
                else
                {
                    PublishTerminalCleanupFailure(
                        $"The managed Local AI listener shutdown failed after the process exited: {Sanitize(ex.Message)}");
                }
                throw;
            }
            if (!process.HasExited)
            {
                try
                {
                    await process.StopAsync(_options.ShutdownTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    _logger.Warn(
                        $"The managed Local AI process could not be stopped during final disposal: {Sanitize(stopException.Message)}");
                }
            }
        }
        finally
        {
            if (!preserved)
                await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnManagedProcessExited(long generation, LocalAiManagedProcessExit exit)
    {
        Task exitTask;
        lock (_exitTasksGate)
        {
            if (!_acceptExitTasks)
                return;
            exitTask = Task.Run(() => HandleManagedProcessExitedAsync(generation, exit));
            _exitTasks.Add(exitTask);
        }
        _ = RemoveCompletedExitTaskAsync(exitTask);
    }

    private async Task HandleManagedProcessExitedAsync(long generation, LocalAiManagedProcessExit exit)
    {
        LocalAiResolvedInstall? restartInstall = null;
        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _stopping || generation != _generation)
                    return;
                ILocalAiManagedProcess? exited = _managedProcess;
                _managedProcess = null;
                if (exited is not null)
                    await exited.DisposeAsync().ConfigureAwait(false);

                bool willRestart = !_explicitStopRequested &&
                    _restartAttempts < _options.MaxRestartAttempts;
                restartInstall = _install;
                if (_install is not null)
                {
                    if (willRestart)
                    {
                        LocalAiEndpointLifecycleResult quiesced;
                        try
                        {
                            quiesced = await QuiesceRouteAsync(
                                    _install,
                                    LocalAiQuiesceReason.EndpointCycle,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            bool withdrawn = await WithdrawRouteAsync(
                                    _install,
                                    "after automatic-restart withdrawal was interrupted")
                                .ConfigureAwait(false);
                            if (withdrawn)
                            {
                                Publish(
                                    LocalAiRuntimeState.Failed,
                                    LocalAiOwnership.None,
                                    $"The Local AI gateway provider withdrawal failed after the router exited: {Sanitize(ex.Message)}");
                            }
                            else
                            {
                                PublishTerminalCleanupFailure(
                                    $"The Local AI gateway provider withdrawal failed after the router exited: {Sanitize(ex.Message)} Terminal gateway routing could not be restored.");
                            }
                            return;
                        }
                        if (!quiesced.Success)
                        {
                            bool withdrawn = await WithdrawRouteAsync(
                                    _install,
                                    "after automatic-restart withdrawal failed")
                                .ConfigureAwait(false);
                            if (withdrawn)
                            {
                                Publish(
                                    LocalAiRuntimeState.Failed,
                                    LocalAiOwnership.None,
                                    quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled after the router exited.");
                            }
                            else
                            {
                                PublishTerminalCleanupFailure(
                                    $"{quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled after the router exited."} Terminal gateway routing could not be restored.");
                            }
                            return;
                        }
                    }
                    else if (!await WithdrawRouteAsync(
                            _install,
                            "after automatic restart attempts were exhausted")
                        .ConfigureAwait(false))
                    {
                        Publish(
                            LocalAiRuntimeState.Failed,
                            LocalAiOwnership.None,
                            "The Local AI gateway provider could not be safely disabled after restart attempts were exhausted.");
                        return;
                    }
                }
                Publish(
                    LocalAiRuntimeState.Failed,
                    LocalAiOwnership.None,
                    $"Managed llama-server exited unexpectedly{(exit.ExitCode.HasValue ? $" with code {exit.ExitCode.Value}" : string.Empty)}.");
                if (!willRestart)
                    return;
                _restartAttempts++;
            }
            finally
            {
                _operationGate.Release();
            }

            await _platform.DelayAsync(_options.RestartDelay, CancellationToken.None).ConfigureAwait(false);
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_disposed && !_stopping && !_explicitStopRequested && generation == _generation)
                {
                    LocalAiRuntimeSnapshot restarted;
                    try
                    {
                        restarted = await EnsureStartedCoreAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        if (restartInstall is not null)
                            await CompleteAutomaticRestartFailureAsync(restartInstall).ConfigureAwait(false);
                        throw;
                    }
                    if (restarted.State is LocalAiRuntimeState.Failed or LocalAiRuntimeState.NotInstalled &&
                        restartInstall is not null)
                    {
                        await CompleteAutomaticRestartFailureAsync(restartInstall).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Managed llama-server automatic restart failed.", ex);
        }
    }

    private async Task CompleteAutomaticRestartFailureAsync(LocalAiResolvedInstall restartInstall)
    {
        LocalAiResolvedInstall cleanupInstall = _install ?? restartInstall;
        bool withdrawn = await WithdrawRouteAsync(
                cleanupInstall,
                "after automatic restart startup did not complete")
            .ConfigureAwait(false);
        if (!withdrawn)
        {
            if (_managedProcess is { HasExited: false })
            {
                PublishManagedFailure(
                    "Automatic Local AI restart did not complete and gateway routing could not be safely disabled; the managed listener remains running.");
            }
            else
            {
                PublishTerminalCleanupFailure(
                    "Automatic Local AI restart did not complete and gateway routing could not be safely disabled.");
            }
            return;
        }

        if (_managedProcess is { HasExited: false })
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            PublishTerminalCleanupFailure("Automatic Local AI restart did not complete.");
        }
    }

    private async Task RemoveCompletedExitTaskAsync(Task exitTask)
    {
        await exitTask.ConfigureAwait(false);
        lock (_exitTasksGate)
            _exitTasks.Remove(exitTask);
    }

    private EndpointOwnershipObservation DiscoverOwnedEndpoint(
        LocalAiResolvedInstall install,
        ILocalAiManagedProcess process)
    {
        WindowsTcpListenerSnapshotResult snapshot = _platform.CaptureListeners();
        if (!snapshot.Ipv4Complete)
            return new(false, null, null);

        WindowsTcpListenerInfo[] loopbackListeners = snapshot.Listeners
            .Where(IsIpv4LoopbackListener)
            .ToArray();
        if (snapshot.Listeners.Any(listener =>
                listener.ProcessId == process.ProcessId &&
                listener.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IsIpv4LoopbackListener(listener)))
        {
            return new(
                true,
                null,
                "Managed llama-server opened an IPv4 listener outside the loopback interface.");
        }
        WindowsTcpListenerInfo[] processListeners = loopbackListeners
            .Where(listener => listener.ProcessId == process.ProcessId)
            .ToArray();
        WindowsTcpListenerInfo[] ownedListeners = processListeners
            .Where(listener => IsManagedListener(listener, process))
            .ToArray();
        if (processListeners.Length != ownedListeners.Length)
        {
            return new(
                true,
                null,
                "A llama-server listener was found, but its process start time could not be verified.");
        }

        int requestedPort = install.Manifest.RequestedPort;
        if (requestedPort != LocalAiPortPolicy.Automatic)
        {
            IReadOnlyList<WindowsTcpListenerInfo> requestedListeners = FindEndpointListeners(snapshot, requestedPort);
            if (requestedListeners.Any(listener => !IsManagedListener(listener, process)))
                return new(true, null, "Another process owns the configured llama-server endpoint.");
            if (ownedListeners.Any(listener => listener.Port != requestedPort))
                return new(true, null, "Managed llama-server did not bind the requested fixed port.");
            if (requestedListeners.Count == 0)
                return new(true, null, null);
            return new(true, BuildEndpoint(requestedPort), null);
        }

        int[] ownedPorts = ownedListeners.Select(listener => listener.Port).Distinct().ToArray();
        if (ownedPorts.Length == 0)
            return new(true, null, null);
        if (ownedPorts.Length != 1)
            return new(true, null, "Managed llama-server opened more than one candidate loopback endpoint.");

        int selectedPort = ownedPorts[0];
        if (FindEndpointListeners(snapshot, selectedPort).Any(listener => !IsManagedListener(listener, process)))
            return new(true, null, "Another process shares the managed llama-server endpoint.");
        return new(true, BuildEndpoint(selectedPort), null);
    }

    private static IReadOnlyList<WindowsTcpListenerInfo> FindEndpointListeners(
        WindowsTcpListenerSnapshotResult snapshot,
        int port) => snapshot.Listeners
            .Where(listener => listener.Port == port && IsIpv4EndpointListener(listener))
            .ToArray();

    private static bool IsIpv4EndpointListener(WindowsTcpListenerInfo listener) =>
        IsIpv4LoopbackListener(listener) || listener.Address.Equals(IPAddress.Any);

    private static bool IsIpv4LoopbackListener(WindowsTcpListenerInfo listener) =>
        listener.Address.Equals(IPAddress.Loopback);

    private static Uri BuildEndpoint(int port) =>
        new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port, "/v1").Uri;

    private LocalAiRuntimeSnapshot PublishHealthy(LlamaServerRouterProbeResult probe) =>
        Publish(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.CompanionManaged,
            probe.Detail,
            _managedProcess?.ProcessId,
            _managedProcess?.StartedAtUtc,
            probe.ModelState);

    private static bool IsManagedListener(
        WindowsTcpListenerInfo listener,
        ILocalAiManagedProcess process) =>
        listener.ProcessId == process.ProcessId &&
            listener.ProcessStartTimeUtc is { } started &&
            Math.Abs((started - process.StartedAtUtc.UtcDateTime).TotalSeconds) < 1;

    private LocalAiRuntimeSnapshot Publish(
        LocalAiRuntimeState state,
        LocalAiOwnership ownership,
        string? detail,
        int? processId = null,
        DateTimeOffset? processStartedAtUtc = null,
        LocalAiModelAvailabilityState modelState = LocalAiModelAvailabilityState.Unknown)
    {
        DateTimeOffset now = _platform.UtcNow;
        if (state == LocalAiRuntimeState.NotInstalled)
            modelState = LocalAiModelAvailabilityState.NotInstalled;
        LocalAiModelEvidence evidence = BuildModelEvidence(modelState, now);
        var value = new LocalAiRuntimeSnapshot(
            state,
            ownership,
            _install?.Endpoint ?? _options.InitialEndpoint,
            _install?.Manifest.EngineVersion,
            _install?.Manifest.ModelCatalogId,
            evidence,
            processId,
            processStartedAtUtc,
            detail,
            now,
            _install?.Manifest.ContextLength,
            _install?.Manifest.KeyCachePrecision,
            _install?.Manifest.ValueCachePrecision,
            _install?.Manifest.DraftKeyCachePrecision,
            _install?.Manifest.DraftValueCachePrecision);
        lock (_snapshotGate)
            _snapshot = value;

        EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? handler = StateChanged;
        if (handler is not null)
        {
            foreach (EventHandler<LocalAiRuntimeSnapshotChangedEventArgs> subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, new(value)); }
                catch (Exception ex) { _logger.Warn($"A local AI state observer failed: {Sanitize(ex.Message)}"); }
            }
        }
        return value;
    }

    private LocalAiModelEvidence BuildModelEvidence(
        LocalAiModelAvailabilityState state,
        DateTimeOffset now) => state switch
        {
            LocalAiModelAvailabilityState.NotInstalled => LocalAiModelEvidence.NotInstalled(now),
            LocalAiModelAvailabilityState.Verified when _install is not null => new(
                state,
                now,
                _install.Manifest.ModelAsset.Sha256,
                _install.Manifest.ModelAsset.SizeBytes),
            LocalAiModelAvailabilityState.Loaded when _install is not null => new(
                state,
                now,
                _install.Manifest.ModelAsset.Sha256,
                _install.Manifest.ModelAsset.SizeBytes,
                _install.Manifest.ModelAlias),
            _ => LocalAiModelEvidence.Unknown(now),
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        Task[] exitTasks;
        lock (_exitTasksGate)
        {
            _acceptExitTasks = false;
            exitTasks = [.. _exitTasks];
        }

        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;
                _stopping = true;
                ++_generation;
                if (_install is not null)
                {
                    try
                    {
                        LocalAiEndpointLifecycleResult quiesced = await _options.EndpointLifecycle
                            .QuiesceAsync(_install, LocalAiQuiesceReason.Teardown, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!quiesced.Success)
                            _logger.Warn(quiesced.Detail ?? "The Local AI gateway provider could not be disabled during shutdown.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"The Local AI gateway provider could not be disabled during shutdown: {Sanitize(ex.Message)}");
                    }
                }
                await DisposeManagedProcessAsync(
                        CancellationToken.None,
                        preserveForRetry: false)
                    .ConfigureAwait(false);
                _disposed = true;
                _client.Dispose();
            }
            finally
            {
                _stopping = false;
                _operationGate.Release();
            }
        }
        finally
        {
            await Task.WhenAll(exitTasks).ConfigureAwait(false);
            _operationGate.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static LlamaServerRuntimeOptions ValidateOptions(LlamaServerRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Paths);
        ArgumentNullException.ThrowIfNull(options.EndpointLifecycle);
        if (!options.InitialEndpoint.IsAbsoluteUri ||
            options.InitialEndpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(options.InitialEndpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            options.InitialEndpoint.Port is <= 0 or > 65535 ||
            options.InitialEndpoint.Port == 80 ||
            !string.Equals(options.InitialEndpoint.AbsolutePath, "/v1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.Query) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.Fragment) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.UserInfo))
        {
            throw new ArgumentException("The initial local AI endpoint must use an explicit IPv4 loopback port.", nameof(options));
        }
        if (options.StartupTimeout <= TimeSpan.Zero ||
            options.HealthPollInterval <= TimeSpan.Zero ||
            options.ShutdownTimeout <= TimeSpan.Zero ||
            options.RestartDelay < TimeSpan.Zero)
        {
            throw new ArgumentException("Runtime timeouts must be positive.", nameof(options));
        }
        if (options.MaxRestartAttempts < 0 ||
            options.MaxLogBytes <= 0 ||
            options.LogBackupCount < 0 ||
            options.MaxLogLineCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Runtime limits are invalid.");
        }
        return options;
    }

    private static string Sanitize(string value) => TokenSanitizer.SanitizeLogMessage(value);

    private sealed record EndpointOwnershipObservation(
        bool IsComplete,
        Uri? Endpoint,
        string? ConflictDetail);
}
