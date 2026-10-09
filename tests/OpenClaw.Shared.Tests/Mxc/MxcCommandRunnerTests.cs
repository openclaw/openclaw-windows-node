using System.Diagnostics;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

[Collection("MxcOwnership")]
public sealed class MxcCommandRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));
    private SettingsData _settings = new();
    private static CommandRequest Command => new() { Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")], TimeoutMs = 10_000 };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private MxcCommandRunner Runner(Func<ContainerRequest, Task<IMxcProcess>> spawn, Func<ContainerRequest, ProbeOutput>? probe = null,
        MxcAvailability? availability = null) => new(() => _settings, () => Path.Combine(_root, "settings"),
            () => availability ?? new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: spawn, probe: probe ?? (_ => MxcAvailabilityTests.BaseProbe()), scratchRoot: _root);

    [Fact]
    public async Task ApprovedCommand_ExecutesOnceWithTypedRequestAndMapsNonzero()
    {
        ContainerRequest? captured = null;
        var process = new FakeProcess(42, "output", "error");
        var runner = Runner(request => { captured = request; return Task.FromResult<IMxcProcess>(process); });
        var result = await runner.RunAsync(Command);
        Assert.Equal(42, result.ExitCode);
        Assert.Equal("output", result.Stdout);
        Assert.Equal("error", result.Stderr);
        Assert.Equal(NodeToolExecutionMode.Sandbox, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.CommandFailed, result.ErrorCategory);
        Assert.True(process.Disposed);
        Assert.True(process.StdinClosed);
        Assert.NotNull(captured);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task UnavailableSdk_BlocksBeforeSpawnWithoutAnyHostRoute()
    {
        var calls = 0;
        var runner = Runner(_ => { calls++; throw new Exception(); },
            availability: new(false, false, false, ["Repair MXC."]));
        var result = await runner.RunAsync(Command);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(NodeToolErrorCategory.SandboxUnavailable, result.ErrorCategory);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConfirmedUnsupportedWindows_ExecutesApprovedCarrierWithHostDiagnostics()
    {
        var runner = Runner(_ => throw new Exception("SDK must not spawn"),
            availability: new(false, false, false, [], isWindowsUnsupported: true));
        var command = new CommandRequest
        {
            Argv = [Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/s", "/c", "echo compatibility"],
            TimeoutMs = 10_000,
        };
        var result = await runner.RunAsync(command);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(NodeToolExecutionMode.Host, result.ExecutionMode);
        Assert.Contains("compatibility", result.Stdout);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task UnsupportedWindows_RevalidatesApprovalBeforeHostSpawn()
    {
        var command = Command;
        command.RevalidateApproval = _ => ValueTask.FromResult(
            OpenClaw.Shared.ExecApprovals.ExecApprovalRevalidationResult.NotCurrent("revoked"));
        var result = await Runner(_ => throw new Exception("must not spawn"),
            availability: new(false, false, false, [], isWindowsUnsupported: true)).RunAsync(command);
        Assert.Equal(NodeToolErrorCategory.ExecPolicyDenied, result.ErrorCategory);
        Assert.Equal(NodeToolExecutionMode.Host, result.ExecutionMode);
    }

    [Fact]
    public async Task UnsupportedWindows_PreservesApprovedCwdAndBoundsOutput()
    {
        Directory.CreateDirectory(_root);
        _settings.SandboxMaxOutputBytes = 512;
        var command = new CommandRequest
        {
            Argv = [Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/s", "/c",
                "cd & for /l %i in (1,1,1000) do @echo output"],
            Cwd = _root, TimeoutMs = 10_000,
        };
        var result = await Runner(_ => throw new Exception("must not spawn"),
            availability: new(false, false, false, [], isWindowsUnsupported: true)).RunAsync(command);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_root, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(result.Stdout) <= 512);
        Assert.Contains("[truncated]", result.Stdout);
    }

    [Fact]
    public async Task UnsupportedWindows_DeadlineRetainsHostModeAndCleansOwnedProcess()
    {
        var command = new CommandRequest
        {
            Argv = [Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/s", "/c",
                "for /l %i in (1,0,2) do @rem wait"],
            TimeoutMs = 250,
        };
        var result = await Runner(_ => throw new Exception("must not spawn"),
            availability: new(false, false, false, [], isWindowsUnsupported: true)).RunAsync(command);
        Assert.True(result.TimedOut);
        Assert.Equal(NodeToolExecutionMode.Host, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.Timeout, result.ErrorCategory);
        Assert.Null(result.SandboxDenialReason);
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task UnsupportedWindows_CallerEnvironmentStillRejected()
    {
        var command = Command;
        command.Env = new Dictionary<string, string> { ["BYPASS"] = "value" };
        var result = await Runner(_ => throw new Exception("must not spawn"),
            availability: new(false, false, false, [], isWindowsUnsupported: true)).RunAsync(command);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("environment", result.Stderr);
        Assert.Equal(NodeToolExecutionMode.Host, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.CommandUnavailable, result.ErrorCategory);
        Assert.Null(result.SandboxDenialReason);
    }

    [Theory]
    [InlineData(SystemRunFilesystemScope.UserFilesReadOnly)]
    [InlineData(SystemRunFilesystemScope.UserFilesReadWrite)]
    public async Task UserFilesScope_UsesInjectedSyntheticPolicy(SystemRunFilesystemScope scope)
    {
        _settings.SystemRunFilesystemScope = scope;
        var context = MxcSyntheticContext.Create(_root);
        ContainerRequest? captured = null;
        var runner = new MxcCommandRunner(() => _settings, () => Path.Combine(_root, "settings"),
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: request => { captured = request; return Task.FromResult<IMxcProcess>(new FakeProcess()); },
            probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: Path.Combine(_root, "scratch"),
            contextProvider: () => context);
        Assert.Equal(0, (await runner.RunAsync(Command)).ExitCode);
        Assert.NotNull(captured);
        Assert.DoesNotContain(Path.GetPathRoot(_root)!, captured.Filesystem!.ReadonlyPaths);
    }

    [Fact]
    public async Task CompleteRequestDaclDowngrade_IsRejectedBeforeSpawn()
    {
        var probe = new ProbeOutput
        {
            Tier = IsolationTier.AppContainerDacl, NeedsDaclAugmentation = true,
            Probes = MxcAvailabilityTests.BaseProbe().Probes,
        };
        var result = await Runner(_ => throw new Exception("must not spawn"), _ => probe).RunAsync(Command);
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
    }

    [Fact]
    public async Task NativeSpawnFailure_IsExplicitNeverFallback()
    {
        var result = await Runner(_ => throw new MxcException(ErrorCode.BackendError, "failed")).RunAsync(Command);
        Assert.Equal(NodeToolErrorCategory.SandboxFailure, result.ErrorCategory);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task MalformedFolderPath_ReportsPolicyErrorBeforeSpawn()
    {
        _settings.SandboxCustomFolders = [new() { Path = _root + "\0invalid", Access = SandboxFolderAccess.ReadOnly }];
        var result = await Runner(_ => throw new Exception("must not spawn")).RunAsync(Command);
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
        Assert.Contains("Review Node Sandbox settings", result.Stderr);
        Assert.DoesNotContain("Repair the installation", result.Stderr);
    }

    [Fact]
    public async Task PermissionsChangedDuringAdmission_RejectsPendingCommand()
    {
        var runner = Runner(_ => throw new Exception("must not spawn"), _ =>
        {
            _settings.SystemRunAllowOutbound = true;
            return MxcAvailabilityTests.BaseProbe();
        });
        var result = await runner.RunAsync(Command);
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
        Assert.Contains("changed before launch", result.Stderr);
    }

    [Fact]
    public async Task ApprovalRevokedDuringNativeAdmission_IsRevalidatedBeforeSpawn()
    {
        var command = Command;
        command.RevalidateApproval = _ => ValueTask.FromResult(
            OpenClaw.Shared.ExecApprovals.ExecApprovalRevalidationResult.NotCurrent("revoked"));
        var result = await Runner(_ => throw new Exception("must not spawn")).RunAsync(command);
        Assert.Equal(NodeToolErrorCategory.ExecPolicyDenied, result.ErrorCategory);
    }

    [Fact]
    public async Task ApprovalSnapshot_RejectsPolicyChangedBeforeRun()
    {
        var runner = Runner(_ => throw new Exception("must not spawn"));
        var command = Command;
        command.ExpectedMxcPolicy = runner.CapturePolicy();
        _settings.SandboxClipboard = SandboxClipboardMode.Both;
        var result = await runner.RunAsync(command);
        Assert.Contains("awaiting approval", result.Stderr);
    }

    [Fact]
    public async Task RunningCommand_KeepsPolicyWhenSettingsChange()
    {
        var process = new FakeProcess();
        process.WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawned = new TaskCompletionSource<ContainerRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Runner(request =>
        {
            spawned.SetResult(request);
            return Task.FromResult<IMxcProcess>(process);
        });
        var running = runner.RunAsync(Command);
        var original = await spawned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _settings.SystemRunAllowOutbound = true;
        process.WaitCompletion.SetResult(new() { ExitCode = 0 });
        Assert.Equal(0, (await running).ExitCode);
        Assert.Equal(NetworkAction.Deny, original.Network!.Egress!.Default);
        Assert.False(process.Killed);
    }

    [Fact]
    public async Task CancellationDuringLateSpawn_ReturnsPromptlyAndRetainsScratchUntilProcessOwned()
    {
        using var cancellation = new CancellationTokenSource();
        var late = new TaskCompletionSource<IMxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess();
        var runner = Runner(_ => { entered.SetResult(); return late.Task; });
        var running = runner.RunAsync(Command, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Single(Directory.GetDirectories(_root));
        late.SetResult(process);
        await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(process.Killed);
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task CancellationWithBlockedKill_ReturnsPromptlyAndCleanupRetainsOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        using var releaseKill = new ManualResetEventSlim();
        var process = new FakeProcess
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            KillAction = () => releaseKill.Wait(),
        };
        var runner = Runner(_ => Task.FromResult<IMxcProcess>(process));
        var running = runner.RunAsync(Command, cancellation.Token);
        await process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(process.Disposed);
        Assert.Single(Directory.GetDirectories(_root));
        releaseKill.Set();
        await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task EightLateLaunches_HoldCapacityUntilActualCleanupCompletes()
    {
        using var cancellation = new CancellationTokenSource();
        var launches = new System.Collections.Concurrent.ConcurrentBag<TaskCompletionSource<IMxcProcess>>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var runner = Runner(_ =>
        {
            var late = new TaskCompletionSource<IMxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
            launches.Add(late);
            if (Interlocked.Increment(ref count) == 8) entered.TrySetResult();
            return late.Task;
        });
        var calls = Enumerable.Range(0, 8).Select(_ => runner.RunAsync(Command, cancellation.Token)).ToArray();
        var processes = new List<FakeProcess>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            foreach (var call in calls)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
            var denied = await runner.RunAsync(Command);
            Assert.Equal(NodeToolErrorCategory.SandboxDenied, denied.ErrorCategory);
            Assert.Contains("eight commands", denied.Stderr);
            Assert.Equal(8, Directory.GetDirectories(_root).Length);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var launch in launches)
            {
                var process = new FakeProcess();
                if (launch.TrySetResult(process)) processes.Add(process);
            }
            await Task.WhenAll(processes.Select(p => p.DisposedCompletion.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForScratchCleanup();
        }
    }

    [Fact]
    public async Task Timeout_IsNotCallerCancellation_UsesOneDeadline()
    {
        var process = new FakeProcess { WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var command = Command;
        command.TimeoutMs = 50;
        var result = await Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(command);
        Assert.True(result.TimedOut);
        Assert.Equal(NodeToolErrorCategory.Timeout, result.ErrorCategory);
        await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForScratchCleanup();
    }

    [CollectionDefinition("MxcOwnership", DisableParallelization = true)]
    public sealed class MxcOwnershipCollection;

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf16be")]
    public async Task Collector_DecodesFragmentedBomsAndBoundsReturnedUtf8Bytes(string name)
    {
        Encoding encoding = name switch
        {
            "utf16" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode, _ => new UTF8Encoding(true),
        };
        var value = "界🙂é";
        using var stream = new FragmentedStream(encoding.GetPreamble().Concat(encoding.GetBytes(value)).ToArray());
        Assert.Equal(value, await MxcCommandRunner.CollectAsync(stream, 100));
        using var longStream = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(value, 100))));
        var truncated = await MxcCommandRunner.CollectAsync(longStream, 64);
        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= 64);
        Assert.EndsWith("[truncated]", truncated);
        Assert.DoesNotContain("\uFFFD", truncated);
        Assert.Equal(longStream.Length, longStream.Position);
    }

    private async Task WaitForScratchCleanup()
    {
        for (var i = 0; i < 100 && Directory.GetDirectories(_root).Length > 0; i++) await Task.Delay(10);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    internal sealed class FakeProcess : IMxcProcess
    {
        private readonly int _exit;
        private readonly MemoryStream _stdin = new();
        public bool StdinClosed => !_stdin.CanWrite;
        public bool Disposed { get; private set; }
        public bool Killed { get; private set; }
        public Action? KillAction { get; init; }
        public TaskCompletionSource<WaitResult>? WaitCompletion { get; set; }
        public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposedCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream? StandardInput => _stdin;
        public uint Id => 0;
        public Stream? StandardOutput { get; }
        public Stream? StandardError { get; }
        public IMxcStreamCloser? StandardOutputCloser => null;
        public IMxcStreamCloser? StandardErrorCloser => null;
        public IReadOnlyList<string> Warnings => [];
        public ExecutionMetadata? OutputMetadata => null;
        public FakeProcess(int exit = 0, string stdout = "", string stderr = "")
        {
            _exit = exit;
            StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes(stdout));
            StandardError = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
        }
        public WaitResult Wait() => new() { ExitCode = _exit };
        public Task<WaitResult> WaitAsync(CancellationToken cancellationToken = default)
        { WaitEntered.TrySetResult(); return WaitCompletion?.Task ?? Task.FromResult(Wait()); }
        public bool TryGetExitCode(out int exitCode) { exitCode = _exit; return true; }
        public Task<(WaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Production must use bounded drains.");
        public void Kill() { Killed = true; KillAction?.Invoke(); WaitCompletion?.TrySetResult(Wait()); }
        public void Dispose()
        {
            Disposed = true;
            _stdin.Dispose(); StandardOutput?.Dispose(); StandardError?.Dispose();
            DisposedCompletion.TrySetResult();
        }
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
