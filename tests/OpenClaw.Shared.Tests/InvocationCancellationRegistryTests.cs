using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class InvocationCancellationRegistryTests
{
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
    public async Task RegistrationWindow_RemainsAmbiguousWhenFirstDuplicateCompletes()
    {
        var registry = new InvocationCancellationRegistry(allowDuplicateIds: true);
        var cancellationTask = registry.TryCancelAfterRegistrationWindowAsync(
            "request",
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.True(registry.TryRegister("request", CancellationToken.None, out var firstCandidate));
        var first = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(firstCandidate);
        using (first)
        {
            Assert.True(first.TryComplete());
        }

        Assert.True(registry.TryRegister("request", CancellationToken.None, out var secondCandidate));
        var second = Assert.IsType<InvocationCancellationRegistry.InvocationCancellation>(secondCandidate);
        using (second)
        {
            var result = await cancellationTask;
            Assert.False(result.Cancelled);
            Assert.True(result.Ambiguous);
            Assert.False(second.Token.IsCancellationRequested);
        }
    }
}
