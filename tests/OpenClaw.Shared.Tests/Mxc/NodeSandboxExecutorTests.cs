using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Mxc.Sdk.V1;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Telemetry;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

[Collection("MxcOwnership")]
public sealed class NodeSandboxExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));
    private readonly MxcRequestContext _context;
    private readonly WorkerResolver _worker;
    private string SettingsDirectory => Path.Combine(_root, "settings");
    private string Runs => Path.Combine(_root, "runs");
    private static MxcAvailability Supported => new(true, false, true, [],
        isolationTier: "base-container", supportsProtectedPathDenies: true);
    private static CommandRequest Command => new()
    {
        Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")], TimeoutMs = 10_000,
    };

    public NodeSandboxExecutorTests()
    {
        _context = MxcSyntheticContext.Create(_root);
        _worker = new(Path.Combine(_root, "product", "Workers", "FileWorker", "win-x64"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private NodeSandboxExecutor Executor(NodeSandboxPolicyStore policy,
        Func<ContainerRequest, Task<IMxcProcess>> spawn,
        Func<MxcAvailability>? availability = null, Func<ContainerRequest, ProbeOutput>? probe = null,
        IOpenClawLogger? logger = null) =>
        new(policy, () => SettingsDirectory, availability ?? (() => Supported), spawn: spawn,
            probe: probe ?? (_ => MxcAvailabilityTests.BaseProbe()), scratchRoot: Runs,
            contextProvider: () => _context, fileWorker: _worker, logger: logger);

    [Fact]
    public async Task ManagedWriteIsAdmittedBeforeLaunchWithoutGrantingLegacyPathOrParent()
    {
        var legacy = Path.Combine(_root, "configured-but-unused");
        var settings = new SettingsData { SandboxUploadStagingPath = legacy };
        ContainerRequest? launched = null;
        using var executor = Executor(new(settings), request =>
        {
            launched = request;
            return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess());
        });
        string? managed = null;
        await executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            managed = io.FileInvocation!.UploadRoot;
            Assert.Equal(FileOperation.Upload, io.FileInvocation!.Operation);
            Assert.Equal(Path.GetDirectoryName(Runs), Path.GetDirectoryName(managed));
            Assert.Matches("^Uploads-[a-f0-9]{64}$", Path.GetFileName(managed));
            Assert.NotEqual(legacy, managed);
            Assert.Contains(managed, launched!.Filesystem!.ReadwritePaths);
            Assert.DoesNotContain(Path.GetDirectoryName(managed)!, launched!.Filesystem!.ReadwritePaths);
            Assert.Equal(SandboxFolderAccess.ReadWrite, io.Resources!.DataAccess.EffectiveAccess(managed));
            Assert.Null(io.Resources!.DataAccess.EffectiveAccess(legacy));
            Assert.DoesNotContain(legacy, launched!.Filesystem!.ReadwritePaths);
            return Task.FromResult(true);
        }, 10_000) { Invocation = new(FileOperation.Upload), ReserveUpload = _ => null });
        Assert.True(Directory.Exists(managed));
        Assert.Empty(Directory.GetFileSystemEntries(managed!));
        using (var owned = ManagedUploadRootLease.Open(managed!)) Assert.True(owned.Complete);
        await executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            Assert.Equal(FileOperation.Fetch, io.FileInvocation!.Operation);
            Assert.Equal(SandboxFolderAccess.ReadOnly, io.Resources!.DataAccess.EffectiveAccess(managed!));
            Assert.DoesNotContain(managed!, launched!.Filesystem!.ReadwritePaths);
            return Task.FromResult(true);
        }, 10_000) { Invocation = new(FileOperation.Fetch) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservedUploadCannotMutateAfterPolicyReplacementOrCancellation(bool cancelBeforeCreate)
    {
        using var cancel = new CancellationTokenSource();
        var policies = new NodeSandboxPolicyStore(new SettingsData());
        var root = ManagedUploadPolicy.ResolvePath(Runs, SettingsDirectory);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservations = 0;
        var settlements = 0;
        using var executor = Executor(policies, _ => throw new Exception("Refused bootstrap must not spawn."));
        var run = executor.RunFileAsync(new FixedFileWorkload<bool>((_, _) => Task.FromResult(true), 10_000)
        {
            Invocation = new(FileOperation.Upload),
            ReserveUpload = _ =>
            {
                reservations++;
                Assert.False(Directory.Exists(root));
                if (cancelBeforeCreate) cancel.Cancel();
                else policies.Persist(new SettingsData { SystemRunAllowOutbound = true }, () => { });
                return null;
            },
            UploadReservationSettled = () => { settlements++; settled.TrySetResult(); },
        }, cancel.Token);
        if (cancelBeforeCreate) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else Assert.Equal(NodeToolErrorCategory.ExecPolicyDenied,
            (await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => run)).Category);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reservations);
        Assert.Equal(1, settlements);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task MetadataCollisionSettlesReservationWithoutAdoptingOrLaunching()
    {
        var root = ManagedUploadPolicy.ResolvePath(Runs, SettingsDirectory);
        var settled = 0;
        using var executor = Executor(new(new SettingsData()), _ => throw new Exception("A collision must not spawn."));
        await Assert.ThrowsAsync<FileCommandException>(() => executor.RunFileAsync(new FixedFileWorkload<bool>(
            (_, _) => Task.FromResult(true), 10_000)
        {
            Invocation = new(FileOperation.Upload),
            ReserveUpload = _ => { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "foreign"), "keep"); return null; },
            UploadReservationSettled = () => settled++,
        }));
        Assert.Equal(1, settled);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "foreign")));
        Assert.Throws<FileCommandException>(() => ManagedUploadRootLease.Open(root));
    }

    [Fact]
    public async Task PostBootstrapProbeFailureRetainsEmptyOwnedRootAndSettlesWithoutSpawning()
    {
        var root = ManagedUploadPolicy.ResolvePath(Runs, SettingsDirectory);
        var settled = 0;
        using var executor = Executor(new(new SettingsData()), _ => throw new Exception("A rejected post-bootstrap probe cannot spawn."),
            probe: _ => Directory.Exists(root) ? throw new NotSupportedException("Controlled probe refusal.") : MxcAvailabilityTests.BaseProbe());
        await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(
            new FixedFileWorkload<bool>((_, _) => Task.FromResult(true), 10_000)
            { Invocation = new(FileOperation.Upload), ReserveUpload = _ => null, UploadReservationSettled = () => settled++ }));
        Assert.Equal(1, settled);
        using var retained = ManagedUploadRootLease.Open(root);
        Assert.True(retained.Complete);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task UncertainLaunchRetainsRootPinsAndDoesNotReleaseUploadReservation()
    {
        var root = ManagedUploadPolicy.ResolvePath(Runs, SettingsDirectory);
        var settled = 0;
        var executor = Executor(new(new SettingsData()), _ => Task.FromException<IMxcProcess>(new IOException("Uncertain SDK launch.")));
        try
        {
            await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(
                new FixedFileWorkload<bool>((_, _) => Task.FromResult(true), 10_000)
                { Invocation = new(FileOperation.Upload), ReserveUpload = _ => null, UploadReservationSettled = () => settled++ }));
            Assert.Equal(0, settled);
            Assert.Throws<IOException>(() => Directory.Move(root, root + ".moved"));
        }
        finally { executor.Dispose(); }
        Directory.Move(root, root + ".moved");
        Assert.True(Directory.Exists(root + ".moved"));
        Assert.Equal(0, settled);
    }

    [Fact]
    public async Task ManagedReapCannotCreateAMissingBoundary()
    {
        var root = ManagedUploadPolicy.ResolvePath(Runs, SettingsDirectory);
        using var executor = Executor(new(new SettingsData()), _ => Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess()));
        await executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            Assert.Equal(root, io.FileInvocation!.UploadRoot);
            Assert.False(Directory.Exists(root));
            return Task.FromResult(true);
        }, 10_000) { Invocation = new(FileOperation.ReapUploads, root) });
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task UploadWithoutInventoryOrWithInvalidBudgetCannotCreateBoundary()
    {
        var root = ManagedUploadPolicy.ResolvePath(Runs, SettingsDirectory);
        using var executor = Executor(new(new SettingsData { SandboxMaxTransferBytes = 2 }),
            _ => throw new Exception("Invalid upload must not spawn."));
        var invalid = await Assert.ThrowsAsync<FileCommandException>(() => executor.RunFileAsync(
            new FixedFileWorkload<bool>((_, _) => Task.FromResult(true), 10_000)
            { Invocation = new(FileOperation.Upload, UploadBytes: 3), ReserveUpload = _ => throw new Exception("Budget must precede reservation.") }));
        Assert.Equal("FILE_TOO_LARGE", invalid.Code);
        var missing = await Assert.ThrowsAsync<FileCommandException>(() => executor.RunFileAsync(
            new FixedFileWorkload<bool>((_, _) => Task.FromResult(true), 10_000) { Invocation = new(FileOperation.Upload) }));
        Assert.Equal("UPLOAD_CLEANUP_BLOCKED", missing.Code);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ExistingUnownedManagedNameCannotAuthorizeAnUploadLaunch()
    {
        var root = ManagedUploadPolicy.ResolvePath(Runs, SettingsDirectory);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "sentinel"), "keep");
        using var executor = Executor(new(new SettingsData()), _ =>
            throw new Exception("An unowned staging name must not authorize launch."));
        await Assert.ThrowsAsync<FileCommandException>(() => executor.RunFileAsync(
            new FixedFileWorkload<bool>((_, _) => throw new Exception("No IO before ownership admission."), 10_000)
            { Invocation = new(FileOperation.Upload) }));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "sentinel")));
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public async Task RememberedLegacyReapDoesNotReceiveManagedWriteAuthority()
    {
        var legacy = Path.Combine(_root, "legacy");
        var expected = new UploadRootIdentity(legacy, "17", "23");
        using var executor = Executor(new(new SettingsData()), _ =>
            Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess()));
        await executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            Assert.Equal(legacy, io.FileInvocation!.UploadRoot);
            Assert.Equal(expected, io.FileInvocation!.ExpectedRoot);
            Assert.Null(io.Resources!.DataAccess.EffectiveAccess(legacy));
            Assert.Empty(io.Resources!.DataAccess.ReadwritePaths);
            return Task.FromResult(true);
        }, 10_000) { Invocation = new(FileOperation.ReapUploads, legacy, expected) });
        Assert.False(Directory.Exists(legacy));
    }

    [Theory]
    [InlineData(SandboxFolderAccess.ReadOnly)]
    [InlineData(SandboxFolderAccess.NoAccess)]
    public async Task ManagedUploadCannotOverrideAnExplicitAncestorRestriction(SandboxFolderAccess access)
    {
        var settings = new SettingsData { SandboxCustomFolders = [new() { Path = _root, Access = access }] };
        using var executor = Executor(new(settings), _ => throw new Exception("A restricted upload must not launch."));
        var error = await Assert.ThrowsAsync<FileCommandException>(() => executor.RunFileAsync(
            new FixedFileWorkload<bool>((_, _) => throw new Exception("No file IO before admission."), 10_000)
            { Invocation = new(FileOperation.Upload) }));
        Assert.Equal("PERMISSION_DENIED", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLifecycleDiagnosticsWaitForSdkFinalizationAndExcludeContent(bool binary)
    {
        var logger = new CapturingLogger();
        var process = new MxcCommandRunnerTests.FakeProcess(23, "private output", "private error")
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var executor = Executor(new(new SettingsData()),
            _ => Task.FromResult<IMxcProcess>(process), logger: logger);
        Task execution = binary
            ? executor.RunFileAsync(Noop())
            : new MxcCommandRunner(executor).RunAsync(Command);
        try
        {
            await process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var spawn = Assert.Single(logger.Infos);
            Assert.Matches(
                @"^\[mxc\] operation=spawn requested=base-container applied=unknown ro=\d+ rw=\d+ denies=\d+$",
                spawn);
        }
        finally
        {
            process.WaitCompletion.SetResult(new() { ExitCode = 23, TimedOut = true });
            await execution;
        }

        Assert.Equal(2, logger.Infos.Count);
        Assert.Equal("[mxc] operation=exit applied=unknown exit=23 timedOut=True", logger.Infos[1]);
        Assert.All(logger.Infos, message =>
        {
            Assert.DoesNotContain(_root, message);
            Assert.DoesNotContain("private", message);
        });
    }

    [Fact]
    public async Task QueuedCommandRechecksRevokedApprovalBeforeNativeSpawn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var allowed = true;
        var calls = 0;
        using var executor = Executor(new(new SettingsData()), _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess());
        });
        var runner = new MxcCommandRunner(executor);
        var first = runner.RunAsync(Command);
        Task<CommandResult>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var command = Command;
            command.RevalidateApproval = _ =>
            {
                var result = Volatile.Read(ref allowed)
                    ? ExecApprovals.ExecApprovalRevalidationResult.Current
                    : ExecApprovals.ExecApprovalRevalidationResult.PolicyDenied("revoked");
                approved.TrySetResult();
                return ValueTask.FromResult(result);
            };
            second = runner.RunAsync(command);
            await approved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref allowed, false);
        }
        finally { release.Set(); }
        Assert.Equal(0, (await first).ExitCode);
        var denied = await second!;
        Assert.Equal(NodeToolErrorCategory.ExecPolicyDenied, denied.ErrorCategory);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecutorShutdownReleasesUncertainPayloadPinsWithoutDeletingUnknownRuntime()
    {
        var executor = Executor(new(new SettingsData()),
            _ => throw new IOException("Native launch outcome is unknown."));
        try
        {
            await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(Noop()));
            await executor.WaitForOwnedOperationsAsync();
            Assert.False(_worker.LastLease!.Disposed);
            var retained = Assert.Single(Directory.GetDirectories(Runs));
            Assert.Throws<IOException>(() => Directory.Move(Runs, Runs + ".moved"));
            executor.Dispose();
            await _worker.LastLease.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Move(Runs, Runs + ".moved");
            Assert.True(Directory.Exists(Path.Combine(Runs + ".moved", Path.GetFileName(retained))));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => executor.RunFileAsync(Noop()));
        }
        finally { executor.Dispose(); }
    }

    [Fact]
    public async Task FailedNativeSpawnDoesNotFabricateAnExitDiagnostic()
    {
        var logger = new CapturingLogger();
        using var executor = Executor(new(new SettingsData()),
            _ => throw new IOException("private launch detail"), logger: logger);

        var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(Noop()));

        Assert.Equal(NodeToolErrorCategory.SandboxFailure, error.Category);
        Assert.StartsWith("[mxc] operation=spawn ", Assert.Single(logger.Infos));
        Assert.DoesNotContain(logger.Infos, message => message.Contains("operation=exit"));
        Assert.DoesNotContain(logger.Warnings, message => message.Contains("private"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScratchSharingViolationWaitsForReleaseOrReportsBoundedCleanupFailure(bool releaseLock)
    {
        if (!OperatingSystem.IsWindows()) return;
        SafeFileHandle? directoryLock = null;
        var logger = new ScratchCleanupLogger(() =>
        {
            if (releaseLock) directoryLock!.Dispose();
        });
        using var executor = Executor(new(new SettingsData()), request =>
        {
            if (directoryLock is not null)
                return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess());
            // Metadata-only handles do not enforce directory deletion sharing.
            directoryLock = OpenDirectory(request.WorkingDirectory!, 0x20, FileShare.ReadWrite,
                IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            Assert.False(directoryLock.IsInvalid, $"Directory lock failed: {Marshal.GetLastWin32Error()}");
            return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess());
        }, logger: logger);
        var execution = executor.RunFileAsync(Noop());
        try
        {
            Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(logger.RetryObserved, "A sharing violation must not abandon scratch immediately.");
            if (releaseLock)
            {
                Assert.Empty(Directory.GetDirectories(Runs));
                Assert.Empty(logger.Warnings);
            }
            else
            {
                Assert.Single(Directory.GetDirectories(Runs));
                Assert.Equal("[mxc] operation=scratch-cleanup-deferred error=IOException", Assert.Single(logger.Warnings));
                directoryLock!.Dispose();
                Assert.True(await executor.RunFileAsync(Noop()));
                Assert.Empty(Directory.GetDirectories(Runs));
            }
        }
        finally
        {
            directoryLock?.Dispose();
            await execution;
        }
    }

    [Theory]
    [InlineData(SystemRunFilesystemScope.SelectedFolders)]
    [InlineData(SystemRunFilesystemScope.UserFilesReadOnly)]
    [InlineData(SystemRunFilesystemScope.UserFilesReadWrite)]
    public async Task CommandsAndBinaryWorkloadsUseSameSavedResourcesWithoutPromotingInfrastructureToData(
        SystemRunFilesystemScope scope)
    {
        var downloads = _context.UserFolders.Single(f => f.Name == "Downloads").Path!;
        var settings = new SettingsData
        {
            SystemRunFilesystemScope = scope, SystemRunAllowOutbound = true,
            SandboxDownloadsAccess = SandboxFolderAccess.ReadWrite, SandboxClipboard = SandboxClipboardMode.Both,
        };
        var requests = new List<ContainerRequest>();
        using var executor = Executor(new(settings), request =>
        {
            requests.Add(request);
            return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess
            {
                StandardOutput = new MemoryStream([0, 255, 128, 13, 10, 0]),
            });
        });
        Assert.Equal(0, (await new MxcCommandRunner(executor).RunAsync(Command)).ExitCode);
        var binary = await executor.RunFileAsync(new FixedFileWorkload<byte[]>((io, stop) =>
        {
            Assert.NotNull(io.Resources);
            Assert.Equal(SandboxFolderAccess.ReadWrite, io.Resources.DataAccess.EffectiveAccess(Path.Combine(downloads, "data")));
            Assert.Null(io.Resources.DataAccess.EffectiveAccess(_worker.Image));
            Assert.Null(io.Resources.DataAccess.EffectiveAccess(Environment.SystemDirectory));
            Assert.Equal(SandboxFolderAccess.NoAccess,
                io.Resources.DataAccess.EffectiveAccess(io.Resources.InfrastructureAccess.ReadwritePaths.Single()));
            Assert.Contains(_worker.Root, io.Resources.InfrastructureAccess.ReadonlyPaths);
            Assert.DoesNotContain(_worker.Root, io.Resources.DataAccess.ReadonlyPaths);
            stop.ThrowIfCancellationRequested();
            io.StandardInput!.Write([0, 255, 13, 10]);
            io.StandardInput.Dispose();
            using var output = new MemoryStream();
            io.StandardOutput!.CopyTo(output);
            return Task.FromResult(output.ToArray());
        }, 10_000));
        Assert.Equal(new byte[] { 0, 255, 128, 13, 10, 0 }, binary);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Filesystem!.ReadonlyPaths.Order(),
            requests[1].Filesystem!.ReadonlyPaths.Where(p => p != _worker.Root).Order());
        Assert.Equal(requests[0].Filesystem!.ReadwritePaths.Where(p => p != requests[0].WorkingDirectory).Order(),
            requests[1].Filesystem!.ReadwritePaths.Where(p => p != requests[1].WorkingDirectory).Order());
        Assert.Equal(requests[0].Filesystem!.DeniedPaths, requests[1].Filesystem!.DeniedPaths);
        Assert.All(requests, r =>
        {
            Assert.Equal(NetworkAction.Allow, r.Network!.Egress!.Default);
            Assert.Equal(ClipboardPolicy.All, r.Ui!.Clipboard);
            Assert.DoesNotContain(Path.GetPathRoot(_root)!, r.Filesystem!.ReadonlyPaths);
        });
        Assert.True(_worker.LastLease!.Disposed);
        Assert.Empty(Directory.GetDirectories(Runs));
    }

    [Fact]
    public async Task FileProtocolPipeWorkersRetainTheImageLeaseUntilTheySettle()
    {
        var pipesRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = Executor(new(new SettingsData()),
            _ => Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess()));
        var operation = executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            io.OwnPipeWorkers(pipeWorker.Task);
            pipesRegistered.SetResult();
            return Task.FromResult(true);
        }, 10_000));
        try
        {
            await pipesRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            Assert.False(_worker.LastLease!.Disposed);
        }
        finally { pipeWorker.SetResult(); }
        Assert.True(await operation);
        Assert.True(_worker.LastLease!.Disposed);
    }

    [Fact]
    public async Task DataAccessPreservesExplicitChildExceptionAndImmutableProtectedDenies()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_root, "selected")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(parent, "allowed")).FullName;
        var policy = new NodeSandboxPolicyStore(new SettingsData
        {
            SandboxCustomFolders =
            [
                new() { Path = parent, Access = SandboxFolderAccess.NoAccess },
                new() { Path = child, Access = SandboxFolderAccess.ReadWrite },
            ],
        });
        using var executor = Executor(policy, _ => Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess()));
        await executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            var data = io.Resources!.DataAccess;
            Assert.Equal(SandboxFolderAccess.NoAccess, data.EffectiveAccess(Path.Combine(parent, "blocked")));
            Assert.Equal(SandboxFolderAccess.ReadWrite, data.EffectiveAccess(Path.Combine(child, "file")));
            Assert.Equal(SandboxFolderAccess.NoAccess, data.EffectiveAccess(Path.Combine(_context.ProtectedPaths[0], "key")));
            return Task.FromResult(true);
        }, 10_000));
        var snapshot = new SettingsData
        {
            SandboxCustomFolders = [new() { Path = _context.ProtectedPaths[0], Access = SandboxFolderAccess.ReadWrite }],
        };
        policy.Persist(snapshot, () => { });
        var denied = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(Noop()));
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, denied.Category);
    }

    [Fact]
    public async Task FixedWorkerBootstrapCannotOverrideProtectedPaths()
    {
        var context = _context with { ProtectedPaths = [.. _context.ProtectedPaths, _worker.Root] };
        using var executor = new NodeSandboxExecutor(new(new SettingsData()), () => SettingsDirectory, () => Supported,
            spawn: _ => throw new Exception("protected worker must not spawn"),
            probe: _ => throw new Exception("protected worker must not be probed"),
            scratchRoot: Runs, contextProvider: () => context, fileWorker: _worker);
        var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(Noop()));
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, error.Category);
        Assert.True(_worker.LastLease!.Disposed);
    }

    [Fact]
    public async Task PersistedChangeDuringFileRequestProbeRejectsLaunchAndNextOperationUsesNewPolicy()
    {
        var policies = new NodeSandboxPolicyStore(new SettingsData());
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseProbe = new ManualResetEventSlim();
        var calls = 0;
        var probes = 0;
        ContainerRequest? launched = null;
        using var executor = Executor(policies, request =>
        {
            calls++;
            launched = request;
            return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess());
        }, probe: _ =>
        {
            if (Interlocked.Increment(ref probes) == 1)
            {
                probed.SetResult();
                Assert.True(releaseProbe.Wait(TimeSpan.FromSeconds(10)));
            }
            return MxcAvailabilityTests.BaseProbe();
        });
        var stale = executor.RunFileAsync(Noop());
        try
        {
            await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            policies.Persist(new() { SystemRunAllowOutbound = true, SandboxClipboard = SandboxClipboardMode.Read }, () => { });
        }
        finally { releaseProbe.Set(); }
        var denied = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => stale);
        Assert.Equal(NodeToolErrorCategory.ExecPolicyDenied, denied.Category);
        Assert.Equal(0, calls);
        await executor.RunFileAsync(Noop());
        Assert.Equal(1, calls);
        Assert.Equal(NetworkAction.Allow, launched!.Network!.Egress!.Default);
        Assert.Equal(ClipboardPolicy.Read, launched.Ui!.Clipboard);
    }

    [Theory]
    [InlineData("unknown", NodeToolErrorCategory.SandboxUnavailable)]
    [InlineData("availability-error", NodeToolErrorCategory.SandboxFailure)]
    [InlineData("probe-error", NodeToolErrorCategory.SandboxFailure)]
    [InlineData("probe-denied", NodeToolErrorCategory.SandboxDenied)]
    [InlineData("spawn-error", NodeToolErrorCategory.SandboxFailure)]
    public async Task ContainmentFailureNeverRetriesFileWorkOnHost(string failure, NodeToolErrorCategory category)
    {
        var hostCalls = 0;
        var nativeCalls = 0;
        using var executor = new NodeSandboxExecutor(new(new SettingsData()), () => SettingsDirectory,
            () => failure switch
            {
                "unknown" => new(false, false, false, ["Unknown platform."]),
                "availability-error" => throw new IOException("probe failed"),
                _ => Supported,
            },
            spawn: _ => { nativeCalls++; throw new IOException("spawn failed"); },
            probe: _ => failure switch
            {
                "probe-error" => throw new IOException("request failed"),
                "probe-denied" => new ProbeOutput
                {
                    Tier = IsolationTier.BaseContainer, NeedsDaclAugmentation = true,
                    Probes = new() { BaseContainerApiPresent = true, BaseContainerSupportsDenyPaths = true, UiCapabilities = new() },
                },
                _ => MxcAvailabilityTests.BaseProbe(),
            }, scratchRoot: Runs, contextProvider: () => _context, fileWorker: _worker)
        {
            StartHost = _ => { hostCalls++; throw new Exception("must not fall back"); },
        };
        var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(Noop()));
        Assert.Equal(category, error.Category);
        Assert.Equal(NodeToolExecutionMode.Sandbox, error.ExecutionMode);
        Assert.Equal(0, hostCalls);
        Assert.Equal(failure == "spawn-error" ? 1 : 0, nativeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyPositiveUnsupportedVerdictStartsFixedHostImageWithoutClaimingSandboxRights(bool failHost)
    {
        var hostCalls = 0;
        var process = new MxcCommandRunnerTests.FakeProcess();
        using var executor = new NodeSandboxExecutor(new(new SettingsData()), () => SettingsDirectory,
            () => new(false, false, false, [], isWindowsUnsupported: true),
            spawn: _ => throw new Exception("no SDK launch"), probe: _ => throw new Exception("no request probe"),
            scratchRoot: Runs, contextProvider: () => throw new Exception("no sandbox projection"), fileWorker: _worker)
        {
            StartHost = info =>
            {
                hostCalls++;
                Assert.Equal(_worker.Image, info.FileName);
                Assert.Equal(new[] { "--stdio" }, info.ArgumentList);
                Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
                Assert.False(info.UseShellExecute);
                if (failHost) throw new IOException("host failure");
                return new ManagedHost(process);
            },
        };
        var work = executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            Assert.Equal(NodeToolExecutionMode.Host, io.ExecutionMode);
            Assert.Null(io.Resources);
            return Task.FromResult(true);
        }, 10_000));
        if (failHost)
        {
            var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => work);
            Assert.Equal(NodeToolErrorCategory.CommandUnavailable, error.Category);
            Assert.Equal(NodeToolExecutionMode.Host, error.ExecutionMode);
        }
        else Assert.True(await work);
        Assert.Equal(1, hostCalls);
        executor.Dispose();
        Assert.True(_worker.LastLease!.Disposed);
    }

    [Fact]
    public async Task LateBinaryLaunchRemainsOwnedAfterCancellationAndNewPolicyCommit()
    {
        var policy = new NodeSandboxPolicyStore(new SettingsData());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<IMxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new MxcCommandRunnerTests.FakeProcess { RootExited = false };
        using var executor = Executor(policy, _ => { entered.SetResult(); return late.Task; });
        using var cancel = new CancellationTokenSource();
        var run = executor.RunFileAsync(new FixedFileWorkload<bool>((_, stop) =>
        {
            stop.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }, 10_000), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = policy.Capture();
        policy.Persist(new() { SandboxClipboard = SandboxClipboardMode.Both }, () => { });
        Assert.True(policy.Capture().Revision > old.Revision);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(_worker.LastLease!.Disposed);
        Assert.Single(Directory.GetDirectories(Runs));
        late.SetResult(process);
        await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _worker.LastLease.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task OwnedWorkJoinIncludesCanceledPreparationBeforeScratchCreation()
    {
        var timeout = TimeSpan.FromMilliseconds(Command.TimeoutMs);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        Directory.CreateDirectory(Runs);
        using var executor = Executor(new(new SettingsData()),
            _ => throw new InvalidOperationException("Canceled preparation must not launch."),
            availability: () =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(timeout), "The preparation gate was not released.");
                return Supported;
            });
        var run = executor.RunFileAsync(Noop(), cancel.Token);
        try
        {
            await entered.Task.WaitAsync(timeout);
            Assert.Empty(Directory.GetDirectories(Runs));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            var joined = executor.WaitForOwnedOperationsAsync();
            Assert.False(joined.IsCompleted, "The owned preparation is still held before scratch creation.");

            using var other = Executor(new(new SettingsData()),
                _ => throw new InvalidOperationException("The other executor has no work."));
            Assert.True(other.WaitForOwnedOperationsAsync().IsCompleted);
            using var waitStop = new CancellationTokenSource();
            waitStop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => executor.WaitForOwnedOperationsAsync(waitStop.Token));
            Assert.False(joined.IsCompleted);
        }
        finally
        {
            cancel.Cancel();
            release.Set();
            await executor.WaitForOwnedOperationsAsync().WaitAsync(timeout);
        }
        Assert.Empty(Directory.GetDirectories(Runs));
    }

    [Fact]
    public async Task CancellationDuringPayloadPreparationReclaimsScratchButNeverSharedCacheStorage()
    {
        var preparing = new PreparingResolver();
        using var executor = new NodeSandboxExecutor(new(new SettingsData()), () => SettingsDirectory, () => Supported,
            spawn: _ => throw new InvalidOperationException("Cancelled preparation must not launch."),
            probe: _ => throw new InvalidOperationException("Cancelled preparation must not probe."),
            scratchRoot: Runs, contextProvider: () => _context, fileWorker: preparing);
        using var stop = new CancellationTokenSource();
        var run = executor.RunFileAsync(Noop(), stop.Token);
        await preparing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(Path.Combine(preparing.Destination!, "partial.dll")));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await preparing.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleanup = Stopwatch.StartNew();
        while (Directory.EnumerateDirectories(Runs).Any() && cleanup.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.Empty(Directory.GetDirectories(Runs));
        Assert.True(File.Exists(Path.Combine(preparing.Destination!, "partial.dll")));
        Assert.Equal("preserve", File.ReadAllText(preparing.Sentinel!));
    }

    [Fact]
    public async Task SharedCapacityIncludesCommandsFinishingCleanupBeforeBinaryAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = Enumerable.Range(0, 8).Select(_ => new MxcCommandRunnerTests.FakeProcess
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        }).ToArray();
        var count = 0;
        using var executor = Executor(new(new SettingsData()), _ =>
        {
            var index = Interlocked.Increment(ref count) - 1;
            if (index == 7) entered.SetResult();
            return Task.FromResult<IMxcProcess>(processes[index]);
        });
        var runner = new MxcCommandRunner(executor);
        var runs = Enumerable.Range(0, 8).Select(_ => runner.RunAsync(Command)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(Noop()));
            Assert.Equal(NodeToolErrorCategory.NodeBusy, error.Category);
            Assert.Null(error.ExecutionMode);
        }
        finally
        {
            foreach (var process in processes) process.WaitCompletion!.TrySetResult(new() { ExitCode = 0 });
            await Task.WhenAll(runs);
        }
    }

    private static FixedFileWorkload<bool> Noop() => new((_, _) => Task.FromResult(true), 10_000);

    [Fact]
    public async Task StreamingTransferOutlivesGeneralCommandDeadlineWithoutChangingUnaryTimeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = Executor(new(new SettingsData { SandboxTimeoutMs = 1000 }),
            _ => Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess()));
        var streaming = executor.RunFileAsync(new FixedFileWorkload<bool>(async (_, stop) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(stop);
            return true;
        }, 10_000, Streaming: true));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var unary = executor.RunFileAsync(new FixedFileWorkload<bool>(async (_, stop) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stop);
                return true;
            }, 10_000));
            var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => unary);
            Assert.Equal(NodeToolErrorCategory.Timeout, error.Category);
            Assert.False(streaming.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True(await streaming);
    }

    [Fact]
    public async Task StreamingDeadlineStillTerminatesWorkerAndSettlesOnlyAfterFinalization()
    {
        var process = new MxcCommandRunnerTests.FakeProcess { RootExited = false };
        var finalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = Executor(new(new SettingsData()),
            _ => Task.FromResult<IMxcProcess>(process));
        var execution = executor.RunFileAsync(new FixedFileWorkload<bool>(async (_, stop) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop);
            return true;
        }, 1000, Streaming: true, ProcessSettled: () =>
        {
            Assert.True(process.Killed);
            Assert.True(process.Finalized);
            finalized.TrySetResult();
        }));
        var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => execution);
        Assert.Equal(NodeToolErrorCategory.Timeout, error.Category);
        await finalized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, FileShare share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    private sealed class ScratchCleanupLogger(Action retryObserved) : IOpenClawLogger
    {
        internal bool RetryObserved { get; private set; }
        internal List<string> Warnings { get; } = [];
        public void Info(string message)
        {
            if (message == "[mxc] operation=scratch-cleanup-retry reason=sharing_violation")
            {
                RetryObserved = true;
                retryObserved();
            }
        }
        public void Debug(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) => Warnings.Add(message);
    }

    private sealed class WorkerResolver : IFixedFileWorkerResolver
    {
        internal string Root { get; }
        internal string Image => Path.Combine(Root, "OpenClaw.FileWorker.exe");
        internal WorkerLease? LastLease { get; private set; }
        internal WorkerResolver(string root)
        {
            Root = Directory.CreateDirectory(root).FullName;
            File.WriteAllBytes(Image, [0]);
        }
        public FixedFileWorkerLease Resolve(string? ownedPayloadDirectory = null, CancellationToken ct = default) =>
            LastLease = new(Root, Image);
    }

    private sealed class PreparingResolver : IFixedFileWorkerResolver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Destination { get; private set; }
        internal string? Sentinel { get; private set; }
        public FixedFileWorkerLease Resolve(string? ownedPayloadDirectory = null, CancellationToken ct = default)
        {
            Destination = Directory.CreateDirectory(ownedPayloadDirectory!).FullName;
            Sentinel = Path.Combine(Path.GetDirectoryName(Destination)!, "unrelated");
            File.WriteAllText(Sentinel, "preserve");
            using var partial = new FileStream(Path.Combine(Destination, "partial.dll"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                partial.Write([1, 2, 3]);
                Entered.SetResult();
                if (!ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Preparation cancellation was not delivered.");
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancelled preparation cannot return a lease.");
            }
            finally { Released.SetResult(); }
        }
    }

    private sealed class WorkerLease(string root, string image) : FixedFileWorkerLease
    {
        public override string ImagePath => image;
        public override string PayloadDirectory => root;
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Dispose() { Disposed = true; Released.TrySetResult(); }
    }

    private sealed class ManagedHost(MxcCommandRunnerTests.FakeProcess process) : MxcProcessLifetime.OwnedProcess
    {
        internal override NodeToolExecutionMode Mode => NodeToolExecutionMode.Host;
        internal override Stream? StandardInput => process.StandardInput;
        internal override Stream? StandardOutput => process.StandardOutput;
        internal override Stream? StandardError => process.StandardError;
        internal override bool TryGetExitCode(out int code) => process.TryGetExitCode(out code);
        internal override Task WaitAsync() => process.WaitAsync();
        internal override void Kill() => process.Kill();
        internal override void CloseOutput() { }
        public override void Dispose() => process.Dispose();
    }
}
