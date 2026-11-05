using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>Finite system.run approval and text-result adapter over the shared node executor.</summary>
public sealed class MxcCommandRunner : ICommandRunner
{
    private const int StatusDllInitFailed = unchecked((int)0xC0000142);
    private readonly NodeSandboxExecutor _executor;
    public string Name => "mxc";
    internal Func<string, MxcFolderAdmission.Metadata?> ReadPhysicalMetadata
    {
        get => _executor.ReadPhysicalMetadata;
        init => _executor.ReadPhysicalMetadata = value;
    }

    public MxcCommandRunner(NodeSandboxExecutor executor) => _executor = executor;

    public MxcCommandRunner(
        Func<SettingsData> settingsProvider,
        Func<string> settingsDirectoryProvider,
        Func<MxcAvailability>? availabilityProvider = null,
        IOpenClawLogger? logger = null,
        Func<ContainerRequest, Task<IMxcProcess>>? spawn = null,
        Func<ContainerRequest, ProbeOutput>? probe = null,
        string? scratchRoot = null,
        Func<MxcRequestContext>? contextProvider = null)
        : this(new NodeSandboxExecutor(new NodeSandboxPolicyStore(settingsProvider), settingsDirectoryProvider,
            availabilityProvider, logger, spawn, probe, scratchRoot, contextProvider)) { }

    public string CapturePolicy() => _executor.CapturePolicy();

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        CommandResult result;
        try { result = await _executor.RunAsync(new ApprovedCommand(request), ct).ConfigureAwait(false); }
        catch (NodeSandboxExecutionException ex)
        {
            result = new()
            {
                ExitCode = -1, Stderr = ex.Message, ExecutionMode = ex.ExecutionMode, ErrorCategory = ex.Category,
                SandboxDenialReason = ex.Category == NodeToolErrorCategory.SandboxDenied
                    ? NodeToolSandboxDenialReason.UnsupportedSandboxRequest : null,
            };
        }
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        return WithInitializationContext(result);
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
        result.Stderr = string.IsNullOrWhiteSpace(result.Stderr) ? hint : result.Stderr + Environment.NewLine + hint;
        return result;
    }

    internal static async Task<string> CollectAsync(Stream? stream, long byteBudget)
    {
        var capture = new CommandOutputCapture.BoundedStream(byteBudget);
        await capture.CollectAsync(stream).ConfigureAwait(false);
        return capture.Snapshot();
    }

    private sealed class ApprovedCommand(CommandRequest command) : NodeSandboxExecutor.Workload<CommandResult>
    {
        private CommandOutputCapture _capture = null!;
        private CommandResult? _deadline;
        internal override int TimeoutMs => command.TimeoutMs;
        internal override string? ExpectedPolicy => command.ExpectedMxcPolicy;
        internal override CommandRequest Command(FixedFileWorkerLease? image) => command;
        internal override void Initialize(NodeSandboxPolicy policy)
        {
            var budget = policy.Settings.SandboxMaxOutputBytes;
            _capture = new(budget > 0 ? budget : 4 * 1024 * 1024);
        }

        internal override async ValueTask RevalidateAsync(CancellationToken stop, NodeToolExecutionMode mode)
        {
            if (command.RevalidateApproval is not { } revalidate) return;
            var authorization = await revalidate(stop).ConfigureAwait(false);
            if (authorization.IsCurrent) return;
            throw authorization.IsPolicyDenial
                ? NodeSandboxExecutor.Failure("Command approval changed before launch. Retry for a fresh approval.",
                    NodeToolErrorCategory.ExecPolicyDenied, mode)
                : NodeSandboxExecutor.Failure("Command approval could not be revalidated before launch. Retry for a fresh approval.",
                    NodeToolErrorCategory.InternalFailure, mode);
        }

        internal override NodeSandboxIoOperation<CommandResult> StartIo(NodeSandboxProcessIo io, CancellationToken stop)
        {
            io.CloseInput();
            _capture.BindRootExit(io.RootExit, () => (io.RootExit.Result, false));
            var output = io.ExecutionMode == NodeToolExecutionMode.Sandbox
                ? Task.Run(() => _capture.StandardOutput.Collect(io.StandardOutput))
                : _capture.StandardOutput.CollectAsync(io.StandardOutput, stop);
            var error = io.ExecutionMode == NodeToolExecutionMode.Sandbox
                ? Task.Run(() => _capture.StandardError.Collect(io.StandardError))
                : _capture.StandardError.CollectAsync(io.StandardError, stop);
            var drains = Task.WhenAll(output, error);
            return new(ReadyAsync(), drains, Interrupt, () => _capture.Snapshot(io.ExecutionMode));

            async Task ReadyAsync()
            {
                await CommandOutputCapture.WaitForExitOrInterruptionAsync(io.RootExit, output, error, stop).ConfigureAwait(false);
                if (io.RootExit.IsCompletedSuccessfully)
                    await CommandOutputCapture.DrainAfterExitAsync(drains, stop).ConfigureAwait(false);
            }
            void Interrupt()
            {
                _capture.CaptureRootExit();
                _capture.StandardOutput.Interrupt();
                _capture.StandardError.Interrupt();
            }
        }

        internal override void CaptureDeadline(NodeToolExecutionMode mode) =>
            _deadline = _capture.Snapshot(mode, deadlineExpired: true);
        internal override CommandResult DeadlineResult(NodeToolExecutionMode mode) => _deadline!;
    }
}
