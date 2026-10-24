using System.Diagnostics;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;
using OpenClaw.TestSupport;
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
    public void Dispose()
    {
        var cleanup = Stopwatch.StartNew();
        while (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32 && cleanup.Elapsed < TimeSpan.FromSeconds(2))
            {
                // Windows can briefly retain fixture directory handles after process exit.
                Thread.Sleep(20);
            }
        }
    }
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void DefaultScratchRoot_IgnoresMissingOrBlankOverride(string? value)
    {
        var root = MxcCommandRunner.GetDefaultScratchRoot(value, _root);
        Assert.Equal(Path.Combine(_root, "OpenClawMxc", "Runs"), root);
        Assert.True(Path.IsPathFullyQualified(root));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void DefaultScratchRoot_PreservesExplicitLocalAppDataRoot()
    {
        var explicitRoot = Path.Combine(_root, "local");
        Assert.Equal(Path.Combine(explicitRoot, "OpenClawMxc", "Runs"),
            MxcCommandRunner.GetDefaultScratchRoot(explicitRoot, Path.Combine(_root, "fallback")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelativeScratchRoot_BlocksBeforeAdmissionOrDirectoryCreation(bool invalidCharacters)
    {
        var root = invalidCharacters ? _root + "\0invalid" : Path.GetRelativePath(Directory.GetCurrentDirectory(), _root);
        var runner = new MxcCommandRunner(() => _settings, () => Path.Combine(_root, "settings"),
            availabilityProvider: () => throw new Exception("must not probe"),
            spawn: _ => throw new Exception("must not spawn"), scratchRoot: root);
        var result = await runner.RunAsync(Command);
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
        Assert.Contains("valid absolute path", result.Stderr);
        Assert.Contains("OPENCLAW_TRAY_LOCALAPPDATA_DIR", result.Stderr);
        Assert.False(Directory.Exists(_root));
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

    [Theory]
    [InlineData("stdout", 1)]
    [InlineData("stderr", 2)]
    [InlineData("both", 3)]
    public async Task UnsupportedWindows_ExitedRootDoesNotWaitForDescendantPipe(string pipe, int length)
    {
        Directory.CreateDirectory(_root);
        var evidence = Environment.GetEnvironmentVariable("OPENCLAW_MXC_REVIEW_ARTIFACTS");
        if (evidence is not null) Directory.CreateDirectory(evidence);
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var identityPath = Path.Combine(_root, $"{pipe}-{iteration}.identity");
            var releasePath = identityPath + ".release";
            var command = FixtureCommand("inherit-mxc-pipes", identityPath, pipe, length.ToString(), releasePath);
            command.Cwd = _root;
            command.TimeoutMs = 30_000;
            using var cancellation = new CancellationTokenSource();
            var execution = Runner(_ => throw new Exception("must not spawn"),
                availability: new(false, false, false, [], isWindowsUnsupported: true))
                .RunAsync(command, cancellation.Token);
            Process? parent = null;
            Process? child = null;
            long elapsed = 0;
            try
            {
                using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(identityPath + ".parent"))
                {
                    Assert.False(execution.IsCompleted, "The fixture exited before publishing its process identities.");
                    await Task.Delay(10, readyDeadline.Token);
                }
                parent = ReadOwnedFixture(identityPath + ".parent");
                child = ReadOwnedFixture(identityPath);
                Assert.False(parent.HasExited);
                Assert.False(child.HasExited);
                Assert.False(execution.IsCompleted);
                File.WriteAllText(releasePath, "release");
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

                // Bound draining after root exit, not cold fixture startup.
                var clock = Stopwatch.StartNew();
                var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(result.TimedOut, result.Stderr);
                Assert.Equal(0, result.ExitCode);
                Assert.StartsWith(new string('o', length), result.Stdout);
                Assert.StartsWith(new string('e', length), result.Stderr);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
                elapsed = clock.ElapsedMilliseconds;
                Assert.False(child.HasExited);
                await WaitForScratchCleanup();
            }
            finally
            {
                try
                {
                    if (child is null && File.Exists(identityPath))
                        child = ReadOwnedFixture(identityPath);
                }
                finally
                {
                    await cancellation.CancelAsync();
                    try
                    {
                        await execution.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        // Cancellation is teardown, not the failure from the drain assertion.
                    }
                    finally
                    {
                        try
                        {
                            if (child is not null)
                            {
                                if (!child.HasExited) child.Kill();
                                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                                if (evidence is not null)
                                {
                                    var name = Path.GetFileName(_root) + "-" + Path.GetFileName(identityPath);
                                    File.Copy(identityPath + ".json", Path.Combine(evidence, name + ".json"));
                                    File.WriteAllText(Path.Combine(evidence, name + ".cleanup.json"),
                                        System.Text.Json.JsonSerializer.Serialize(new { child.Id, Exited = child.HasExited, PostRootExitElapsedMs = elapsed }));
                                }
                            }
                        }
                        finally
                        {
                            child?.Dispose();
                            parent?.Dispose();
                        }
                        await WaitForScratchCleanup();
                    }
                }
            }
        }
    }

    [Fact]
    public async Task UnsupportedWindows_DeadlinePreservesCollectedOutput()
    {
        var command = FixtureCommand("hold", "8000");
        command.TimeoutMs = 1_000;
        var result = await Runner(_ => throw new Exception("must not spawn"),
            availability: new(false, false, false, [], isWindowsUnsupported: true)).RunAsync(command);
        await WaitForScratchCleanup();
        Assert.True(result.TimedOut);
        Assert.Contains("holding-stdout", result.Stdout);
        Assert.Contains("holding-stderr", result.Stderr);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task NativeRootExitRetainsStatusWhenFinalizationCrossesDeadline(int exit)
    {
        var process = new FakeProcess(exit, "out", "err")
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var command = Command;
        command.TimeoutMs = 1_000;
        try
        {
            var running = Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(command);
            await process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(process.WaitCompletion.Task.IsCompleted);
            var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(exit, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Equal(exit == 0 ? NodeToolErrorCategory.None : NodeToolErrorCategory.CommandFailed, result.ErrorCategory);
            Assert.Equal("out", result.Stdout);
            Assert.Equal("err", result.Stderr);
            Assert.False(process.Killed);
            Assert.False(process.Disposed);
            Assert.False(process.Finalized);
            Assert.Equal(0, process.ExitProbesAfterWait);
            Assert.Single(Directory.GetDirectories(_root));
        }
        finally
        {
            process.WaitCompletion.TrySetResult(new() { ExitCode = exit });
            await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForScratchCleanup();
        }
        Assert.True(process.Finalized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRootExitKeepsFinalizationOwnedUntilItCompletes(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess(7, "out", "err")
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var running = Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(Command, cancellation.Token);
        try
        {
            await process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(running.IsCompleted);
            Assert.False(process.Finalized);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            Assert.False(process.Killed);
            Assert.False(process.Disposed);
            Assert.Single(Directory.GetDirectories(_root));
            Assert.Equal(0, process.ExitProbesAfterWait);
        }
        finally
        {
            process.WaitCompletion.TrySetResult(new() { ExitCode = 7 });
            await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForScratchCleanup();
        }
        Assert.True(process.Finalized);
        if (!cancel)
        {
            var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(7, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Equal("out", result.Stdout);
            Assert.Equal("err", result.Stderr);
        }
    }

    [Fact]
    public async Task NativeRunningRootIsObservedBeforeFinalizationStarts()
    {
        var process = new FakeProcess(7) { RootExited = false };
        var running = Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(Command);
        try
        {
            await process.ExitProbed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(process.WaitEntered.Task.IsCompleted);
        }
        finally
        {
            process.RootExited = true;
        }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(process.Killed);
        Assert.True(process.Finalized);
        Assert.True(process.Disposed);
        Assert.Equal(0, process.ExitProbesAfterWait);
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task NativeFinalizationFailureIsSurfacedAndReadersAreOwned()
    {
        var process = new FakeProcess(7)
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var running = Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(Command);
        try
        {
            await process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            process.WaitCompletion.TrySetException(new IOException("synthetic finalization failure"));
        }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NodeToolErrorCategory.SandboxFailure, result.ErrorCategory);
        Assert.True(process.Disposed);
        Assert.False(process.Killed);
        Assert.Equal(0, process.ExitProbesAfterWait);
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task NativeRootObservationFailureStillKillsFinalizesAndClosesReaders()
    {
        using var output = new NativePipe("out");
        using var error = new NativePipe("err");
        var process = new FakeProcess
        {
            RootExited = false,
            ExitProbeAction = () => throw new IOException("synthetic exit probe failure"),
            StandardOutput = output, StandardError = error,
            StandardOutputCloser = new NativeCloser(output), StandardErrorCloser = new NativeCloser(error),
        };
        var result = await Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(Command)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NodeToolErrorCategory.SandboxFailure, result.ErrorCategory);
        Assert.True(process.Killed);
        Assert.True(process.Finalized);
        Assert.True(process.Disposed);
        Assert.True(output.ReaderExited && error.ReaderExited);
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task NativeReaderAndKillFailuresRetainOwnershipUntilFinalizationSettles()
    {
        using var output = new NativePipe("out", fail: true);
        using var error = new NativePipe("err");
        var process = new FakeProcess
        {
            RootExited = false,
            StandardOutput = output, StandardError = error,
            StandardOutputCloser = new NativeCloser(output), StandardErrorCloser = new NativeCloser(error),
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            KillAction = () => throw new IOException("synthetic kill failure"),
        };
        var running = Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(Command);
        try
        {
            await process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(process.Killed);
            Assert.False(running.IsCompleted);
            Assert.False(process.Disposed);
            Assert.Single(Directory.GetDirectories(_root));
            Assert.Equal(0, process.ExitProbesAfterWait);
        }
        finally
        {
            process.WaitCompletion.TrySetResult(new() { ExitCode = 7 });
        }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NodeToolErrorCategory.SandboxFailure, result.ErrorCategory);
        Assert.True(process.Disposed);
        Assert.True(process.Finalized);
        Assert.True(error.ReaderExited);
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task DeadlinePreservesCollectedOutputWithoutWaitingForBlockedNativeKill()
    {
        using var releaseKill = new ManualResetEventSlim();
        var process = new FakeProcess(stdout: "out", stderr: "err")
        {
            RootExited = false,
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            KillAction = () => releaseKill.Wait(),
        };
        var command = Command;
        command.TimeoutMs = 1_000;
        try
        {
            var result = await Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(command)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.TimedOut);
            Assert.False(process.Disposed);
            Assert.Equal("out", result.Stdout);
            Assert.Equal("err", result.Stderr);
        }
        finally
        {
            releaseKill.Set();
            await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForScratchCleanup();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSynchronousReadersUseSdkClosersAndPreserveShortDeadlineOutput(bool rootExited)
    {
        using var output = new NativePipe("out");
        using var error = new NativePipe("err");
        using var releaseClose = new ManualResetEventSlim();
        var outputCloser = new NativeCloser(output, rootExited ? releaseClose : null);
        var errorCloser = new NativeCloser(error);
        var process = new FakeProcess
        {
            RootExited = rootExited,
            StandardOutput = output, StandardError = error,
            StandardOutputCloser = outputCloser, StandardErrorCloser = errorCloser,
            WaitCompletion = rootExited ? null : new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var command = Command;
        command.TimeoutMs = 1_000;
        try
        {
            var running = Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(command);
            await Task.WhenAll(output.Paused.Task, error.Paused.Task).WaitAsync(TimeSpan.FromSeconds(2));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(!rootExited, result.TimedOut);
            Assert.Equal(rootExited ? 0 : -1, result.ExitCode);
            Assert.StartsWith("out", result.Stdout);
            Assert.StartsWith("err", result.Stderr);
            Assert.DoesNotContain("\uFFFD", result.Stdout + result.Stderr);
            if (rootExited)
            {
                Assert.False(process.Killed);
                Assert.False(process.Disposed);
                Assert.Single(Directory.GetDirectories(_root));
            }
        }
        finally
        {
            releaseClose.Set();
            await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(outputCloser.Closed);
            Assert.True(errorCloser.Closed);
            Assert.True(output.ReaderExited && error.ReaderExited);
            await WaitForScratchCleanup();
        }
    }

    [Fact]
    public async Task NativeReaderFailureTerminatesRootAndInterruptsOtherReader()
    {
        using var output = new NativePipe("out", fail: true);
        using var error = new NativePipe("err");
        var process = new FakeProcess
        {
            RootExited = false,
            StandardOutput = output, StandardError = error,
            StandardOutputCloser = new NativeCloser(output), StandardErrorCloser = new NativeCloser(error),
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var result = await Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(Command)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NodeToolErrorCategory.SandboxFailure, result.ErrorCategory);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
        Assert.True(error.ReaderExited);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedScratchUsesVerifiedPhysicalGrantWhileLogicalCleanupRemainsOwned(bool cancelLateSpawn)
    {
        var context = MxcSyntheticContext.Create(_root);
        var logicalRoot = Directory.CreateDirectory(Path.Combine(_root, "logical-runs")).FullName;
        var physicalRoot = Directory.CreateDirectory(Path.Combine(_root, "physical-runs")).FullName;
        string? logicalScratch = null;
        ContainerRequest? captured = null;
        var process = new FakeProcess(stdout: "physical scratch");
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<IMxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var runner = new MxcCommandRunner(() => _settings, () => Path.Combine(_root, "settings"),
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: request =>
            {
                captured = request;
                spawned.TrySetResult();
                return cancelLateSpawn ? late.Task : Task.FromResult<IMxcProcess>(process);
            },
            probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: logicalRoot, contextProvider: () => context)
        {
            ReadPhysicalMetadata = path =>
            {
                var metadata = MxcFolderAdmission.ReadMetadata(path);
                if (!path.StartsWith(logicalRoot + "\\", StringComparison.OrdinalIgnoreCase)) return metadata;
                Assert.True(Directory.Exists(path), "Only already-created owned scratch may be resolved.");
                logicalScratch = path;
                // Two ordinary fixture directories model the two spellings observed in a package.
                // This is not native virtualization proof; the test asserts SDK inputs and logical ownership.
                var physical = Directory.CreateDirectory(Path.Combine(physicalRoot, Path.GetFileName(path))).FullName;
                return metadata!.Value with { FinalPath = physical };
            },
        };
        var running = runner.RunAsync(Command, cancellation.Token);
        try
        {
            await spawned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(captured);
            Assert.NotNull(logicalScratch);
            Assert.StartsWith(physicalRoot + "\\", captured.WorkingDirectory);
            Assert.Contains(captured.WorkingDirectory, captured.Filesystem!.ReadwritePaths);
            Assert.DoesNotContain(logicalScratch, captured.Filesystem.ReadwritePaths);
            if (cancelLateSpawn)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
                Assert.True(Directory.Exists(logicalScratch));
            }
            else Assert.Equal("physical scratch", (await running).Stdout);
        }
        finally
        {
            late.TrySetResult(process);
            await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 100 && Directory.Exists(logicalScratch); i++) await Task.Delay(10);
        }
        Assert.False(Directory.Exists(logicalScratch));
        Assert.Equal(cancelLateSpawn, process.Killed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhysicalResolutionFailureNeverSpawnsOrDowngradesToHost(bool failScratch)
    {
        var context = MxcSyntheticContext.Create(_root);
        var settings = Directory.CreateDirectory(Path.Combine(_root, "settings")).FullName;
        var scratchRoot = Path.Combine(_root, "scratch");
        var spawnCount = 0;
        var runner = new MxcCommandRunner(() => _settings, () => settings,
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: _ => { spawnCount++; throw new Exception("must not spawn"); },
            probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: scratchRoot, contextProvider: () => context)
        {
            ReadPhysicalMetadata = path =>
                (failScratch ? path.StartsWith(scratchRoot, StringComparison.Ordinal) : path == settings)
                    ? throw new UnauthorizedAccessException("owned fixture metadata denial")
                    : MxcFolderAdmission.ReadMetadata(path),
        };
        var result = await runner.RunAsync(Command);
        Assert.Equal(0, spawnCount);
        Assert.Equal(NodeToolExecutionMode.Sandbox, result.ExecutionMode);
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
        Assert.Contains("could not be verified", result.Stderr);
        Assert.Empty(Directory.GetDirectories(scratchRoot));
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
        var telemetry = NodeToolInvocation.GetSandboxTelemetry(result.ExecutionMode, result.ErrorCategory);
        Assert.NotNull(telemetry);
        Assert.True(telemetry.Requested);
        Assert.False(telemetry.Applied);
        Assert.Null(telemetry.Technology);
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
        var process = new FakeProcess { RootExited = false };
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
        process.RootExited = true;
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
            RootExited = false,
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            KillAction = () => releaseKill.Wait(),
        };
        var runner = Runner(_ => Task.FromResult<IMxcProcess>(process));
        var running = runner.RunAsync(Command, cancellation.Token);
        await process.ExitProbed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(process.Disposed);
        Assert.Single(Directory.GetDirectories(_root));
        releaseKill.Set();
        await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForScratchCleanup();
    }

    [Fact]
    public async Task EightActiveCommands_ReportBusyWithoutClaimingCleanupOrContainment()
    {
        using var cancellation = new CancellationTokenSource();
        var processes = Enumerable.Range(0, 8).Select(_ => new FakeProcess
        {
            RootExited = false,
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        }).ToArray();
        var index = -1;
        var runner = Runner(_ => Task.FromResult<IMxcProcess>(processes[Interlocked.Increment(ref index)]));
        var calls = processes.Select(_ => runner.RunAsync(Command, cancellation.Token)).ToArray();
        try
        {
            await Task.WhenAll(processes.Select(p => p.ExitProbed.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            var busy = await runner.RunAsync(Command);
            Assert.Equal(NodeToolErrorCategory.NodeBusy, busy.ErrorCategory);
            Assert.Null(busy.ExecutionMode);
            Assert.Null(busy.SandboxDenialReason);
            Assert.Null(NodeToolInvocation.GetSandboxTelemetry(busy.ExecutionMode, busy.ErrorCategory));
            Assert.Contains("eight commands running or finishing cleanup", busy.Stderr);
            Assert.All(processes, p => Assert.False(p.RootExited || p.Killed || p.Disposed));
            Assert.All(calls, call => Assert.False(call.IsCompleted));
        }
        finally
        {
            cancellation.Cancel();
            foreach (var call in calls)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
            await Task.WhenAll(processes.Select(p => p.DisposedCompletion.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForScratchCleanup();
        }
    }

    [Fact]
    public async Task EightFinalizations_HoldCapacityUntilActualCleanupCompletes()
    {
        using var cancellation = new CancellationTokenSource();
        var processes = Enumerable.Range(0, 8).Select(_ => new FakeProcess
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        }).ToArray();
        var index = -1;
        var runner = Runner(_ => Task.FromResult<IMxcProcess>(processes[Interlocked.Increment(ref index)]));
        var calls = processes.Select(_ => runner.RunAsync(Command, cancellation.Token)).ToArray();
        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitEntered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            foreach (var call in calls)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
            var denied = await runner.RunAsync(Command);
            Assert.Equal(NodeToolErrorCategory.NodeBusy, denied.ErrorCategory);
            Assert.Contains("eight commands", denied.Stderr);
            Assert.Equal(8, Directory.GetDirectories(_root).Length);
            Assert.All(processes, p => Assert.False(p.Disposed || p.Killed || p.Finalized));
        }
        finally
        {
            cancellation.Cancel();
            foreach (var process in processes) process.WaitCompletion!.TrySetResult(new() { ExitCode = 0 });
            await Task.WhenAll(processes.Select(p => p.DisposedCompletion.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForScratchCleanup();
        }
        Assert.All(processes, p => Assert.True(p.Finalized));
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
            Assert.Equal(NodeToolErrorCategory.NodeBusy, denied.ErrorCategory);
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
        var process = new FakeProcess
        {
            RootExited = false,
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var command = Command;
        command.TimeoutMs = 50;
        var result = await Runner(_ => Task.FromResult<IMxcProcess>(process)).RunAsync(command);
        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
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

    private static CommandRequest FixtureCommand(params string[] arguments)
    {
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        var configuration = typeof(MxcCommandRunnerTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        var image = Path.Combine(repo, "tests", "OpenClaw.Shared.TestHost", "bin", configuration,
            "net10.0", "OpenClaw.Shared.TestHost.exe");
        Assert.True(File.Exists(image), image);
        return new() { Argv = [image, "--process-fixture", .. arguments], TimeoutMs = 10_000 };
    }

    private static Process ReadOwnedFixture(string path)
    {
        // The immutable identity is published by rename, whose delete handle can still be closing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var identity = reader.ReadToEnd().Trim().Split('|');
        var child = Process.GetProcessById(int.Parse(identity[0]));
        try
        {
            _ = child.SafeHandle;
            Assert.Equal(long.Parse(identity[1]), child.StartTime.ToUniversalTime().Ticks);
            Assert.Equal("OpenClaw.Shared.TestHost.exe", Path.GetFileName(ProcessImagePath.Read(child.SafeHandle)));
            return child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    internal sealed class FakeProcess : IMxcProcess
    {
        private readonly int _exit;
        private readonly MemoryStream _stdin = new();
        private int _rootExited = 1;
        private int _exitProbesAfterWait;
        public bool StdinClosed => !_stdin.CanWrite;
        public bool Disposed { get; private set; }
        public bool Killed { get; private set; }
        public bool Finalized { get; private set; }
        public bool RootExited { get => Volatile.Read(ref _rootExited) != 0; set => Volatile.Write(ref _rootExited, value ? 1 : 0); }
        public int ExitProbesAfterWait => Volatile.Read(ref _exitProbesAfterWait);
        public Action? KillAction { get; init; }
        public Action? ExitProbeAction { get; init; }
        public TaskCompletionSource<WaitResult>? WaitCompletion { get; set; }
        public TaskCompletionSource ExitProbed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposedCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream? StandardInput => _stdin;
        public uint Id => 0;
        public Stream? StandardOutput { get; init; }
        public Stream? StandardError { get; init; }
        public IMxcStreamCloser? StandardOutputCloser { get; init; }
        public IMxcStreamCloser? StandardErrorCloser { get; init; }
        public IReadOnlyList<string> Warnings => [];
        public ExecutionMetadata? OutputMetadata => null;
        public FakeProcess(int exit = 0, string stdout = "", string stderr = "")
        {
            _exit = exit;
            StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes(stdout));
            StandardError = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
        }
        public WaitResult Wait() => new() { ExitCode = _exit };
        public async Task<WaitResult> WaitAsync(CancellationToken cancellationToken = default)
        {
            WaitEntered.TrySetResult();
            var result = await (WaitCompletion?.Task ?? Task.FromResult(Wait()));
            Finalized = true;
            return result;
        }
        public bool TryGetExitCode(out int exitCode)
        {
            if (WaitEntered.Task.IsCompleted) Interlocked.Increment(ref _exitProbesAfterWait);
            ExitProbeAction?.Invoke();
            var exited = RootExited;
            exitCode = exited ? _exit : 0;
            ExitProbed.TrySetResult();
            return exited;
        }
        public Task<(WaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Production must use bounded drains.");
        public void Kill() { Killed = true; KillAction?.Invoke(); RootExited = true; WaitCompletion?.TrySetResult(Wait()); }
        public void Dispose()
        {
            Disposed = true;
            _stdin.Dispose(); StandardOutput?.Dispose(); StandardError?.Dispose();
            DisposedCompletion.TrySetResult();
        }
    }

    private sealed class NativeCloser(NativePipe pipe, ManualResetEventSlim? release = null) : IMxcStreamCloser
    {
        public bool Closed { get; private set; }
        public void Close() { release?.Wait(); Closed = true; pipe.CloseRead(); }
        public void Dispose() { }
    }

    private sealed class NativePipe(string text, bool fail = false) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        private readonly ManualResetEventSlim _closed = new();
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReaderExited { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position < Length) return base.Read(buffer, offset, Math.Min(1, count));
            Paused.TrySetResult();
            if (fail) throw new IOException("synthetic reader failure");
            _closed.Wait();
            ReaderExited = true;
            return 0;
        }
        public void CloseRead() => _closed.Set();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _closed.Dispose();
            base.Dispose(disposing);
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
