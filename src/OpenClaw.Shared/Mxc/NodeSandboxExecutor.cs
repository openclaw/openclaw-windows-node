using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

public sealed class NodeSandboxExecutionException(
    string message, NodeToolErrorCategory category, NodeToolExecutionMode? mode) : Exception(message)
{
    public NodeToolErrorCategory Category { get; } = category;
    public NodeToolExecutionMode? ExecutionMode { get; } = mode;
}

/// <summary>
/// Shared policy admission, mode selection and bounded operation ownership. Workloads
/// cannot provide grants or select host execution. A late launch keeps its slot and scratch.
/// </summary>
public sealed class NodeSandboxExecutor : IDisposable
{
    private static readonly SemaphoreSlim OwnedOperations = new(8, 8);
    private readonly NodeSandboxPolicyStore _policies;
    private readonly Func<string> _settingsDirectory;
    private readonly Func<ContainerRequest, Task<IMxcProcess>> _spawn;
    private readonly Func<ContainerRequest, ProbeOutput> _probe;
    private readonly Func<MxcAvailability> _availability;
    private readonly Func<MxcRequestContext> _context;
    private readonly IFixedFileWorkerResolver? _fileWorker;
    private readonly string _scratchRoot;
    private readonly IOpenClawLogger _logger;
    private readonly MxcOperationCleanup _cleanup;
    internal Func<ProcessStartInfo, MxcProcessLifetime.OwnedProcess> StartHost { get; init; } =
        info => new MxcProcessLifetime.HostProcess(Process.Start(info) ??
            throw new IOException("The approved process could not be started."));
    internal Func<string, MxcFolderAdmission.Metadata?> ReadPhysicalMetadata { get; set; } = MxcFolderAdmission.ReadMetadata;

    public NodeSandboxExecutor(
        NodeSandboxPolicyStore policies,
        Func<string> settingsDirectoryProvider,
        Func<MxcAvailability>? availabilityProvider = null,
        IOpenClawLogger? logger = null,
        Func<ContainerRequest, Task<IMxcProcess>>? spawn = null,
        Func<ContainerRequest, ProbeOutput>? probe = null,
        string? scratchRoot = null,
        Func<MxcRequestContext>? contextProvider = null,
        IFixedFileWorkerResolver? fileWorker = null)
    {
        _policies = policies;
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
        _cleanup = new(_logger);
        _fileWorker = fileWorker;
    }

    internal static string GetDefaultScratchRoot(string? overrideRoot, string localAppData) =>
        Path.Combine(string.IsNullOrWhiteSpace(overrideRoot) ? localAppData : overrideRoot, "OpenClawMxc", "Runs");
    internal static string GetPayloadCacheRoot(string scratchRoot) =>
        Path.Combine(Path.GetDirectoryName(scratchRoot)!, "FileWorkerCache");

    public string CapturePolicy() => _policies.Capture().Fingerprint;
    public void Dispose() => _cleanup.Dispose();

    public Task<T> RunFileAsync<T>(FixedFileWorkload<T> workload, CancellationToken ct = default) =>
        RunAsync(new FileWorkload<T>(workload, _fileWorker), ct);

    internal async Task<T> RunAsync<T>(Workload<T> workload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string scratchError = "The MXC scratch directory must use a valid absolute path. " +
            "Remove or correct OPENCLAW_TRAY_LOCALAPPDATA_DIR and restart OpenClaw.";
        try
        {
            if (!Path.IsPathFullyQualified(_scratchRoot)) throw new ArgumentException();
            _ = Path.GetFullPath(_scratchRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(scratchError, NodeToolErrorCategory.SandboxDenied, NodeToolExecutionMode.Sandbox);
        }
        var policy = _policies.Capture();
        if (workload.ExpectedPolicy is not null && workload.ExpectedPolicy != policy.Fingerprint)
            throw Failure("Permissions changed while the command was awaiting approval. Retry for a fresh approval.",
                NodeToolErrorCategory.ExecPolicyDenied, NodeToolExecutionMode.Sandbox);
        workload.Initialize(policy);
        if (!OwnedOperations.Wait(0))
            throw Failure("Command execution is busy: eight commands running or finishing cleanup. Wait for capacity, then retry.",
                NodeToolErrorCategory.NodeBusy, null);
        try { _cleanup.Reserve(); }
        catch { OwnedOperations.Release(); throw; }

        var stop = new CancellationTokenSource();
        var timeout = workload.ExecutionTimeout(policy.Settings.SandboxTimeoutMs);
        var mode = (int)NodeToolExecutionMode.Sandbox;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var owned = Task.Run(async () =>
        {
            try
            {
                return await ExecuteOwnedAsync(workload, policy, stop.Token,
                    selected => Volatile.Write(ref mode, (int)selected)).ConfigureAwait(false);
            }
            finally
            {
                stop.Dispose();
                OwnedOperations.Release();
            }
        });
        try { return await owned.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Read the command's root status before termination; binary protocols have no partial result.
            var selected = (NodeToolExecutionMode)Volatile.Read(ref mode);
            try { workload.CaptureDeadline(selected); }
            finally
            {
                try { stop.Cancel(); }
                catch (ObjectDisposedException) { }
                _ = ObserveCleanupAsync(owned);
            }
            ct.ThrowIfCancellationRequested();
            return workload.DeadlineResult(selected);
        }
    }

    private async Task<T> ExecuteOwnedAsync<T>(Workload<T> workload, NodeSandboxPolicy policy,
        CancellationToken stop, Action<NodeToolExecutionMode> selectMode)
    {
        MxcOwnedOperation? operation = null;
        FixedFileWorkerLease? image = null;
        var processSettled = true;
        var mode = NodeToolExecutionMode.Sandbox;
        try
        {
            stop.ThrowIfCancellationRequested();
            var availability = _availability();
            if (availability.IsWindowsUnsupported)
            {
                mode = NodeToolExecutionMode.Host;
                selectMode(mode);
            }
            else if (!availability.CanRunSystemRunSandbox)
                throw Failure(string.Join(" ", availability.SystemRunSandboxUnsupportedReasons),
                    NodeToolErrorCategory.SandboxUnavailable, mode);

            operation = MxcOwnedOperation.Create(_scratchRoot);
            operation.CreateScratch();
            var scratch = operation.Scratch;
            var payloadCacheRoot = GetPayloadCacheRoot(_scratchRoot);
            image = workload.ResolveImage(
                mode == NodeToolExecutionMode.Sandbox ? payloadCacheRoot : null, stop);
            var command = workload.Command(image);
            NodeSandboxResources? resources = null;
            ContainerRequest? request = null;
            ProcessStartInfo? host = null;
            if (mode == NodeToolExecutionMode.Host)
                host = UnsupportedWindowsCommandExecutor.CreateStartInfo(command, scratch);
            else
            {
                var physicalScratch = MxcPhysicalPaths.ResolveDirectory(scratch, ReadPhysicalMetadata, requireExisting: true);
                var settings = policy.Settings;
                var context = _context();
                context = context with { ExecutorPrivatePaths = [.. context.ExecutorPrivatePaths, _scratchRoot, payloadCacheRoot] };
                if (image is null)
                    request = MxcRequestBuilder.Build(command, settings, _settingsDirectory(), physicalScratch, context,
                        MxcFolderAdmission.ReadMetadata, ReadPhysicalMetadata);
                else
                {
                    resources = MxcRequestBuilder.ComposeResources(settings, _settingsDirectory(), physicalScratch,
                        context, MxcFolderAdmission.ReadMetadata, ReadPhysicalMetadata, image.PayloadDirectory);
                    if (resources.DataAccess.ProtectedPaths.Any(p => MxcRequestBuilder.Within(image.ImagePath, p)) ||
                        MxcFolderPermissions.EffectiveAccess(image.ImagePath, resources.ReadonlyPaths,
                            resources.ReadwritePaths, resources.DeniedPaths) != SandboxFolderAccess.ReadOnly)
                        throw new NotSupportedException("The fixed file worker conflicts with Node Sandbox permissions.");
                    if (MxcFolderPermissions.EffectiveAccess(physicalScratch, resources.ReadonlyPaths,
                            resources.ReadwritePaths, resources.DeniedPaths) is null or SandboxFolderAccess.NoAccess)
                        throw new NotSupportedException("The operation scratch conflicts with Node Sandbox permissions.");
                    request = MxcRequestBuilder.CreateRequest(command.Argv!, physicalScratch, settings, resources);
                }
                var admission = _probe(request);
                if (admission.Error is not null || admission.Tier != IsolationTier.BaseContainer ||
                    admission.NeedsDaclAugmentation != false || !admission.Probes.BaseContainerSupportsDenyPaths)
                    throw Failure("This complete permission policy requires unsupported containment. " +
                        "MXC BaseContainer with native protected-folder denies and no host ACL mutation is required. Update Windows or select a supported policy.",
                        NodeToolErrorCategory.SandboxDenied, mode);
            }
            stop.ThrowIfCancellationRequested();
            await workload.RevalidateAsync(stop, mode).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            var launch = _policies.Submit(policy, async () =>
            {
                // Cancellation after submission cannot discard a native result.
                processSettled = false;
                MxcProcessLifetime.OwnedProcess process;
                if (mode == NodeToolExecutionMode.Host)
                    process = StartHost(host!);
                else
                {
                    _logger.Info($"[mxc] operation=spawn requested=base-container applied=unknown ro={request!.Filesystem!.ReadonlyPaths.Count} rw={request.Filesystem.ReadwritePaths.Count} denies={request.Filesystem.DeniedPaths.Count}");
                    process = new MxcProcessLifetime.NativeProcess(await _spawn(request).ConfigureAwait(false), _logger);
                }
                // Even a synchronous start must release the ordered dispatcher before
                // the protocol, stream disposal or root observation can execute.
                await Task.Yield();
                return await MxcProcessLifetime.RunAsync(process, policy, resources, workload.StartIo, stop,
                    () => { processSettled = true; workload.ProcessSettled(); }).ConfigureAwait(false);
            }, stop, () => workload.RevalidateAsync(stop, mode));
            if (mode == NodeToolExecutionMode.Host)
                _logger.Warn("[mxc] operation=compatibility executionMode=host reason=windows_unsupported sandboxControlsEnforced=false");
            return await launch.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (StaleNodeSandboxPolicyException)
        {
            throw Failure("Permissions changed before launch. Retry with the current Sandbox policy.",
                NodeToolErrorCategory.ExecPolicyDenied, mode);
        }
        catch (NodeSandboxExecutionException) { throw; }
        catch (NodeCommandException) { throw; }
        catch (NotSupportedException ex)
        {
            throw Failure(ex.Message, mode == NodeToolExecutionMode.Host
                ? NodeToolErrorCategory.CommandUnavailable : NodeToolErrorCategory.SandboxDenied, mode);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[mxc] operation=failed error={ex.GetType().Name} applied=unknown");
            throw mode == NodeToolExecutionMode.Host
                ? Failure("Uncontained compatibility execution failed. Verify the approved executable and working folder before retrying.",
                    NodeToolErrorCategory.CommandUnavailable, mode)
                : Failure("MXC failed to apply or execute this policy. Repair the installation or update Windows before retrying.",
                    NodeToolErrorCategory.SandboxFailure, mode);
        }
        finally
        {
            if (processSettled || operation is null) image?.Dispose();
            else operation.RetainPayload(image);
            if (operation is null) _cleanup.ReleaseUncreated();
            else await _cleanup.CompleteAsync(operation, processSettled).ConfigureAwait(false);
        }
    }

    private async Task ObserveCleanupAsync(Task owned)
    {
        try { await owned.ConfigureAwait(false); }
        catch (OperationCanceledException) { _logger.Info("[mxc] operation=cancel-cleanup-complete"); }
        catch (Exception ex) { _logger.Warn($"[mxc] operation=cancel-cleanup-failed error={ex.GetType().Name}"); }
    }

    private static int EffectiveTimeout(int request, int setting) =>
        request > 0 && setting > 0 ? Math.Min(request, setting) : request > 0 ? request : setting > 0 ? setting : 30_000;

    internal static NodeSandboxExecutionException Failure(string reason, NodeToolErrorCategory category,
        NodeToolExecutionMode? mode) => new(reason, category, mode);

    internal abstract class Workload<T>
    {
        internal abstract int TimeoutMs { get; }
        internal virtual int ExecutionTimeout(int setting) => EffectiveTimeout(TimeoutMs, setting);
        internal virtual void ProcessSettled() { }
        internal virtual string? ExpectedPolicy => null;
        internal virtual void Initialize(NodeSandboxPolicy policy) { }
        internal virtual FixedFileWorkerLease? ResolveImage(string? payloadCacheRoot, CancellationToken stop) => null;
        internal abstract CommandRequest Command(FixedFileWorkerLease? image);
        internal virtual ValueTask RevalidateAsync(CancellationToken stop, NodeToolExecutionMode mode) => ValueTask.CompletedTask;
        internal abstract NodeSandboxIoOperation<T> StartIo(NodeSandboxProcessIo io, CancellationToken stop);
        internal virtual void CaptureDeadline(NodeToolExecutionMode mode) { }
        internal virtual T DeadlineResult(NodeToolExecutionMode mode) =>
            throw Failure("The operation timed out.", NodeToolErrorCategory.Timeout, mode);
    }

    private sealed class FileWorkload<T>(FixedFileWorkload<T> workload, IFixedFileWorkerResolver? resolver) : Workload<T>
    {
        internal override int TimeoutMs => workload.TimeoutMs;
        internal override int ExecutionTimeout(int setting) => workload.Streaming
            ? Math.Clamp(workload.TimeoutMs, 1, FileTransfer.FileLimits.TransferTimeoutMs)
            : base.ExecutionTimeout(setting);
        internal override void ProcessSettled() => workload.ProcessSettled?.Invoke();
        internal override FixedFileWorkerLease ResolveImage(string? payloadCacheRoot, CancellationToken stop)
        {
            var image = resolver?.Resolve(payloadCacheRoot, stop) ??
                throw new NotSupportedException("The fixed file worker is not installed.");
            try
            {
                var path = MxcRequestBuilder.ValidateExecutable(image.ImagePath);
                if (!Path.GetFileName(path).Equals("OpenClaw.FileWorker.exe", StringComparison.OrdinalIgnoreCase) ||
                    !MxcFolderPermissions.SamePath(Path.GetDirectoryName(path)!, image.PayloadDirectory))
                    throw new NotSupportedException("The fixed file worker payload identity is invalid.");
                MxcFolderAdmission.Validate(image.PayloadDirectory, MxcFolderAdmission.ReadMetadata);
                MxcFolderAdmission.Validate(path, MxcFolderAdmission.ReadMetadata);
                if (MxcFolderAdmission.ReadMetadata(image.PayloadDirectory) is not { } directory ||
                    (directory.Attributes & FileAttributes.Directory) == 0 ||
                    MxcFolderAdmission.ReadMetadata(path) is not { } executable ||
                    (executable.Attributes & FileAttributes.Directory) != 0)
                    throw new NotSupportedException("The fixed file worker payload is unavailable.");
                return image;
            }
            catch { image.Dispose(); throw; }
        }

        internal override CommandRequest Command(FixedFileWorkerLease? image) =>
            new() { Argv = [image!.ImagePath, "--stdio"], TimeoutMs = workload.TimeoutMs };

        internal override NodeSandboxIoOperation<T> StartIo(NodeSandboxProcessIo io, CancellationToken stop)
        {
            var protocol = Task.Run(() => workload.Protocol(io, stop), CancellationToken.None);
            return new(CompleteAsync(), SettleAsync(), () => { }, () => protocol.GetAwaiter().GetResult());
            async Task CompleteAsync()
            {
                await protocol.ConfigureAwait(false);
                await io.RootExit.ConfigureAwait(false);
            }
            async Task SettleAsync()
            {
                try { await protocol.ConfigureAwait(false); }
                finally { await io.SettlePipeWorkersAsync().ConfigureAwait(false); }
            }
        }
    }
}
