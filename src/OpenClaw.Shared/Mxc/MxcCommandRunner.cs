using System.Diagnostics;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// The only production system.run runner. Ownership survives cancelled waits and late native launches.
/// Admission slots cover launch, termination, draining and disposal, not just the caller's wait.
/// </summary>
public sealed class MxcCommandRunner : ICommandRunner
{
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
        _scratchRoot = scratchRoot ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawMxc", "Runs");
        _logger = logger ?? NullLogger.Instance;
    }

    public string CapturePolicy() => MxcRequestBuilder.Fingerprint(MxcRequestBuilder.Snapshot(_settings()));

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var settings = MxcRequestBuilder.Snapshot(_settings());
        var fingerprint = MxcRequestBuilder.Fingerprint(settings);
        if (request.ExpectedMxcPolicy is not null && request.ExpectedMxcPolicy != fingerprint)
            return Block("Permissions changed while the command was awaiting approval. Retry for a fresh approval.");
        if (!OwnedOperations.Wait(0))
            return Block("MXC is still cleaning up eight commands. Wait for owned native operations to finish, then retry.");

        var stopwatch = Stopwatch.StartNew();
        var stop = new CancellationTokenSource();
        var timeout = EffectiveTimeout(request.TimeoutMs, settings.SandboxTimeoutMs);
        var executionMode = (int)NodeToolExecutionMode.Sandbox;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > 0) deadline.CancelAfter(timeout);
        var owned = Task.Run(async () =>
        {
            try { return await ExecuteOwnedAsync(request, settings, fingerprint, stop.Token,
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
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Do not dispose a late process or its scratch from this waiter. Its owner remains bounded.
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* The native owner completed at the cancellation boundary. */ }
            _ = ObserveCleanupAsync(owned);
            ct.ThrowIfCancellationRequested();
            return new CommandResult
            {
                ExitCode = -1, TimedOut = true, DurationMs = stopwatch.ElapsedMilliseconds,
                Stderr = "The command deadline expired. Owned process termination and cleanup may still be completing.",
                ExecutionMode = (NodeToolExecutionMode)Volatile.Read(ref executionMode),
                ErrorCategory = NodeToolErrorCategory.Timeout,
            };
        }
    }

    private async Task<CommandResult> ExecuteOwnedAsync(
        CommandRequest command, SettingsData settings, string fingerprint, CancellationToken stop,
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
                if (command.RevalidateApproval is { } hostRevalidate &&
                    !(await hostRevalidate(stop).ConfigureAwait(false)).IsCurrent)
                    return Block("Command approval changed before launch. Retry for a fresh approval.",
                        NodeToolErrorCategory.ExecPolicyDenied, mode);
                if (CapturePolicy() != fingerprint)
                    return Block("Permissions changed before launch. Retry for a fresh approval.",
                        NodeToolErrorCategory.ExecPolicyDenied, mode);
                _logger.Warn("[mxc] operation=compatibility executionMode=host reason=windows_unsupported sandboxControlsEnforced=false");
                return await UnsupportedWindowsCommandExecutor.RunAsync(command, scratch,
                    settings.SandboxMaxOutputBytes > 0 ? settings.SandboxMaxOutputBytes : 4 * 1024 * 1024,
                    stop).ConfigureAwait(false);
            }
            if (!availability.CanRunSystemRunSandbox)
                return Block(string.Join(" ", availability.SystemRunSandboxUnsupportedReasons), NodeToolErrorCategory.SandboxUnavailable);
            Directory.CreateDirectory(scratch);
            var request = MxcRequestBuilder.Build(command, settings, _settingsDirectory(), scratch, _context());
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
                    return Block("Command approval changed before launch. Retry for a fresh approval.",
                        NodeToolErrorCategory.ExecPolicyDenied);
            }
            stop.ThrowIfCancellationRequested();
            if (CapturePolicy() != fingerprint)
                return Block("Permissions changed before launch. Retry for a fresh approval.");

            _logger.Info($"[mxc] operation=spawn requested=base-container applied=unknown ro={request.Filesystem!.ReadonlyPaths.Count} rw={request.Filesystem.ReadwritePaths.Count} denies={request.Filesystem.DeniedPaths.Count}");
            // Never pass caller cancellation to SpawnAsync: the SDK's cancelled wait hides its late result.
            using var process = await _spawn(request).ConfigureAwait(false);
            process.StandardInput?.Dispose();
            using var stdout = process.StandardOutput;
            using var stderr = process.StandardError;
            using var stdoutCloser = process.StandardOutputCloser;
            using var stderrCloser = process.StandardErrorCloser;
            var budget = settings.SandboxMaxOutputBytes > 0 ? settings.SandboxMaxOutputBytes : 4 * 1024 * 1024;
            var output = Task.Run(() => CollectAsync(stdout, budget));
            var error = Task.Run(() => CollectAsync(stderr, budget));
            var wait = process.WaitAsync(CancellationToken.None);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = stop.Register(() => cancelled.TrySetResult());
            var completion = Task.WhenAll((Task)wait, output, error);
            nativeCompletion = completion;
            var finished = await Task.WhenAny(completion, cancelled.Task).ConfigureAwait(false);
            if (stop.IsCancellationRequested || finished == cancelled.Task)
            {
                // Blocking native termination remains with this owner, never the caller's cancellation path.
                await Task.Run(() =>
                {
                    try { process.Kill(); }
                    finally
                    {
                        try { stdoutCloser?.Close(); }
                        finally { stderrCloser?.Close(); }
                    }
                }).ConfigureAwait(false);
            }
            await completion.ConfigureAwait(false);
            var result = await wait.ConfigureAwait(false);
            var captures = await Task.WhenAll(output, error).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            _logger.Info($"[mxc] operation=exit applied=unknown exit={result.ExitCode} timedOut={result.TimedOut}");
            return new CommandResult
            {
                Stdout = captures[0], Stderr = captures[1], ExitCode = result.ExitCode,
                TimedOut = result.TimedOut, ExecutionMode = NodeToolExecutionMode.Sandbox,
                ErrorCategory = result.TimedOut ? NodeToolErrorCategory.Timeout :
                    result.ExitCode == 0 ? NodeToolErrorCategory.None : NodeToolErrorCategory.CommandFailed,
            };
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

    private async Task ObserveCleanupAsync(Task<CommandResult> owned)
    {
        try { await owned.ConfigureAwait(false); }
        catch (OperationCanceledException) { _logger.Info("[mxc] operation=cancel-cleanup-complete"); }
        catch (Exception ex) { _logger.Warn($"[mxc] operation=cancel-cleanup-failed error={ex.GetType().Name}"); }
    }

    internal static async Task<string> CollectAsync(Stream? stream, long byteBudget)
    {
        if (stream is null) return "";
        var cap = (int)Math.Clamp(byteBudget, 0, int.MaxValue - 16);
        var bytes = new byte[8192];
        var prefixLength = 0;
        while (prefixLength < 4)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(prefixLength, 4 - prefixLength)).ConfigureAwait(false);
            if (read == 0) break;
            prefixLength += read;
        }
        // StreamReader can misidentify a BOM when a native pipe fragments it into single-byte reads.
        Encoding encoding = new UTF8Encoding(false, false);
        var bom = 0;
        if (prefixLength >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
        { encoding = new UTF32Encoding(false, false); bom = 4; }
        else if (prefixLength >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
        { encoding = new UTF32Encoding(true, false); bom = 4; }
        else if (prefixLength >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) bom = 3;
        else if (prefixLength >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
        { encoding = Encoding.Unicode; bom = 2; }
        else if (prefixLength >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
        { encoding = Encoding.BigEndianUnicode; bom = 2; }
        var decoder = encoding.GetDecoder();
        var chars = new char[8194];
        var result = new StringBuilder(Math.Min(cap, 8192));
        const string marker = "\n[truncated]";
        var contentCap = Math.Max(0, cap - Encoding.UTF8.GetByteCount(marker));
        var retained = 0;
        var truncated = false;
        char? pendingHigh = null;
        Decode(prefixLength - bom, bom, eof: false);
        while (true)
        {
            var read = await stream.ReadAsync(bytes).ConfigureAwait(false);
            Decode(read, 0, eof: read == 0);
            if (read == 0) break;
        }
        if (truncated && cap >= Encoding.UTF8.GetByteCount(marker)) result.Append(marker);
        return result.ToString();

        void Decode(int byteCount, int offset, bool eof)
        {
            var count = decoder.GetChars(bytes, offset, byteCount, chars, 0, flush: eof);
            if (pendingHigh is { } high)
            {
                if (count > 0 && char.IsLowSurrogate(chars[0]))
                {
                    Append(new string([high, chars[0]]));
                    Array.Copy(chars, 1, chars, 0, --count);
                }
                else Append("\uFFFD");
                pendingHigh = null;
            }
            for (var i = 0; i < count;)
            {
                if (i == count - 1 && char.IsHighSurrogate(chars[i]))
                { pendingHigh = chars[i]; break; }
                var width = char.IsHighSurrogate(chars[i]) && i + 1 < count && char.IsLowSurrogate(chars[i + 1]) ? 2 : 1;
                var chunk = chars.AsSpan(i, width);
                i += width;
                Append(chunk);
            }
        }

        void Append(ReadOnlySpan<char> chunk)
        {
            var cost = Encoding.UTF8.GetByteCount(chunk);
            if (!truncated && retained + cost <= contentCap)
            { result.Append(chunk); retained += cost; }
            else truncated = true;
        }
    }

    private static int EffectiveTimeout(int request, int setting) =>
        request > 0 && setting > 0 ? Math.Min(request, setting) : request > 0 ? request : setting > 0 ? setting : 30_000;

    private CommandResult Block(string reason, NodeToolErrorCategory category = NodeToolErrorCategory.SandboxDenied,
        NodeToolExecutionMode mode = NodeToolExecutionMode.Sandbox)
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
