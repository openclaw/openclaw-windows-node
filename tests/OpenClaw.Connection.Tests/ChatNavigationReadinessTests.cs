using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClawTray.Tests.Connection;

/// <summary>
/// Tests for ChatNavigationReadiness: both the synchronous IsOperatorHandshakeReady
/// and the async WaitForOperatorHandshakeAsync (timeout and cancellation paths).
/// </summary>
public class ChatNavigationReadinessTests
{
    // ─── IsOperatorHandshakeReady ───

    [Fact]
    public void IsOperatorHandshakeReady_NullManager_ReturnsTrue()
    {
        Assert.True(ChatNavigationReadiness.IsOperatorHandshakeReady(null));
    }

    [Fact]
    public void IsOperatorHandshakeReady_OperatorConnected_ReturnsTrue()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connected);
        Assert.True(ChatNavigationReadiness.IsOperatorHandshakeReady(mgr));
    }

    [Theory]
    [InlineData(RoleConnectionState.Idle)]
    [InlineData(RoleConnectionState.Connecting)]
    [InlineData(RoleConnectionState.Error)]
    [InlineData(RoleConnectionState.PairingRequired)]
    public void IsOperatorHandshakeReady_OperatorNotConnected_ReturnsFalse(RoleConnectionState state)
    {
        var mgr = new StubConnectionManager(state);
        Assert.False(ChatNavigationReadiness.IsOperatorHandshakeReady(mgr));
    }

    // ─── WaitForOperatorHandshakeAsync: null manager ───

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_NullManager_ReturnsTrueImmediately()
    {
        var result = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            null, TimeSpan.FromMilliseconds(50));
        Assert.True(result);
    }

    // ─── WaitForOperatorHandshakeAsync: already ready ───

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_AlreadyConnected_ReturnsTrueImmediately()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connected);
        var result = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            mgr, TimeSpan.FromMilliseconds(50));
        Assert.True(result);
    }

    // ─── WaitForOperatorHandshakeAsync: waits for state change ───

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ConnectsBeforeTimeout_ReturnsTrue()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);

        var waitTask = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            mgr, TimeSpan.FromSeconds(5));

        // Signal connected on a background thread after a short delay.
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            mgr.SimulateConnected();
        });

        var result = await waitTask;
        Assert.True(result);
    }

    // ─── WaitForOperatorHandshakeAsync: timeout ───

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_NeverConnects_ReturnsFalseAfterTimeout()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        var result = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            mgr, TimeSpan.FromMilliseconds(50));
        Assert.False(result);
    }

    // ─── WaitForOperatorHandshakeAsync: cancellation ───

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_Cancelled_ThrowsOperationCanceledException()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
                mgr, TimeSpan.FromSeconds(5), cts.Token));
    }

    // ─── Stub ───

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

        public event EventHandler<GatewayConnectionSnapshot>? StateChanged;
#pragma warning disable CS0067
        public event EventHandler<ConnectionDiagnosticEvent>? DiagnosticEvent;
        public event EventHandler<OperatorClientChangedEventArgs>? OperatorClientChanged;
#pragma warning restore CS0067

        public void SimulateConnected()
        {
            CurrentSnapshot = BuildSnapshot(RoleConnectionState.Connected);
            StateChanged?.Invoke(this, CurrentSnapshot);
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
        public Task ReconnectAsync() => Task.CompletedTask;
        public Task SwitchGatewayAsync(string gatewayId) => Task.CompletedTask;
        public Task EnsureNodeConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode) => Task.FromResult(new SetupCodeResult(SetupCodeOutcome.InvalidCode));
        public Task<SetupCodeResult> ConnectWithSharedTokenAsync(string gatewayUrl, string token, SshTunnelConfig? sshTunnel = null) => Task.FromResult(new SetupCodeResult(SetupCodeOutcome.InvalidCode));
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
