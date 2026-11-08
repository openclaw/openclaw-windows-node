using OpenClaw.Connection;
using OpenClaw.Shared;
using System.Collections.Concurrent;
using System.Reflection;
using Xunit;

namespace OpenClaw.Connection.Tests;

public class OperatorScopeHelperTests
{
    // ─── CanApproveDevices ───

    [Fact]
    public void CanApproveDevices_ReturnsFalse_ForEmptyList()
    {
        Assert.False(OperatorScopeHelper.CanApproveDevices([]));
    }

    [Theory]
    [InlineData("operator.admin")]
    [InlineData("operator.pairing")]
    [InlineData("OPERATOR.ADMIN")]
    [InlineData("OPERATOR.PAIRING")]
    [InlineData("Operator.Admin")]
    [InlineData("Operator.Pairing")]
    public void CanApproveDevices_ReturnsTrue_ForApprovalScope(string scope)
    {
        Assert.True(OperatorScopeHelper.CanApproveDevices([scope]));
    }

    [Theory]
    [InlineData("operator.read")]
    [InlineData("node.invoke")]
    [InlineData("admin")]
    [InlineData("pairing")]
    [InlineData("operator")]
    [InlineData("")]
    public void CanApproveDevices_ReturnsFalse_ForNonApprovalScope(string scope)
    {
        Assert.False(OperatorScopeHelper.CanApproveDevices([scope]));
    }

    [Fact]
    public void CanApproveDevices_ReturnsTrue_WhenAdminIsAmongMultipleScopes()
    {
        Assert.True(OperatorScopeHelper.CanApproveDevices(["operator.read", "operator.admin", "node.invoke"]));
    }

    [Fact]
    public void CanApproveDevices_ReturnsTrue_WhenPairingIsAmongMultipleScopes()
    {
        Assert.True(OperatorScopeHelper.CanApproveDevices(["operator.read", "operator.pairing"]));
    }

    [Fact]
    public void CanApproveDevices_ReturnsFalse_WhenNoApprovalScopePresent()
    {
        Assert.False(OperatorScopeHelper.CanApproveDevices(["operator.read", "node.invoke", "user.profile"]));
    }
}

public class ChatNavigationReadinessTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
    // ─── IsOperatorHandshakeReady ───

    [Fact]
    public void IsOperatorHandshakeReady_ReturnsTrue_WhenManagerIsNull()
    {
        Assert.True(ChatNavigationReadiness.IsOperatorHandshakeReady(null));
    }

    [Fact]
    public void IsOperatorHandshakeReady_ReturnsTrue_WhenOperatorIsConnected()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connected);
        Assert.True(ChatNavigationReadiness.IsOperatorHandshakeReady(mgr));
    }

    [Theory]
    [InlineData(RoleConnectionState.Idle)]
    [InlineData(RoleConnectionState.Connecting)]
    [InlineData(RoleConnectionState.Error)]
    [InlineData(RoleConnectionState.PairingRequired)]
    public void IsOperatorHandshakeReady_ReturnsFalse_WhenOperatorIsNotConnected(RoleConnectionState state)
    {
        var mgr = new StubConnectionManager(state);
        Assert.False(ChatNavigationReadiness.IsOperatorHandshakeReady(mgr));
    }

    // ─── WaitForOperatorHandshakeAsync ───

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ReturnsTrue_WhenAlreadyConnected()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connected);
        var result = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, TimeSpan.FromMilliseconds(100));
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ReturnsTrue_WhenNullManager()
    {
        var result = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(null, TimeSpan.FromMilliseconds(100));
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ReturnsFalse_OnTimeout()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        var result = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, TimeSpan.FromMilliseconds(50));
        Assert.False(result);
        Assert.Equal(0, mgr.SubscriberCount);
        mgr.SimulateConnected();
        Assert.False(result);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ReturnsTrue_WhenStateEventFires()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);

        var waitTask = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, Timeout.InfiniteTimeSpan);
        Assert.Equal(1, mgr.SubscriberCount);
        mgr.SimulateConnected();

        var result = await waitTask.WaitAsync(HangGuard);
        Assert.True(result);
        Assert.Equal(0, mgr.SubscriberCount);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ThrowsOperationCanceled_WhenTokenCancelled()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        using var cts = new CancellationTokenSource();
        var waitTask = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, Timeout.InfiniteTimeSpan, cts.Token);
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask.WaitAsync(HangGuard));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(0, mgr.SubscriberCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForOperatorHandshakeAsync_CancellationAfterQueuedCompletion_PreservesOutcomePrecedence(
        bool connectedBeforeCancellation)
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        using var cts = new CancellationTokenSource();
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        Task<bool> waitTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            waitTask = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
                mgr,
                connectedBeforeCancellation ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(1),
                cts.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        try
        {
            if (connectedBeforeCancellation)
                mgr.SimulateConnected();

            await context.Posted.WaitAsync(HangGuard);
            Assert.False(waitTask.IsCompleted);
            Assert.Equal(1, mgr.SubscriberCount);
            cts.Cancel();
            context.Drain();

            if (connectedBeforeCancellation)
            {
                Assert.True(await waitTask.WaitAsync(HangGuard));
            }
            else
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => waitTask.WaitAsync(HangGuard));
                Assert.Equal(cts.Token, error.CancellationToken);
            }
            Assert.Equal(0, mgr.SubscriberCount);
        }
        finally
        {
            cts.Cancel();
            context.Drain();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForOperatorHandshakeAsync_PreservesReadyFirstPrecedence(bool nullManager)
    {
        var mgr = nullManager ? null : new StubConnectionManager(RoleConnectionState.Connected);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.True(await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            mgr, TimeSpan.FromMilliseconds(-2), cts.Token));
        if (mgr != null) Assert.Equal(0, mgr.SubscriberCount);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(4294967295)]
    public async Task WaitForOperatorHandshakeAsync_RejectsInvalidTimeout_AndDetachesHandler(long milliseconds)
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
                mgr, TimeSpan.FromMilliseconds(milliseconds), cts.Token));
        Assert.Equal(0, mgr.SubscriberCount);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_PreCancelledTokenBeatsZeroTimeout()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, TimeSpan.Zero, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(0, mgr.SubscriberCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    public async Task WaitForOperatorHandshakeAsync_ZeroMillisecondDeadlineCompletesSynchronously(double milliseconds)
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        var waitTask = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            mgr, TimeSpan.FromMilliseconds(milliseconds));

        Assert.True(waitTask.IsCompleted);
        Assert.False(await waitTask);
        Assert.Equal(0, mgr.SubscriberCount);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_RechecksSnapshotAfterSubscription()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting) { ConnectOnSubscription = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.True(await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            mgr, TimeSpan.FromMilliseconds(-2), cts.Token));
        Assert.Equal(0, mgr.SubscriberCount);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_TimelySuccessWins_WhenNotificationIsHeldPastDeadline()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        using var cts = new CancellationTokenSource();
        using var gate = new HandshakeCompletionNotificationGate(HangGuard);
        var deadline = TimeSpan.FromSeconds(1);
        gate.BeginDelayCapture();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var waitTask = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, deadline, cts.Token);
        gate.EndDelayCapture();
        var completion = mgr.FindCapturedCompletion();
        using var deadlineObservation = gate.ObserveDeadline(completion.Task);
        gate.Arm(completion.Task);
        Exception? eventFailure = null;
        var eventThread = new Thread(() =>
        {
            try { mgr.SimulateConnected(); }
            catch (Exception error) { eventFailure = error; }
        }) { IsBackground = true };
        eventThread.Start();
        try
        {
            Assert.True(gate.Entered.Wait(HangGuard), "The completion-notification gate was not reached.");
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started, gate.SuccessPublishedAt) < deadline,
                "Success must be published before the real deadline, not injected after it.");
            Assert.True(completion.Task.IsCompletedSuccessfully);
            Assert.True(await completion.Task);
            Assert.False(waitTask.IsCompleted);
            Assert.True(gate.DeadlineElapsed.Wait(HangGuard), "The helper's actual deadline did not fire.");
            cts.Cancel();
        }
        finally
        {
            gate.Release.Set();
            Assert.True(eventThread.Join(HangGuard), "The event thread did not exit.");
        }

        Assert.Null(eventFailure);
        Assert.Null(gate.Fault);
        Assert.True(await waitTask.WaitAsync(HangGuard));
        Assert.Equal(0, mgr.SubscriberCount);
    }

    // ─── Stub ───

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            _posted.TrySetResult();
        }

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var callback))
                callback.Callback(callback.State);
        }
    }

    private sealed class StubConnectionManager : IGatewayConnectionManager
    {
        public StubConnectionManager(RoleConnectionState operatorState)
        {
            CurrentSnapshot = BuildSnapshot(operatorState);
        }

        public GatewayConnectionSnapshot CurrentSnapshot { get; private set; }
        public string? ActiveGatewayUrl => null;
        public IOperatorGatewayClient? OperatorClient => null;
        public ConnectionDiagnostics Diagnostics { get; } = new();

        private EventHandler<GatewayConnectionSnapshot>? _stateChanged;
        public int SubscriberCount => _stateChanged?.GetInvocationList().Length ?? 0;
        public bool ConnectOnSubscription { get; init; }
        public event EventHandler<GatewayConnectionSnapshot>? StateChanged
        {
            add
            {
                _stateChanged += value;
                if (ConnectOnSubscription) CurrentSnapshot = BuildSnapshot(RoleConnectionState.Connected);
            }
            remove => _stateChanged -= value;
        }
#pragma warning disable CS0067
        public event EventHandler<ConnectionDiagnosticEvent>? DiagnosticEvent;
        public event EventHandler<OperatorClientChangedEventArgs>? OperatorClientChanged;
#pragma warning restore CS0067

        public void SimulateConnected()
        {
            CurrentSnapshot = BuildSnapshot(RoleConnectionState.Connected);
            _stateChanged?.Invoke(this, CurrentSnapshot);
        }

        public TaskCompletionSource<bool> FindCapturedCompletion()
        {
            var target = _stateChanged!.Target!;
            return target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => field.GetValue(target))
                .OfType<TaskCompletionSource<bool>>().Single();
        }

        private static GatewayConnectionSnapshot BuildSnapshot(RoleConnectionState op) => new()
        {
            OperatorState = op,
            NodeState = RoleConnectionState.Idle,
            OverallState = OverallConnectionState.Idle,
            NodePairingStatus = PairingStatus.Unknown
        };

        public Task ConnectAsync(string? gatewayId = null) => Task.CompletedTask;
        public Task ConnectNodeOnlyAsync(string? gatewayId = null) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task DisconnectByUserAsync() => Task.CompletedTask;
        public Task ReconnectAsync() => Task.CompletedTask;
        public Task<bool> ReconnectIfCurrentAsync(string gatewayId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<bool> RecoverSshTunnelAsync(SshTunnelExit tunnelExit) => Task.FromResult(false);
        public Task SwitchGatewayAsync(string gatewayId) => Task.CompletedTask;
        public void SetGatewayConnectionIntent(string gatewayId, bool shouldBeConnected) { }
        public bool IsAutomaticReconnectAllowed(string gatewayId) => true;
        public bool IsManualGatewayLifecycleInProgress => false;
        public Task<IDisposable> BeginManualGatewayLifecycleOperationAsync(CancellationToken cancellationToken = default) => Task.FromResult<IDisposable>(new NoopScope());
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
        public Task EnsureNodeConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode, SshTunnelConfig? sshTunnel = null) => Task.FromResult(new SetupCodeResult(SetupCodeOutcome.InvalidCode));
        public Task<SetupCodeResult> ConnectWithSharedTokenAsync(string gatewayUrl, string token, SshTunnelConfig? sshTunnel = null) => Task.FromResult(new SetupCodeResult(SetupCodeOutcome.InvalidCode));
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
