using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class DevicePairApprovalCoordinatorTests
{
    [Fact]
    public async Task PostApproveReconnect_IsBounded()
    {
        var node = new FakeNodeReconnectPort();
        var gateway = new ApprovalGatewayClient();
        var leases = new ApprovalGatewayLeaseSource(node, gateway);
        var firstDelayStarted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelay =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCount = 0;
        Task Delay(TimeSpan _)
        {
            if (Interlocked.Increment(ref delayCount) == 1)
            {
                firstDelayStarted.TrySetResult();
                return releaseFirstDelay.Task;
            }
            return Task.CompletedTask;
        }

        var coordinator = new DevicePairApprovalCoordinator(
            node,
            leases,
            NullLogger.Instance,
            new ConnectionDiagnostics(),
            Delay);
        var pending = new PairingStatusEventArgs(
            PairingStatus.Pending,
            "node-1",
            requestId: "request-1",
            approvalKind: PairingApprovalKind.DevicePair);

        coordinator.HandlePairingStatus(pending, node.Current);
        await firstDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => gateway.ApprovalCount == 1);

        coordinator.HandlePairingStatus(pending, node.Current);
        coordinator.HandlePairingStatus(pending, node.Current);
        releaseFirstDelay.TrySetResult();

        await WaitUntilAsync(() => node.StartCount == 2);
        coordinator.HandlePairingStatus(pending, node.Current);
        // slopwatch-ignore: SW004 Bounded delay proves the exhausted request does not schedule a third reconnect.
        await Task.Delay(50);

        Assert.Equal(1, gateway.ApprovalCount);
        Assert.Equal(2, node.StartCount);
        Assert.Equal(2, Volatile.Read(ref delayCount));
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StaleApproval_DoesNotPoisonSuccessfulRequestDedupe()
    {
        var node = new FakeNodeReconnectPort();
        var gateway = new ApprovalGatewayClient();
        var approvalStarted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApproval =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ApproveAsync = _ =>
        {
            approvalStarted.TrySetResult();
            return releaseApproval.Task;
        };
        var coordinator = new DevicePairApprovalCoordinator(
            node,
            new ApprovalGatewayLeaseSource(node, gateway),
            NullLogger.Instance,
            new ConnectionDiagnostics(),
            _ => Task.CompletedTask);
        var pending = new PairingStatusEventArgs(
            PairingStatus.Pending,
            "node-1",
            requestId: "request-1",
            approvalKind: PairingApprovalKind.DevicePair);

        coordinator.HandlePairingStatus(pending, node.Current);
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        node.AdvanceGeneration();
        releaseApproval.TrySetResult(true);
        await WaitUntilAsync(() => gateway.ApprovalCount == 1);
        // slopwatch-ignore: SW004 Bounded delay lets the stale approval continuation release its CAS lease.
        await Task.Delay(50);

        gateway.ApproveAsync = _ => Task.FromResult(true);
        coordinator.HandlePairingStatus(pending, node.Current);
        await WaitUntilAsync(() => gateway.ApprovalCount == 2);

        Assert.Equal(2, gateway.ApprovalCount);
        await coordinator.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition timed out.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeNodeReconnectPort : INodePairReconnectPort
    {
        public NodeAttemptStamp Current { get; private set; } =
            new(new GatewayAttemptStamp(1, "gw-1"), 1);

        public int StartCount { get; private set; }

        public void AdvanceGeneration() =>
            Current = Current with { NodeGeneration = Current.NodeGeneration + 1 };

        public bool IsCurrentNodeAttempt(NodeAttemptStamp attempt) =>
            attempt == Current;

        public Task<NodeStartResult> StartAsync(
            long expectedLifecycleGeneration,
            long? expectedNodeGeneration = null)
        {
            if (expectedLifecycleGeneration != Current.GatewayAttempt.LifecycleGeneration ||
                expectedNodeGeneration != Current.NodeGeneration)
            {
                return Task.FromResult(
                    new NodeStartResult(NodeStartOutcome.Superseded));
            }

            StartCount++;
            Current = Current with { NodeGeneration = Current.NodeGeneration + 1 };
            return Task.FromResult(
                new NodeStartResult(NodeStartOutcome.Started, Current));
        }
    }

    private sealed class ApprovalGatewayLeaseSource(
        FakeNodeReconnectPort node,
        ApprovalGatewayClient gateway) : IOperatorApprovalGatewayLeaseSource
    {
        public OperatorApprovalGatewayLease? TryAcquireOperatorApprovalGateway(
            NodeAttemptStamp attempt) =>
            node.IsCurrentNodeAttempt(attempt)
                ? new OperatorApprovalGatewayLease(attempt, gateway)
                : null;
    }

    private sealed class ApprovalGatewayClient : OpenClawGatewayClient
    {
        public ApprovalGatewayClient()
            : base("wss://gateway.example", "test-token", NullLogger.Instance)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_grantedOperatorScopes",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(this, new[] { "operator.admin" });
        }

        public int ApprovalCount { get; private set; }
        public Func<string, Task<bool>>? ApproveAsync { get; set; }
        public override bool IsConnectedToGateway => true;

        public override Task<bool> DevicePairApproveAsync(string requestId)
        {
            ApprovalCount++;
            return ApproveAsync?.Invoke(requestId) ?? Task.FromResult(true);
        }
    }
}
