using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// The only production system.run runner. Ownership survives cancelled waits and late native launches.
/// Admission slots cover launch, termination, draining and disposal, not just the caller's wait.
/// </summary>
public sealed class MxcCommandRunner : ICommandRunner
{
    private const int StatusDllInitFailed = unchecked((int)0xC0000142);
    private static readonly SemaphoreSlim OwnedOperations = new(8, 8);
    private readonly Func<SettingsData> _settings;
    private readonly Func<string> _settingsDirectory;
    private readonly Func<ContainerRequest, Task<IMxcProcess>> _spawn;
    private readonly Func<ContainerRequest, ProbeOutput> _probe;
    private readonly Func<MxcAvailability> _availability;
    private readonly Func<MxcRequestContext> _context;
    private readonly string _scratchRoot;
    private readonly IOpenClawLogger _logger;

    public string Name => "mxc";
    internal Func<string, MxcFolderAdmission.Metadata?> ReadPhysicalMetadata { get; init; } = MxcFolderAdmission.ReadMetadata;

    public MxcCommandRunner(
        Func<SettingsData> settingsProvider,
        Func<string> settingsDirectoryProvider,
        Func<MxcAvailability>? availabilityProvider = null,
        IOpenClawLogger? logger = null,
        Func<ContainerRequest, Task<IMxcProcess>>? spawn = null,
        Func<ContainerRequest, ProbeOutput>? probe = null,
        string? scratchRoot = null,
        Func<MxcRequestContext>? contextProvider = null)
    {
        _settings = settingsProvider;
        _settingsDirectory = settingsDirectoryProvider;
        _availability = availabilityProvider ?? (() => MxcAvailability.Probe(logger));
        _context = contextProvider ?? (() => MxcRequestContext.Capture(_settingsDirectory()));
        _spawn = spawn ?? (async request => await MxcContainer.SpawnAsync(
            request, new SpawnOptions { Telemetry = new TelemetryConfig { Enabled = false } },
            CancellationToken.None).ConfigureAwait(false));
        _probe = probe ?? (request => MxcContainer.Probe(request));
        _scratchRoot = scratchRoot ?? GetDefaultScratchRoot(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _logger = logger ?? NullLogger.Instance;
    }

    internal static string GetDefaultScratchRoot(string? overrideRoot, string localAppData) =>
        Path.Combine(string.IsNullOrWhiteSpace(overrideRoot) ? localAppData : overrideRoot, "OpenClawMxc", "Runs");

    public string CapturePolicy() => MxcRequestBuilder.Fingerprint(MxcRequestBuilder.Snapshot(_settings()));

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        const string scratchError = "The MXC scratch directory must use a valid absolute path. " +
            "Remove or correct OPENCLAW_TRAY_LOCALAPPDATA_DIR and restart OpenClaw.";
        try
        {
            if (!Path.IsPathFullyQualified(_scratchRoot)) return Block(scratchError);
            _ = Path.GetFullPath(_scratchRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Block(scratchError);
        }
        var settings = MxcRequestBuilder.Snapshot(_settings());
        var fingerprint = MxcRequestBuilder.Fingerprint(settings);
        if (request.ExpectedMxcPolicy is not null && request.ExpectedMxcPolicy != fingerprint)
            return Block("Permissions changed while the command was awaiting approval. Retry for a fresh approval.",
                NodeToolErrorCategory.ExecPolicyDenied);
        if (!OwnedOperations.Wait(0))
            return Block("Command execution is busy: eight commands running or finishing cleanup. Wait for capacity, then retry.",
                NodeToolErrorCategory.NodeBusy, mode: null);

        var stopwatch = Stopwatch.StartNew();
        var stop = new CancellationTokenSource();
        var timeout = EffectiveTimeout(request.TimeoutMs, settings.SandboxTimeoutMs);
        var executionMode = (int)NodeToolExecutionMode.Sandbox;
        var capture = new CommandOutputCapture(
            settings.SandboxMaxOutputBytes > 0 ? settings.SandboxMaxOutputBytes : 4 * 1024 * 1024);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > 0) deadline.CancelAfter(timeout);
        var owned = Task.Run(async () =>
        {
            try { return await ExecuteOwnedAsync(request, settings, fingerprint, capture, stop.Token,
                mode => Volatile.Write(ref executionMode, (int)mode)).ConfigureAwait(false); }
            finally
            {
                stop.Dispose();
                OwnedOperations.Release();
            }
        });

        try
        {
            var result = await owned.WaitAsync(deadline.Token).ConfigureAwait(false);
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            return WithInitializationContext(result);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Snapshot before termination can overwrite the root's exit status or close native streams.
            var result = capture.Snapshot((NodeToolExecutionMode)Volatile.Read(ref executionMode), deadlineExpired: true);
            // Do not dispose a late process or its scratch from this waiter. Its owner remains bounded.
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* The native owner completed at the cancellation boundary. */ }
            _ = ObserveCleanupAsync(owned);
            ct.ThrowIfCancellationRequested();
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            return WithInitializationContext(result);
        }
    }

    private static CommandResult WithInitializationContext(CommandResult result)
    {
        if (result.ExitCode != StatusDllInitFailed || result.TimedOut ||
            result.ExecutionMode != NodeToolExecutionMode.Sandbox)
            return result;

        const string hint =
            "Command initialization failed (0xC0000142). This can involve Windows UI APIs (USER32) " +
            "or another DLL dependency. Check Windows runtime and desktop compatibility. " +
            "Containment was not relaxed.";
        result.Stderr = string.IsNullOrWhiteSpace(result.Stderr)
            ? hint
            : result.Stderr + Environment.NewLine + hint;
        return result;
    }

    private async Task<CommandResult> ExecuteOwnedAsync(
        CommandRequest command, SettingsData settings, string fingerprint, CommandOutputCapture capture, CancellationToken stop,
        Action<NodeToolExecutionMode> selectMode)
    {
        var scratch = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N"));
        var mode = NodeToolExecutionMode.Sandbox;
        Task? nativeCompletion = null;
        try
        {
            stop.ThrowIfCancellationRequested();
            var availability = _availability();
            if (availability.IsWindowsUnsupported)
            {
                selectMode(NodeToolExecutionMode.Host);
                mode = NodeToolExecutionMode.Host;
                Directory.CreateDirectory(scratch);
                stop.ThrowIfCancellationRequested();
                if (command.RevalidateApproval is { } hostRevalidate)
                {
                    var authorization = await hostRevalidate(stop).ConfigureAwait(false);
                    if (!authorization.IsCurrent)
                        return BlockApprovalRevalidation(authorization, mode);
                }
                if (CapturePolicy() != fingerprint)
                    return Block("Permissions changed before launch. Retry for a fresh approval.",
                        NodeToolErrorCategory.ExecPolicyDenied, mode);
                _logger.Warn("[mxc] operation=compatibility executionMode=host reason=windows_unsupported sandboxControlsEnforced=false");
                return await UnsupportedWindowsCommandExecutor.RunAsync(command, scratch, capture, stop).ConfigureAwait(false);
            }
            if (!availability.CanRunSystemRunSandbox)
                return Block(string.Join(" ", availability.SystemRunSandboxUnsupportedReasons), NodeToolErrorCategory.SandboxUnavailable);
            Directory.CreateDirectory(scratch);
            // Only this newly created, owned directory may be translated for an SDK grant/cwd.
            // Keep the logical path for cleanup; caller-selected grants and cwd stay fail-closed.
            var physicalScratch = MxcPhysicalPaths.ResolveDirectory(scratch, ReadPhysicalMetadata, requireExisting: true);
            var request = MxcRequestBuilder.Build(command, settings, _settingsDirectory(), physicalScratch, _context(),
                MxcFolderAdmission.ReadMetadata, ReadPhysicalMetadata);
            var admission = _probe(request);
            if (admission.Error is not null || admission.Tier != IsolationTier.BaseContainer ||
                admission.NeedsDaclAugmentation != false ||
                !admission.Probes.BaseContainerSupportsDenyPaths)
                return Block("This complete permission policy requires unsupported containment. " +
                    "MXC BaseContainer with native protected-folder denies and no host ACL mutation is required. Update Windows or select a supported policy.");
            stop.ThrowIfCancellationRequested();
            if (command.RevalidateApproval is { } revalidate)
            {
                var authorization = await revalidate(stop).ConfigureAwait(false);
                if (!authorization.IsCurrent)
                    return BlockApprovalRevalidation(authorization, mode);
            }
            stop.ThrowIfCancellationRequested();
            if (CapturePolicy() != fingerprint)
                return Block("Permissions changed before launch. Retry for a fresh approval.",
                    NodeToolErrorCategory.ExecPolicyDenied);

            _logger.Info($"[mxc] operation=spawn requested=base-container applied=unknown ro={request.Filesystem!.ReadonlyPaths.Count} rw={request.Filesystem.ReadwritePaths.Count} denies={request.Filesystem.DeniedPaths.Count}");
            // Never pass caller cancellation to SpawnAsync: the SDK's cancelled wait hides its late result.
            using var process = await _spawn(request).ConfigureAwait(false);
            process.StandardInput?.Dispose();
            using var stdout = process.StandardOutput;
            using var stderr = process.StandardError;
            using var stdoutCloser = process.StandardOutputCloser;
            using var stderrCloser = process.StandardErrorCloser;
            var output = Task.Run(() => capture.StandardOutput.Collect(stdout));
            var error = Task.Run(() => capture.StandardError.Collect(stderr));
            using var observationStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var rootExit = ObserveRootExitAsync(process, observationStop.Token);
            capture.BindRootExit(rootExit, () => (rootExit.Result, false));
            var drains = Task.WhenAll(output, error);
            nativeCompletion = drains;
            try
            {
                await CommandOutputCapture.WaitForExitOrInterruptionAsync(rootExit, output, error, stop).ConfigureAwait(false);
                if (rootExit.IsCompletedSuccessfully)
                    await CommandOutputCapture.DrainAfterExitAsync(drains, stop).ConfigureAwait(false);
            }
            finally
            {
                // SDK WaitAsync includes descendant reaping/teardown and holds the control lock.
                // Settle the non-finalizing observer before either Kill or WaitAsync takes that lock.
                observationStop.Cancel();
                try { await rootExit.ConfigureAwait(false); }
                catch (OperationCanceledException) when (observationStop.IsCancellationRequested) { }
                finally
                {
                    capture.CaptureRootExit();
                    capture.StandardOutput.Interrupt();
                    capture.StandardError.Interrupt();
                    // Close the SDK streams independently of a potentially blocked Kill. All workers stay
                    // under this operation's admission slot and scratch lifetime until they really settle.
                    var close = Task.Run(() =>
                    {
                        try { stdoutCloser?.Close(); }
                        finally { stderrCloser?.Close(); }
                    });
                    var kill = rootExit.IsCompletedSuccessfully
                        ? Task.CompletedTask : Task.Run(() => process.Kill());
                    try { await Task.WhenAll(close, kill).ConfigureAwait(false); }
                    finally
                    {
                        // Finalization is mandatory even after root exit or a failed Kill. Its duration
                        // cannot turn the cached root status into a caller timeout.
                        var wait = process.WaitAsync(CancellationToken.None);
                        nativeCompletion = Task.WhenAll(wait, drains);
                        await nativeCompletion.ConfigureAwait(false);
                        var result = await wait.ConfigureAwait(false);
                        _logger.Info($"[mxc] operation=exit applied=unknown exit={result.ExitCode} timedOut={result.TimedOut}");
                    }
                }
            }
            return capture.Snapshot(NodeToolExecutionMode.Sandbox);
        }
        catch (OperationCanceledException) { throw; }
        catch (NotSupportedException ex)
        {
            return Block(ex.Message, mode == NodeToolExecutionMode.Host
                ? NodeToolErrorCategory.CommandUnavailable : NodeToolErrorCategory.SandboxDenied, mode);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[mxc] operation=failed error={ex.GetType().Name} applied=unknown");
            return mode == NodeToolExecutionMode.Host
                ? Block("Uncontained compatibility execution failed. Verify the approved executable and working folder before retrying.",
                    NodeToolErrorCategory.CommandUnavailable, mode)
                : Block("MXC failed to apply or execute this policy. Repair the installation or update Windows before retrying.",
                    NodeToolErrorCategory.SandboxFailure);
        }
        finally
        {
            if (nativeCompletion is not null)
            {
                try { await nativeCompletion.ConfigureAwait(false); }
                catch (Exception ex)
                { _logger.Warn($"[mxc] operation=terminal-task-failed error={ex.GetType().Name}"); }
            }
            if (Directory.Exists(scratch))
            {
                try { Directory.Delete(scratch, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"[mxc] operation=scratch-cleanup-failed error={ex.GetType().Name}");
                }
            }
        }
    }

    private static async Task<int> ObserveRootExitAsync(IMxcProcess process, CancellationToken stop)
    {
        var pollMs = 1;
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            if (process.TryGetExitCode(out var exitCode)) return exitCode;
            await Task.Delay(pollMs, stop).ConfigureAwait(false);
            pollMs = Math.Min(pollMs * 2, 50);
        }
    }

    private async Task ObserveCleanupAsync(Task<CommandResult> owned)
    {
        try { await owned.ConfigureAwait(false); }
        catch (OperationCanceledException) { _logger.Info("[mxc] operation=cancel-cleanup-complete"); }
        catch (Exception ex) { _logger.Warn($"[mxc] operation=cancel-cleanup-failed error={ex.GetType().Name}"); }
    }

    internal static async Task<string> CollectAsync(Stream? stream, long byteBudget)
    {
        var capture = new CommandOutputCapture.BoundedStream(byteBudget);
        await capture.CollectAsync(stream).ConfigureAwait(false);
        return capture.Snapshot();
    }

    private static int EffectiveTimeout(int request, int setting) =>
        request > 0 && setting > 0 ? Math.Min(request, setting) : request > 0 ? request : setting > 0 ? setting : 30_000;

    private CommandResult BlockApprovalRevalidation(
        ExecApprovals.ExecApprovalRevalidationResult authorization, NodeToolExecutionMode mode) =>
        authorization.IsPolicyDenial
            ? Block("Command approval changed before launch. Retry for a fresh approval.",
                NodeToolErrorCategory.ExecPolicyDenied, mode)
            : Block("Command approval could not be revalidated before launch. Retry for a fresh approval.",
                NodeToolErrorCategory.InternalFailure, mode);

    private CommandResult Block(string reason, NodeToolErrorCategory category = NodeToolErrorCategory.SandboxDenied,
        NodeToolExecutionMode? mode = NodeToolExecutionMode.Sandbox)
    {
        _logger.Warn($"[mxc] operation=blocked category={category} applied=unknown");
        return new()
        {
            ExitCode = -1, Stderr = reason, ExecutionMode = mode,
            ErrorCategory = category,
            SandboxDenialReason = category == NodeToolErrorCategory.SandboxDenied
                ? NodeToolSandboxDenialReason.UnsupportedSandboxRequest : null,
        };
    }
}
