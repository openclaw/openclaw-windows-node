using OpenClaw.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ThinkingLevelClearReconcilerTests
{
    [Fact]
    public async Task PreAckNull_IsProtectedUntilPostAckCorrelatedNull()
    {
        using var reconciler = Create();
        var operation = reconciler.BeginClear("main", "off");

        var preAck = reconciler.ObserveSnapshot("main", null);

        Assert.True(preAck.Accepted);
        Assert.Equal("off", preAck.EffectiveThinkingLevel);
        Assert.True(preAck.ProtectedCanonicalIntent);
        Assert.Null(preAck.RefreshRequest);
        Assert.False(operation.Confirmation.IsCompleted);

        Assert.True(reconciler.TryAcknowledgePatch(operation, out var refresh));
        Assert.Equal(1, refresh.Attempt);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.CommittedAwaitingCanonicalNull,
            operation.State);

        var confirmed = reconciler.ApplyCorrelatedSnapshot(refresh, null);

        Assert.True(confirmed.Accepted);
        Assert.Null(confirmed.EffectiveThinkingLevel);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Confirmed,
            operation.State);
        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.Confirmed,
            await reconciler.WaitForConfirmationAsync(operation));
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Idle,
            reconciler.GetState("main"));
    }

    [Fact]
    public async Task RejectionAfterAcknowledgement_DoesNotReverseCommittedClear()
    {
        using var reconciler = Create();
        var operation = reconciler.BeginClear("main", "off");
        Assert.True(reconciler.TryAcknowledgePatch(operation, out var refresh));

        var rejected = reconciler.RejectPatch(
            operation,
            new InvalidOperationException("late duplicate failure"));

        Assert.False(rejected);
        Assert.False(operation.Confirmation.IsCompleted);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.CommittedAwaitingCanonicalNull,
            operation.State);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.CommittedAwaitingCanonicalNull,
            reconciler.GetState("main"));

        var confirmed = reconciler.ApplyCorrelatedSnapshot(refresh, null);

        Assert.True(confirmed.Accepted);
        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.Confirmed,
            await reconciler.WaitForConfirmationAsync(operation));
    }

    [Fact]
    public void ConcreteValueWithoutReconciliation_LeavesCanonicalStateUntouched()
    {
        using var reconciler = Create();

        var selection = reconciler.BeginConcreteSelection("main", "high", "off");

        Assert.False(selection.TracksReconciliation);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Idle,
            reconciler.GetState("main"));
        Assert.Equal("off", reconciler.ObserveSnapshot("main", "off").EffectiveThinkingLevel);
    }

    [Fact]
    public async Task ConcreteValue_SupersedesPendingClearAndRejectsLateNull()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "high");

        var selection = reconciler.BeginConcreteSelection("main", "low", "high");

        Assert.True(selection.TracksReconciliation);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Superseded,
            clear.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reconciler.WaitForConfirmationAsync(clear));
        Assert.False(reconciler.TryAcknowledgePatch(clear, out _));

        var staleNull = reconciler.ObserveSnapshot("main", null);
        var refresh = AssertRefresh(staleNull);
        Assert.Equal("low", staleNull.EffectiveThinkingLevel);

        var current = reconciler.ApplyCorrelatedSnapshot(refresh, "low");

        Assert.True(current.Accepted);
        Assert.Equal("low", current.EffectiveThinkingLevel);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Idle,
            reconciler.GetState("main"));
    }

    [Fact]
    public async Task FailedPatch_RetainsSelectionUntilCorrelatedExternalConvergence()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "high");
        var failure = new InvalidOperationException("unknown method: sessions.patch");

        Assert.True(reconciler.RejectPatch(clear, failure));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconciler.WaitForConfirmationAsync(clear));
        Assert.Same(failure, thrown);

        var externalNull = reconciler.ObserveSnapshot("main", null);
        var refresh = AssertRefresh(externalNull);
        Assert.Equal("high", externalNull.EffectiveThinkingLevel);

        var converged = reconciler.ApplyCorrelatedSnapshot(refresh, null);

        Assert.True(converged.Accepted);
        Assert.Null(converged.EffectiveThinkingLevel);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Idle,
            reconciler.GetState("main"));
    }

    [Fact]
    public async Task CancellationBeforeAck_IsInterruptedAndKeepsProtectedIntent()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "minimal");
        var canceled = new OperationCanceledException();

        Assert.True(reconciler.RejectPatch(clear, canceled));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reconciler.WaitForConfirmationAsync(clear));
        var delayedNull = reconciler.ObserveSnapshot("main", null);
        Assert.Equal("minimal", delayedNull.EffectiveThinkingLevel);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.RetryingRefresh,
            reconciler.GetState("main"));
    }

    [Fact]
    public async Task DisconnectBeforeAck_RejectsLateAckAndConvergesOnReconnect()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "off");

        Assert.Empty(reconciler.OnConnectionChanged(connected: false));
        await Assert.ThrowsAsync<ThinkingLevelClearInterruptedException>(
            () => reconciler.WaitForConfirmationAsync(clear));
        Assert.False(reconciler.TryAcknowledgePatch(clear, out _));
        Assert.Equal("off", reconciler.ObserveSnapshot("main", null).EffectiveThinkingLevel);

        var reconnectRefresh = Assert.Single(
            reconciler.OnConnectionChanged(connected: true));
        var converged = reconciler.ApplyCorrelatedSnapshot(reconnectRefresh, "off");

        Assert.True(converged.Accepted);
        Assert.Equal("off", converged.EffectiveThinkingLevel);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Idle,
            reconciler.GetState("main"));
    }

    [Fact]
    public async Task CancellationAfterAck_ReturnsCommittedWhileLateSnapshotConfirms()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "off");
        Assert.True(reconciler.TryAcknowledgePatch(clear, out var refresh));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await reconciler.WaitForConfirmationAsync(clear, cancellation.Token);

        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.CommittedAwaitingCanonicalNull,
            outcome);
        Assert.Equal("off", reconciler.ObserveSnapshot("main", null).EffectiveThinkingLevel);

        var confirmed = reconciler.ApplyCorrelatedSnapshot(refresh, null);
        Assert.True(confirmed.Accepted);
        Assert.Null(confirmed.EffectiveThinkingLevel);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Confirmed,
            clear.State);
    }

    [Fact]
    public async Task PostAckTimeout_ReturnsCommittedWhileLateSnapshotConfirms()
    {
        using var reconciler = Create(
            confirmationTimeout: TimeSpan.FromMilliseconds(10));
        var clear = reconciler.BeginClear("main", "high");
        Assert.True(reconciler.TryAcknowledgePatch(clear, out var refresh));

        var outcome = await reconciler.WaitForConfirmationAsync(clear);

        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.CommittedAwaitingCanonicalNull,
            outcome);
        Assert.Equal("high", reconciler.ObserveSnapshot("main", null).EffectiveThinkingLevel);
        Assert.True(reconciler.ApplyCorrelatedSnapshot(refresh, null).Accepted);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Confirmed,
            clear.State);
    }

    [Fact]
    public async Task PostAckTimeoutThenConcreteSelection_RejectsStaleClearSnapshot()
    {
        using var reconciler = Create(
            confirmationTimeout: TimeSpan.FromMilliseconds(10));
        var clear = reconciler.BeginClear("main", "off");
        Assert.True(reconciler.TryAcknowledgePatch(clear, out var staleRefresh));
        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.CommittedAwaitingCanonicalNull,
            await reconciler.WaitForConfirmationAsync(clear));

        reconciler.BeginConcreteSelection("main", "low", "off");

        Assert.False(reconciler.ApplyCorrelatedSnapshot(staleRefresh, null).Accepted);
        var delayedNull = reconciler.ObserveSnapshot("main", null);
        var currentRefresh = AssertRefresh(delayedNull);
        Assert.Equal("low", delayedNull.EffectiveThinkingLevel);
        Assert.True(reconciler.ApplyCorrelatedSnapshot(currentRefresh, "low").Accepted);
    }

    [Fact]
    public void CommittedReconciliation_RetriesBoundedlyAndRestartsOnExternalChange()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "minimal");
        Assert.True(reconciler.TryAcknowledgePatch(clear, out var first));

        var firstRetry = AssertRefresh(
            reconciler.ApplyCorrelatedSnapshot(first, "minimal"));
        var secondRetry = AssertRefresh(
            reconciler.ApplyCorrelatedSnapshot(firstRetry, "minimal"));
        var exhausted = reconciler.ApplyCorrelatedSnapshot(secondRetry, "minimal");

        Assert.Null(exhausted.RefreshRequest);
        Assert.Equal(3, secondRetry.Attempt);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.CommittedAwaitingCanonicalNull,
            reconciler.GetState("main"));

        var externalNull = reconciler.ObserveSnapshot("main", null);
        var restarted = AssertRefresh(externalNull);
        Assert.Equal(1, restarted.Attempt);
        Assert.Equal("minimal", externalNull.EffectiveThinkingLevel);

        Assert.True(reconciler.ApplyCorrelatedSnapshot(restarted, null).Accepted);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Confirmed,
            clear.State);
    }

    [Fact]
    public async Task RefreshFailure_UsesOwnedRetryTimerAndStopsAtBound()
    {
        var delays = 0;
        using var reconciler = Create(
            delay: (_, _) =>
            {
                Interlocked.Increment(ref delays);
                return Task.CompletedTask;
            });
        var clear = reconciler.BeginClear("main", "off");
        Assert.True(reconciler.TryAcknowledgePatch(clear, out var first));

        var second = Assert.NotNull(await reconciler.RetryAfterFailureAsync(first));
        var third = Assert.NotNull(await reconciler.RetryAfterFailureAsync(second));
        var exhausted = await reconciler.RetryAfterFailureAsync(third);

        Assert.Null(exhausted);
        Assert.Equal(2, delays);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.CommittedAwaitingCanonicalNull,
            reconciler.GetState("main"));
    }

    [Fact]
    public async Task TwoRapidClears_OnlyNewestOperationCanConfirm()
    {
        using var reconciler = Create();
        var first = reconciler.BeginClear("main", "off");
        var second = reconciler.BeginClear("main", "off");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reconciler.WaitForConfirmationAsync(first));
        Assert.False(reconciler.TryAcknowledgePatch(first, out _));
        Assert.True(reconciler.TryAcknowledgePatch(second, out var refresh));

        Assert.True(reconciler.ApplyCorrelatedSnapshot(refresh, null).Accepted);
        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.Confirmed,
            await reconciler.WaitForConfirmationAsync(second));
    }

    [Fact]
    public async Task OldConnectionRefresh_CannotOverwriteCurrentGeneration()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "off");
        Assert.True(reconciler.TryAcknowledgePatch(clear, out var oldRefresh));

        reconciler.OnConnectionChanged(connected: false);
        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.CommittedAwaitingCanonicalNull,
            await reconciler.WaitForConfirmationAsync(clear));
        var currentRefresh = Assert.Single(
            reconciler.OnConnectionChanged(connected: true));

        var current = reconciler.ApplyCorrelatedSnapshot(currentRefresh, "off");
        var stale = reconciler.ApplyCorrelatedSnapshot(oldRefresh, null);

        Assert.True(current.Accepted);
        Assert.Equal("off", current.EffectiveThinkingLevel);
        Assert.False(stale.Accepted);
    }

    [Fact]
    public async Task ClientSwap_InterruptsOldAuthorityAndRequestsCurrentGenerationSnapshot()
    {
        using var reconciler = Create();
        var clear = reconciler.BeginClear("main", "high");
        Assert.True(reconciler.TryAcknowledgePatch(clear, out var oldRefresh));
        var oldGeneration = reconciler.ConnectionGeneration;

        var currentRefresh = Assert.Single(
            reconciler.OnConnectionChanged(connected: true, clientChanged: true));

        Assert.Equal(
            ThinkingLevelClearReconciler.ClearOutcome.CommittedAwaitingCanonicalNull,
            await reconciler.WaitForConfirmationAsync(clear));
        Assert.True(currentRefresh.ConnectionGeneration > oldGeneration);
        Assert.False(reconciler.ApplyCorrelatedSnapshot(oldRefresh, null).Accepted);
        Assert.True(reconciler.ApplyCorrelatedSnapshot(currentRefresh, "high").Accepted);
    }

    [Fact]
    public async Task Dispose_CancelsOperationsAndRejectsLaterSnapshots()
    {
        var reconciler = Create();
        var clear = reconciler.BeginClear("main", "off");

        reconciler.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reconciler.WaitForConfirmationAsync(clear));
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Disposed,
            clear.State);
        Assert.False(reconciler.ObserveSnapshot("main", null).Accepted);
        Assert.Equal(
            ThinkingLevelClearReconciler.ReconciliationState.Disposed,
            reconciler.GetState("main"));
    }

    [Fact]
    public async Task ConcurrentSupersession_LeavesOneBoundedCurrentOperation()
    {
        using var reconciler = Create();
        var operations = new System.Collections.Concurrent.ConcurrentBag<
            ThinkingLevelClearReconciler.ClearOperation>();

        await Task.WhenAll(Enumerable.Range(0, 256).Select(index => Task.Run(() =>
        {
            var operation = reconciler.BeginClear("main", index % 2 == 0 ? "off" : "high");
            operations.Add(operation);
            reconciler.ObserveSnapshot("main", null);
        })));

        var current = Assert.Single(
            operations,
            operation => operation.State ==
                ThinkingLevelClearReconciler.ReconciliationState.AwaitingPatchAck);
        Assert.True(reconciler.TryAcknowledgePatch(current, out var refresh));
        Assert.Equal(1, refresh.Attempt);
        Assert.All(
            operations.Where(operation => !ReferenceEquals(operation, current)),
            operation => Assert.Equal(
                ThinkingLevelClearReconciler.ReconciliationState.Superseded,
                operation.State));
    }

    private static ThinkingLevelClearReconciler Create(
        TimeSpan? confirmationTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            connected: true,
            confirmationTimeout,
            retryDelay: TimeSpan.Zero,
            delay: delay);

    private static ThinkingLevelClearReconciler.RefreshRequest AssertRefresh(
        ThinkingLevelClearReconciler.SnapshotResolution resolution)
    {
        Assert.True(resolution.Accepted);
        return Assert.IsType<ThinkingLevelClearReconciler.RefreshRequest>(
            resolution.RefreshRequest);
    }
}
