using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class NodeSandboxPolicyTests
{
    [Fact]
    public void SnapshotIsDetachedAndUnrelatedSavesDoNotInvalidatePolicy()
    {
        var settings = new SettingsData
        {
            SandboxCustomFolders = [new() { Path = @"C:\data", Access = SandboxFolderAccess.ReadOnly }],
        };
        var store = new NodeSandboxPolicyStore(settings);
        var original = store.Capture();
        settings.SandboxCustomFolders[0].Access = SandboxFolderAccess.ReadWrite;
        Assert.Equal(SandboxFolderAccess.ReadOnly, original.Settings.SandboxCustomFolders![0].Access);
        var unrelated = original.Settings with { ShowNotifications = !original.Settings.ShowNotifications };
        store.Persist(unrelated, () => { });
        Assert.Same(original, store.Capture());
        store.Persist(settings, () => { });
        Assert.Equal(original.Revision + 1, store.Capture().Revision);
        store.Persist(original.Settings, () => { });
        Assert.Equal(original.Revision + 2, store.Capture().Revision);
        Assert.Equal(original.Fingerprint, store.Capture().Fingerprint);
    }

    [Fact]
    public async Task FailedPersistencePublishesNothingAndPreparedWorkCanStillStart()
    {
        var store = new NodeSandboxPolicyStore(new SettingsData());
        var prepared = store.Capture();
        Assert.Throws<IOException>(() => store.Persist(new() { SystemRunAllowOutbound = true },
            () => throw new IOException("disk write failed")));
        Assert.Same(prepared, store.Capture());
        Assert.Equal(42, await store.Submit(prepared, () => Task.FromResult(42), default));
    }

    [Fact]
    public async Task SaveWinningBeforeQueuedAdmissionRejectsStaleRevisionWithoutInvokingLaunch()
    {
        var store = new NodeSandboxPolicyStore(new SettingsData());
        var prepared = store.Capture();
        using var releaseSave = new ManualResetEventSlim();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = Task.Run(() => store.Persist(new() { SystemRunAllowOutbound = true }, () =>
        {
            writing.SetResult();
            Assert.True(releaseSave.Wait(TimeSpan.FromSeconds(10)));
        }));
        Task<int>? run = null;
        var called = false;
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run = store.Submit(prepared, () => { called = true; return Task.FromResult(42); }, default);
            Assert.False(run.IsCompleted);
            Assert.Same(prepared, store.Capture());
        }
        finally { releaseSave.Set(); await save; }
        await Assert.ThrowsAsync<StaleNodeSandboxPolicyException>(() => run!);
        Assert.False(called);
        Assert.True(store.Capture().Revision > prepared.Revision);
    }

    [Fact]
    public async Task SynchronousLaunchSubmissionPrecedesCommitButLateCompletionDoesNotBlockIt()
    {
        var store = new NodeSandboxPolicyStore(new SettingsData());
        var prepared = store.Capture();
        using var releaseSubmission = new ManualResetEventSlim();
        var submitting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateProcess = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = store.Submit(prepared, () =>
        {
            submitting.SetResult();
            Assert.True(releaseSubmission.Wait(TimeSpan.FromSeconds(10)));
            return lateProcess.Task;
        }, default);
        await submitting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = Task.Run(() => store.Persist(new() { SystemRunAllowOutbound = true },
            () => saved.SetResult()));
        try
        {
            Assert.Same(prepared, store.Capture());
            Assert.False(saved.Task.IsCompleted);
        }
        finally { releaseSubmission.Set(); }
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        Assert.True(store.Capture().Revision > prepared.Revision);
        lateProcess.SetResult(42);
        Assert.Equal(42, await run);
    }

    [Fact]
    public async Task CancelledQueuedLaunchDoesNotStartAfterCommitDispatcherUnblocks()
    {
        var store = new NodeSandboxPolicyStore(new SettingsData());
        var prepared = store.Capture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = store.Submit(prepared, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return Task.FromResult(0);
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var called = false;
        var queued = store.Submit(prepared, () => { called = true; return Task.FromResult(1); }, cancellation.Token);
        cancellation.Cancel();
        release.Set();
        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(called);
    }
}
