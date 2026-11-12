using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.Shared.Tests.FileTransfer;

public sealed class UploadRetentionCoordinatorTests : IDisposable
{
    private readonly string _private = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".upload-coordinator-test-data", Guid.NewGuid().ToString("N"));
    public UploadRetentionCoordinatorTests() => Directory.CreateDirectory(_private);
    public void Dispose()
    {
        Directory.Delete(_private, true);
        var parent = Path.GetDirectoryName(_private)!;
        if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
    }

    [Fact]
    public async Task ConstructorDoesNotScheduleOrAttachAdmissionUntilExplicitStart()
    {
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, Path.Combine(_private, "roots.json"));
        Assert.Null(ops.UploadAdmission);
        Assert.False(ops.Calls.Reader.TryRead(out _));
        coordinator.Start();
        Assert.NotNull(ops.UploadAdmission);
        Assert.Null(ops.UploadAdmission!(@"C:\uploads"));
        ops.Settled(@"C:\uploads");
        coordinator.Refresh(recoverRoots: true);
        Assert.Equal(@"C:\uploads", (await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Path);
    }

    [Fact]
    public async Task RootReservationSurvivesCrashBeforeUploadCompletionAndLocationSwitch()
    {
        var path = Path.Combine(_private, "roots.json");
        var firstPath = Path.Combine(_private, "first");
        var secondPath = Path.Combine(_private, "second");
        var ops = new Operations();
        await using (var coordinator = new UploadRetentionCoordinator(ops, path))
        {
            coordinator.Start();
            Assert.Null(ops.UploadAdmission!(firstPath));
            Assert.Null(ops.UploadAdmission!(secondPath));
            var reservations = JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!;
            Assert.Equal(new[] { firstPath, secondPath }, reservations.Select(r => r.Path));
            Assert.All(reservations, r => { Assert.Empty(r.Device); Assert.Empty(r.Inode); });
        }
        var recovered = new Operations();
        await using var restart = new UploadRetentionCoordinator(recovered, path);
        restart.Start();
        var recovery = await recovered.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(firstPath, recovery.Path);
        Assert.Null(recovery.Identity);
        Assert.False(Directory.Exists(firstPath));
    }

    [Fact]
    public async Task PersistenceFailurePublishesOutsideLockAndBlocksNewMutationWithoutRetryingCompletedUpload()
    {
        var path = Path.Combine(_private, "roots.json");
        var root = new UploadRootIdentity(Path.Combine(_private, "uploads"), "17", "23");
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        coordinator.Start();
        ops.UploadAdmission!(root.Path);
        using var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var notified = false;
        coordinator.Changed += () =>
        {
            // A second thread must be able to enter the state lock during notification.
            notified = Task.Run(() => coordinator.Refresh(recoverRoots: true)).Wait(TimeSpan.FromSeconds(10));
        };
        ops.Completed(new(root, 1, 3, DateTimeOffset.UtcNow + TimeSpan.FromDays(1)));
        Assert.True(notified);
        Assert.Contains("do not retry completed", coordinator.BlockedReason);
        Assert.Equal("UPLOAD_CLEANUP_BLOCKED", Assert.Throws<FileCommandException>(() => ops.UploadAdmission!(Path.Combine(_private, "other"))).Code);
        Assert.False(ops.Calls.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StartupRecoversOnlyRememberedRootsAndEmptyRootsAreNotReadmittedOnRefresh()
    {
        var old = new UploadRootIdentity(Path.Combine(_private, "old-uploads"), "17", "23");
        var current = new UploadRootIdentity(Path.Combine(_private, "new-uploads"), "17", "29");
        var path = Path.Combine(_private, "upload-roots.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { old }, FileJson.Options));
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        var emptySaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Changed += () => emptySaved.TrySetResult();
        coordinator.Start();
        var first = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(old, first.Identity);
        Assert.Equal(old.Path, first.Path);
        first.Result.SetResult(new(FileJson.Element(new { reaped = true }), new(true),
            new(old, 0, 0, null)));
        await emptySaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetArrayLength());
        coordinator.Refresh(recoverRoots: true);
        // A newly admitted root is the scheduling barrier. An empty old root must
        // not appear in the work queue before this independently reserved location.
        Assert.Null(ops.UploadAdmission!(current.Path));
        ops.Settled(current.Path);
        coordinator.Refresh(recoverRoots: true);
        var second = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(current.Path, second.Path);
        Assert.Null(second.Identity);
        ops.Completed(new(current, 1, 3, DateTimeOffset.UtcNow + TimeSpan.FromDays(1)));
        var inventory = JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!;
        Assert.Equal(current, Assert.Single(inventory));
        Assert.Equal(current, ops.UploadAdmission!(current.Path));
        Assert.False(Directory.Exists(current.Path));
    }

    [Fact]
    public async Task DeniedReapKeepsIdentityInventoryAndPublishesBlockedState()
    {
        var root = new UploadRootIdentity(@"C:\owned-uploads", "17", "23");
        var path = Path.Combine(_private, "upload-roots.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { root }, FileJson.Options));
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Changed += () => changed.TrySetResult();
        coordinator.Start();
        var call = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        call.Result.SetResult(new(FileJson.Element(new { ok = false }), new(false, "PERMISSION_DENIED", "The current data policy denies cleanup.")));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("denies", coordinator.BlockedReason);
        Assert.Equal(root, Assert.Single(JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!));
    }

    [Fact]
    public async Task WorkerCommandFailurePublishesBlockedStateAndAllowsLaterRecovery()
    {
        var root = new UploadRootIdentity(Path.Combine(_private, "uploads"), "17", "23");
        var path = Path.Combine(_private, "roots.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { root }, FileJson.Options));
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        var changes = Channel.CreateUnbounded<bool>();
        coordinator.Changed += () => changes.Writer.TryWrite(true);
        coordinator.Start();
        var failed = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        failed.Result.SetException(new FileCommandException("FILE_WORKER_CACHE_PREPARATION_FAILED", "Synthetic cache access failure."));
        await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("blocked", coordinator.BlockedReason);
        Assert.Equal(root, Assert.Single(JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!));
        coordinator.Refresh(recoverRoots: true);
        var retry = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(root, retry.Identity);
        retry.Result.SetResult(new(FileJson.Element(new { reaped = true }), new(true), new(root, 0, 0, null)));
        await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(coordinator.BlockedReason);
        Assert.Empty(JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!);
    }

    [Fact]
    public async Task ExistingRootAdmissionKeepsReservationWhenOlderCleanupReportsEmpty()
    {
        var root = new UploadRootIdentity(Path.Combine(_private, "uploads"), "17", "23");
        var barrier = new UploadRootIdentity(Path.Combine(_private, "barrier"), "17", "29");
        var path = Path.Combine(_private, "roots.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { root, barrier }, FileJson.Options));
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        coordinator.Start();
        var stale = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(root, stale.Identity);
        Assert.Equal(root, ops.UploadAdmission!(root.Path));
        stale.Result.SetResult(new(FileJson.Element(new { reaped = true }), new(true), new(root, 0, 0, null)));
        var next = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(barrier, next.Identity);
        // Reaching the next root proves the older result was processed, without timing sleeps.
        Assert.Contains(root, JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!);
    }

    [Fact]
    public async Task ConcurrentUploadsKeepReservationUntilEveryWorkerSettlesThenRecoverInterruptedState()
    {
        var root = new UploadRootIdentity(Path.Combine(_private, "uploads"), "17", "23");
        var barrier = Path.Combine(_private, "barrier");
        var path = Path.Combine(_private, "roots.json");
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        var changed = Channel.CreateUnbounded<bool>();
        coordinator.Changed += () => changed.Writer.TryWrite(true);
        coordinator.Start();
        Assert.Null(ops.UploadAdmission!(root.Path));
        Assert.Null(ops.UploadAdmission!(root.Path));
        ops.Completed(new(root, 0, 0, null));
        await changed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(root, Assert.Single(JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!));
        ops.Settled(root.Path);
        coordinator.Refresh(recoverRoots: true);
        Assert.Null(ops.UploadAdmission!(barrier));
        ops.Settled(barrier);
        var next = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(barrier, next.Path); // The first upload settling must not admit root cleanup.
        next.Result.SetResult(new(FileJson.Element(new { }), new(false, "NOT_FOUND", "The reserved directory was never created.")));
        await changed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        ops.Settled(root.Path);
        var recovery = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(root, recovery.Identity);
        recovery.Result.SetResult(new(FileJson.Element(new { reaped = true }), new(true), new(root, 0, 0, null)));
        await changed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(JsonSerializer.Deserialize<UploadRootIdentity[]>(File.ReadAllText(path), FileJson.Options)!);
    }

    [Fact]
    public async Task SuccessfulRootDoesNotHideAnotherRootsBlockedCleanupAndSettingsRefreshRetriesIt()
    {
        var denied = new UploadRootIdentity(Path.Combine(_private, "denied"), "17", "23");
        var allowed = new UploadRootIdentity(Path.Combine(_private, "allowed"), "17", "29");
        var path = Path.Combine(_private, "upload-roots.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { denied, allowed }, FileJson.Options));
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = 0;
        coordinator.Changed += () => { if (Interlocked.Increment(ref changes) == 2) changed.TrySetResult(); };
        coordinator.Start();
        var first = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(denied, first.Identity);
        first.Result.SetResult(new(FileJson.Element(new { ok = false }), new(false, "PERMISSION_DENIED", "Old root remains denied.")));
        var second = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(allowed, second.Identity);
        second.Result.SetResult(new(FileJson.Element(new { reaped = true }), new(true),
            new(allowed, 1, 3, DateTimeOffset.UtcNow + TimeSpan.FromDays(1))));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Old root remains denied.", coordinator.BlockedReason);
        coordinator.Refresh(recoverRoots: true);
        var retry = await ops.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(denied, retry.Identity);
        // Disposal cancels this admitted operation; no target filesystem calls occur in the coordinator.
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[null]")]
    [InlineData("[{\"path\":null,\"device\":\"17\",\"inode\":\"23\"}]")]
    public async Task CorruptInventoryBlocksNewUploadAndNeverScansTargets(string invalid)
    {
        var path = Path.Combine(_private, "upload-roots.json");
        File.WriteAllText(path, invalid);
        var ops = new Operations();
        await using var coordinator = new UploadRetentionCoordinator(ops, path);
        coordinator.Start();
        Assert.Contains("inventory", coordinator.BlockedReason);
        Assert.Equal("UPLOAD_CLEANUP_BLOCKED", Assert.Throws<FileCommandException>(() => ops.UploadAdmission!(@"C:\uploads")).Code);
        Assert.False(ops.Calls.Reader.TryRead(out _));
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    private sealed record Call(string Path, UploadRootIdentity? Identity, TaskCompletionSource<FileOperationResult> Result);
    private sealed class Operations : IUploadRetentionOperations
    {
        public Channel<Call> Calls { get; } = Channel.CreateUnbounded<Call>();
        public event Action<UploadRetentionState>? RetentionChanged;
        public event Action<string>? UploadSettled;
        public Func<string, UploadRootIdentity?>? UploadAdmission { get; set; }
        public void Completed(UploadRetentionState state) => RetentionChanged?.Invoke(state);
        public void Settled(string path) => UploadSettled?.Invoke(path);
        public Task<FileOperationResult> RecoverUploadsAsync(string rootPath, UploadRootIdentity? identity, CancellationToken stop)
        {
            var result = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Writer.TryWrite(new(rootPath, identity, result));
            return result.Task.WaitAsync(stop);
        }
    }
}
