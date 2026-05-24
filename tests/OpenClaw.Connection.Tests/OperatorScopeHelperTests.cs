using OpenClaw.Connection;
using OpenClaw.Shared;
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
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ReturnsTrue_WhenStateEventFires()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);

        // Fire the Connected state event after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            mgr.SimulateConnected();
        });

        var result = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, TimeSpan.FromMilliseconds(500));
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForOperatorHandshakeAsync_ThrowsOperationCanceled_WhenTokenCancelled()
    {
        var mgr = new StubConnectionManager(RoleConnectionState.Connecting);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(30);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ChatNavigationReadiness.WaitForOperatorHandshakeAsync(mgr, TimeSpan.FromSeconds(10), cts.Token));
    }

    // ─── Stub ───

    private sealed class StubConnectionManager : IGatewayConnectionManager
    {
        private RoleConnectionState _operatorState;

        public StubConnectionManager(RoleConnectionState operatorState)
        {
            _operatorState = operatorState;
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
            _operatorState = RoleConnectionState.Connected;
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
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task ReconnectAsync() => Task.CompletedTask;
        public Task SwitchGatewayAsync(string gatewayId) => Task.CompletedTask;
        public Task EnsureNodeConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode) => Task.FromResult(new SetupCodeResult(SetupCodeOutcome.InvalidCode));
        public Task<SetupCodeResult> ConnectWithSharedTokenAsync(string gatewayUrl, string token, SshTunnelConfig? sshTunnel = null) => Task.FromResult(new SetupCodeResult(SetupCodeOutcome.InvalidCode));
        public void Dispose() { }
    }
}
