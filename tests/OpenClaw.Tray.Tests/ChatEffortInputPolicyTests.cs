using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatEffortInputPolicyTests
{
    [Fact]
    public void NativePreviewOwnershipLastsOnlyForTheActiveGesture()
    {
        var policy = new ChatEffortInputPolicy();
        Assert.False(policy.IsPointerActive);
        policy.BeginPointer(7);
        Assert.True(policy.IsPointerActive);
        policy.QueueValueChange();
        Assert.True(policy.IsPointerActive);
        Assert.False(policy.EndPointer(8));
        Assert.True(policy.IsPointerActive);
        Assert.True(policy.EndPointer(7));
        Assert.False(policy.IsPointerActive);
        policy.BeginPointer(7);
        policy.CancelPending();
        Assert.False(policy.IsPointerActive);
    }

    [Fact]
    public void OnlyLatestQueuedValueCanCommit()
    {
        var policy = new ChatEffortInputPolicy();
        var old = policy.QueueValueChange();
        var current = policy.QueueValueChange();
        Assert.False(policy.CanCommit(old));
        Assert.True(policy.CanCommit(current));
    }

    [Fact]
    public void BubblingPointerPressInvalidatesTheEarlierNativeValueCallback()
    {
        var policy = new ChatEffortInputPolicy();
        var beforeBubble = policy.QueueValueChange();
        policy.BeginPointer(7);
        Assert.False(policy.CanCommit(beforeBubble));
        Assert.False(policy.CanCommit(policy.QueueValueChange()));
    }

    [Fact]
    public void PointerReleaseCommitsOnceAndInvalidatesIntermediateCallbacks()
    {
        var policy = new ChatEffortInputPolicy();
        policy.BeginPointer(7);
        var intermediate = policy.QueueValueChange();
        Assert.False(policy.EndPointer(8));
        Assert.True(policy.EndPointer(7));
        Assert.False(policy.EndPointer(7));
        Assert.False(policy.CanCommit(intermediate));
    }

    [Fact]
    public void ReleaseAtUnchangedMinimumStillCommits()
    {
        var policy = new ChatEffortInputPolicy();
        policy.BeginPointer(1);
        Assert.True(policy.EndPointer(1));
    }

    [Fact]
    public void CaptureLossDuringContactCancelsTheGestureAndQueuedPreview()
    {
        var policy = new ChatEffortInputPolicy();
        policy.BeginPointer(7);
        var pointerRevision = Assert.IsType<long>(policy.GetPointerRevision(7));
        var preview = policy.QueueValueChange();

        Assert.True(policy.CancelPointer(7, pointerRevision));
        Assert.False(policy.EndPointer(7));
        Assert.False(policy.CanCommit(preview));
        Assert.Null(policy.GetPointerRevision(7));
        Assert.True(policy.CanCommit(policy.QueueValueChange()));
    }

    [Fact]
    public void NormalReleaseBeforeDeferredCaptureCleanupCommitsExactlyOnce()
    {
        var policy = new ChatEffortInputPolicy();
        policy.BeginPointer(7);
        var pointerRevision = Assert.IsType<long>(policy.GetPointerRevision(7));

        Assert.True(policy.EndPointer(7));
        Assert.False(policy.CancelPointer(7, pointerRevision));
        Assert.False(policy.EndPointer(7));
    }

    [Fact]
    public void DeferredCaptureCleanupCannotCancelANewerGestureUsingTheSamePointer()
    {
        var policy = new ChatEffortInputPolicy();
        policy.BeginPointer(7);
        var oldRevision = Assert.IsType<long>(policy.GetPointerRevision(7));
        policy.BeginPointer(7);

        Assert.False(policy.CancelPointer(7, oldRevision));
        Assert.True(policy.EndPointer(7));
    }

    [Fact]
    public void CancellationRemainsValidAcrossQueuedValuesButNotOtherPointers()
    {
        var policy = new ChatEffortInputPolicy();
        policy.BeginPointer(7);
        var pointerRevision = Assert.IsType<long>(policy.GetPointerRevision(7));
        policy.QueueValueChange();

        Assert.Null(policy.GetPointerRevision(8));
        Assert.False(policy.CancelPointer(8, pointerRevision));
        Assert.True(policy.CancelPointer(7, pointerRevision));
        Assert.False(policy.EndPointer(7));
    }

    [Fact]
    public void CleanupInvalidatesQueuedValuesAndPointerCompletion()
    {
        var policy = new ChatEffortInputPolicy();
        var queued = policy.QueueValueChange();
        policy.BeginPointer(1);
        policy.CancelPending();
        Assert.False(policy.CanCommit(queued));
        Assert.False(policy.EndPointer(1));
        Assert.True(policy.CanCommit(policy.QueueValueChange()));
    }
}
