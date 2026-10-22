using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class NativeRestartAdmissionTests
{
    [Theory]
    [InlineData("\"ai-v3:truncated")]
    [InlineData("\"not-a-handle\"")]
    [InlineData("oversized")]
    public void InvalidRestartIsReportedOnceAndOnlyItsRegularFileIsRemoved(string contents)
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("setup-dashboard-handoff");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "restart.json");
        File.WriteAllText(file, contents == "oversized" ? new string('x', 2048) : contents);
        var sentinel = Path.Combine(directory, "pending.json");
        File.WriteAllText(sentinel, "keep");
        var store = new NativeRestartRecoveryStore(temp.Path);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Null(store.Read());
        Assert.False(File.Exists(file));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task InvalidRestartCleanupCannotDeleteNewerCooperatingHandle()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("setup-dashboard-handoff");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "restart.json");
        File.WriteAllText(file, "{");
        var handle = "ai-v3:" + new string('d', 64);
        var store = new NativeRestartRecoveryStore(temp.Path);
        using var started = new ManualResetEventSlim();
        var lease = OpenClaw.Shared.PersistenceFileLease.Acquire(file);
        var writer = Task.Run(() => { started.Set(); store.Save(handle); });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidDataException>(() => store.Read());
        }
        finally { lease.Dispose(); }
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(handle, store.Read());
        store.Clear("ai-v3:" + new string('e', 64));
        Assert.Equal(handle, store.Read());
    }

    [Fact]
    public void ReparseRestartIsRejectedWithoutDeletingLinkOrTarget()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("setup-dashboard-handoff");
        Directory.CreateDirectory(directory);
        var target = temp.Combine("outside.json");
        File.WriteAllText(target, "{");
        var file = Path.Combine(directory, "restart.json");
        try
        {
            File.CreateSymbolicLink(file, target);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() &&
            (uint)ex.HResult == 0x80070522)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Creating a symlink requires Developer Mode or SeCreateSymbolicLinkPrivilege.");
            return;
        }
        try
        {
            var store = new NativeRestartRecoveryStore(temp.Path);
            Assert.Throws<IOException>(() => store.Read());
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("{", File.ReadAllText(target));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task DelayedOldOwnerIsNeverForwardedToAndMutexAcquisitionStaysOnOneThread()
    {
        using var temp = new TempDirectory();
        var store = new NativeRestartRecoveryStore(temp.Path);
        var handle = "ai-v3:" + new string('a', 64);
        var ownerThread = Environment.CurrentManagedThreadId;
        var waits = 0;
        var prompts = 0;
        var admitted = NativeRestartAdmission.Acquire(handle, store.Save, budget =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.Equal(TimeSpan.FromSeconds(60), budget);
            Assert.Equal(handle, store.Read());
            return ++waits == 2;
        }, reason =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.Equal(NativeRestartWaitFailure.PreviousInstance, reason);
            prompts++;
            return true;
        });
        Assert.True(admitted);
        Assert.Equal(1, prompts);
        var router = new ActivationRouter("openclaw", "unused-restart-test");
        var sink = new Sink();
        var input = new LaunchActivationInput(null, [], handle, false);
        await router.DispatchPlanAsync(router.PlanLaunch(input), sink, CancellationToken.None);
        Assert.Equal(handle, Assert.IsType<ActivationRoute.CompleteAiSetup>(Assert.Single(sink.Routes)).Handle);
        store.Clear(handle);
        Assert.Null(store.Read());
    }

    [Fact]
    public void RealMutexIsReleasedByTheSameThreadThatAdmitsTheRestart()
    {
        var name = "OpenClaw.TestRestart." + Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var oldOwner = new Thread(() =>
        {
            using var oldMutex = new Mutex(true, name);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            oldMutex.ReleaseMutex();
        });
        oldOwner.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        using var mutex = new Mutex(false, name);
        try
        {
            Assert.True(NativeRestartAdmission.Acquire("ai-v3:" + new string('c', 64),
                _ => release.Set(), timeout => mutex.WaitOne(timeout), _ => false));
            mutex.ReleaseMutex();
        }
        finally { release.Set(); oldOwner.Join(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void BoundedFailureRetainsProtectedHandleForExplicitReopen()
    {
        using var temp = new TempDirectory();
        var store = new NativeRestartRecoveryStore(temp.Path);
        var handle = "ai-v3:" + new string('b', 64);
        Assert.False(NativeRestartAdmission.Acquire(handle, store.Save, _ => false, _ => false));
        Assert.Equal(handle, new NativeRestartRecoveryStore(temp.Path).Read());
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(Path.Combine(temp.Path, "setup-dashboard-handoff", "restart.json"));
            Assert.True(System.IO.FileSystemAclExtensions.GetAccessControl(info).AreAccessRulesProtected);
        }
    }

    [Fact]
    public void RecoveryStorageFailureIsVisibleAndCannotBeTreatedAsOwnership()
    {
        var prompts = new List<NativeRestartWaitFailure>();
        Assert.False(NativeRestartAdmission.Acquire("ai-v3:" + new string('a', 64),
            _ => throw new IOException(), _ => throw new Exception("Must not wait before preserving"),
            reason => { prompts.Add(reason); return false; }));
        Assert.Equal([NativeRestartWaitFailure.RecoveryStorage], prompts);
    }

    private sealed class Sink : IActivationPlanSink
    {
        public List<ActivationRoute> Routes { get; } = [];
        public Task DispatchAsync(ActivationRoute route, CancellationToken ct)
        {
            Routes.Add(route);
            return Task.CompletedTask;
        }
        public Task<bool> ConfirmAsync(ActivationConfirmation confirmation, CancellationToken ct) =>
            throw new InvalidOperationException("Native setup must use its existing receipt validation route.");
    }
}
