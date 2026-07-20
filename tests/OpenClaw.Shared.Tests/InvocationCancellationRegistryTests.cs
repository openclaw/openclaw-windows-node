using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class InvocationCancellationRegistryTests
{
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    [Fact]
    public void TryCancel_CancelsOnlyMatchingInvocation()
    {
        var registry = new InvocationCancellationRegistry();
        Assert.True(registry.TryRegister("first", CancellationToken.None, out var firstCandidate));
        Assert.True(registry.TryRegister("second", CancellationToken.None, out var secondCandidate));
        var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        var second = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(secondCandidate);
        using (first)
        using (second)
        {
            Assert.True(registry.TryCancel("first"));

            Assert.True(first.Token.IsCancellationRequested);
            Assert.True(first.CancelledByCaller);
            Assert.False(second.Token.IsCancellationRequested);
            Assert.False(second.CancelledByCaller);
        }
    }

    [Fact]
    public void TryRegister_RejectsDuplicateActiveId_AndAllowsReuseAfterCompletion()
    {
        var registry = new InvocationCancellationRegistry();
        Assert.True(registry.TryRegister("same", CancellationToken.None, out var firstCandidate));
        var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        using (first)
        {
            Assert.False(registry.TryRegister("same", CancellationToken.None, out var duplicate));
            Assert.Null(duplicate);
        }

        Assert.True(registry.TryRegister("same", CancellationToken.None, out var replacementCandidate));
        var replacement = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(replacementCandidate);
        replacement.Dispose();
    }

    [Fact]
    public void TransportCancellation_IsLinkedButNotMarkedAsCallerCancellation()
    {
        using var transportCts = new CancellationTokenSource();
        var registry = new InvocationCancellationRegistry();
        Assert.True(registry.TryRegister("request", transportCts.Token, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(invocationCandidate);
        using (invocation)
        {
            transportCts.Cancel();

            Assert.True(invocation.Token.IsCancellationRequested);
            Assert.False(invocation.CancelledByCaller);
        }
    }

    [Fact]
    public void TryCancel_AfterTransportCancellation_DoesNotChangeCancellationReason()
    {
        using var transportCts = new CancellationTokenSource();
        var registry = new InvocationCancellationRegistry();
        Assert.True(registry.TryRegister("request", transportCts.Token, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(invocationCandidate);
        using (invocation)
        {
            transportCts.Cancel();

            Assert.False(registry.TryCancel("request"));
            Assert.False(invocation.CancelledByCaller);
        }
    }

    [Fact]
    public void CancelAll_CancelsEveryActiveInvocation()
    {
        var registry = new InvocationCancellationRegistry();
        Assert.True(registry.TryRegister("first", CancellationToken.None, out var firstCandidate));
        Assert.True(registry.TryRegister("second", CancellationToken.None, out var secondCandidate));
        var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        var second = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(secondCandidate);
        using (first)
        using (second)
        {
            registry.CancelAll();

            Assert.True(first.Token.IsCancellationRequested);
            Assert.True(second.Token.IsCancellationRequested);
            Assert.False(first.CancelledByCaller);
            Assert.False(second.CancelledByCaller);
        }
    }

    [Fact]
    public void TryCancel_UnknownOrCompletedId_IsHarmless()
    {
        var registry = new InvocationCancellationRegistry();
        Assert.False(registry.TryCancel("missing"));

        Assert.True(registry.TryRegister("done", CancellationToken.None, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(invocationCandidate);
        invocation.Dispose();

        Assert.False(registry.TryCancel("done"));
    }

    [Fact]
    public void TryComplete_WinsAgainstLateCancellation()
    {
        var registry = new InvocationCancellationRegistry();
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(invocationCandidate);
        using (invocation)
        {
            Assert.True(invocation.TryComplete());
            Assert.False(registry.TryCancel("request"));
            Assert.False(invocation.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public void CallerCancellation_WinsAgainstLateCompletion()
    {
        var registry = new InvocationCancellationRegistry();
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(invocationCandidate);
        using (invocation)
        {
            Assert.True(registry.TryCancel("request"));
            Assert.False(invocation.TryComplete());
            Assert.True(invocation.CancelledByCaller);
        }
    }

    [Fact]
    public void DuplicateIds_CanCoexistWhenEnabled_AndCancellationIsAmbiguous()
    {
        var registry = new InvocationCancellationRegistry(allowDuplicateIds: true);
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var firstCandidate));
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var secondCandidate));
        var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        var second = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(secondCandidate);
        using (first)
        using (second)
        {
            Assert.False(registry.TryCancel("request", out var ambiguous));
            Assert.True(ambiguous);
            Assert.False(first.Token.IsCancellationRequested);
            Assert.False(second.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public void DuplicateIds_CanBeCancelledAfterOneCompletes()
    {
        var registry = new InvocationCancellationRegistry(allowDuplicateIds: true);
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var firstCandidate));
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var secondCandidate));
        var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        var second = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(secondCandidate);
        using (first)
        using (second)
        {
            Assert.True(first.TryComplete());
            Assert.True(registry.TryCancel("request", out var ambiguous));
            Assert.False(ambiguous);
            Assert.True(second.CancelledByCaller);
        }
    }

    [Fact]
    public void PendingCancellation_IsConsumedBeforeRegistrationReturns()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new InvocationCancellationRegistry(
            allowDuplicateIds: true,
            pendingCancellationTtl: TimeSpan.FromSeconds(5),
            timeProvider: timeProvider);

        Assert.Equal(
            InvocationCancellationResult.Pending,
            registry.TryCancelOrRemember("request"));
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.True(registry.TryRegister("request", CancellationToken.None, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(invocationCandidate);
        using (invocation)
        {
            Assert.True(invocation.CancelledByCaller);
            Assert.True(invocation.Token.IsCancellationRequested);
            Assert.False(invocation.TryComplete());
        }
    }

    [Fact]
    public void PendingCancellation_TakesPrecedenceOverAlreadyCancelledTransport()
    {
        using var transportCts = new CancellationTokenSource();
        transportCts.Cancel();
        var registry = new InvocationCancellationRegistry(
            pendingCancellationTtl: TimeSpan.FromSeconds(5));

        Assert.Equal(
            InvocationCancellationResult.Pending,
            registry.TryCancelOrRemember("request"));

        Assert.True(registry.TryRegister("request", transportCts.Token, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(
            invocationCandidate);
        using (invocation)
        {
            Assert.True(invocation.CancelledByCaller);
            Assert.True(invocation.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public void PendingCancellation_WithDuplicateIdsCancelsFirstRegistrationOnly()
    {
        var registry = new InvocationCancellationRegistry(
            allowDuplicateIds: true,
            pendingCancellationTtl: TimeSpan.FromSeconds(5));

        Assert.Equal(
            InvocationCancellationResult.Pending,
            registry.TryCancelOrRemember("request"));

        Assert.True(registry.TryRegister("request", CancellationToken.None, out var firstCandidate));
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var secondCandidate));
        using var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(
            firstCandidate);
        using var second = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(
            secondCandidate);

        Assert.True(first.CancelledByCaller);
        Assert.False(second.CancelledByCaller);
    }

    [Fact]
    public void PendingCancellation_ExpiresAfterTtl()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new InvocationCancellationRegistry(
            pendingCancellationTtl: TimeSpan.FromSeconds(5),
            timeProvider: timeProvider);

        Assert.Equal(
            InvocationCancellationResult.Pending,
            registry.TryCancelOrRemember("request"));
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        Assert.True(registry.TryRegister("request", CancellationToken.None, out var invocationCandidate));
        var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(invocationCandidate);
        using (invocation)
        {
            Assert.False(invocation.CancelledByCaller);
            Assert.False(invocation.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public void RepeatedPendingCancellation_DoesNotRefreshTtl()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new InvocationCancellationRegistry(
            pendingCancellationTtl: TimeSpan.FromSeconds(5),
            timeProvider: timeProvider);

        Assert.Equal(
            InvocationCancellationResult.Pending,
            registry.TryCancelOrRemember("request"));
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(
            InvocationCancellationResult.Pending,
            registry.TryCancelOrRemember("request"));
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.True(registry.TryRegister("request", CancellationToken.None, out var invocationCandidate));
        using var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(
            invocationCandidate);
        Assert.False(invocation.CancelledByCaller);
    }

    [Fact]
    public void LateCancellation_AfterCompletionDoesNotPoisonReuse()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new InvocationCancellationRegistry(
            pendingCancellationTtl: TimeSpan.FromSeconds(5),
            timeProvider: timeProvider);
        Assert.True(registry.TryRegister("request", CancellationToken.None, out var firstCandidate));
        var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        using (first)
        {
            Assert.True(first.TryComplete());
        }

        Assert.Equal(
            InvocationCancellationResult.NotFound,
            registry.TryCancelOrRemember("request"));
        Assert.Equal(0, registry.PendingCancellationCount);

        Assert.True(registry.TryRegister("request", CancellationToken.None, out var replacementCandidate));
        var replacement = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(replacementCandidate);
        using (replacement)
        {
            Assert.False(replacement.CancelledByCaller);
        }
    }

    [Fact]
    public void PendingCancellationCap_EvictsOldestEntryDeterministically()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new InvocationCancellationRegistry(
            pendingCancellationTtl: TimeSpan.FromSeconds(5),
            maxPendingCancellations: 2,
            timeProvider: timeProvider);

        Assert.Equal(InvocationCancellationResult.Pending, registry.TryCancelOrRemember("first"));
        Assert.Equal(InvocationCancellationResult.Pending, registry.TryCancelOrRemember("second"));
        Assert.Equal(InvocationCancellationResult.Pending, registry.TryCancelOrRemember("third"));
        Assert.Equal(2, registry.PendingCancellationCount);

        Assert.True(registry.TryRegister("first", CancellationToken.None, out var firstCandidate));
        Assert.True(registry.TryRegister("second", CancellationToken.None, out var secondCandidate));
        Assert.True(registry.TryRegister("third", CancellationToken.None, out var thirdCandidate));
        using var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        using var second = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(secondCandidate);
        using var third = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(thirdCandidate);

        Assert.False(first.CancelledByCaller);
        Assert.True(second.CancelledByCaller);
        Assert.True(third.CancelledByCaller);
    }

    [Fact]
    public void RecentCompletionCap_EvictsOldestEntryDeterministically()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new InvocationCancellationRegistry(
            pendingCancellationTtl: TimeSpan.FromSeconds(5),
            maxRecentCompletions: 2,
            timeProvider: timeProvider);

        Complete("first");
        Complete("second");
        Complete("third");
        Assert.Equal(2, registry.RecentCompletionCount);

        Assert.Equal(InvocationCancellationResult.Pending, registry.TryCancelOrRemember("first"));
        Assert.Equal(InvocationCancellationResult.NotFound, registry.TryCancelOrRemember("second"));
        Assert.Equal(InvocationCancellationResult.NotFound, registry.TryCancelOrRemember("third"));

        void Complete(string requestId)
        {
            Assert.True(registry.TryRegister(requestId, CancellationToken.None, out var candidate));
            var invocation = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(candidate);
            using (invocation)
            {
                Assert.True(invocation.TryComplete());
            }
        }
    }
}
