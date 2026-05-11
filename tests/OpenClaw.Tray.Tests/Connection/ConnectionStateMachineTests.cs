using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

public class ConnectionStateMachineTests
{
    private readonly ConnectionStateMachine _sm = new();

    // ─── Initial state ───

    [Fact]
    public void InitialState_IsIdle()
    {
        Assert.Equal(OverallConnectionState.Idle, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Idle, _sm.Current.OperatorState);
        Assert.Equal(RoleConnectionState.Idle, _sm.Current.NodeState);
    }

    // ─── Operator: Idle → Connecting → Connected ───

    [Fact]
    public void Idle_ConnectRequested_TransitionsToConnecting()
    {
        Assert.True(_sm.TryTransition(ConnectionTrigger.ConnectRequested));
        Assert.Equal(OverallConnectionState.Connecting, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Connecting, _sm.Current.OperatorState);
    }

    [Fact]
    public void Connecting_HandshakeSucceeded_TransitionsToConnected()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.HandshakeSucceeded));
        Assert.Equal(RoleConnectionState.Connected, _sm.Current.OperatorState);
    }

    [Fact]
    public void Connected_DisconnectRequested_TransitionsToIdle()
    {
        GoToConnected();
        Assert.True(_sm.TryTransition(ConnectionTrigger.DisconnectRequested));
        Assert.Equal(OverallConnectionState.Idle, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Idle, _sm.Current.OperatorState);
    }

    // ─── Operator: Connecting sub-steps ───

    [Fact]
    public void Connecting_ConnectRequestSent_StaysConnecting()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.ConnectRequestSent));
        Assert.Equal(RoleConnectionState.Connecting, _sm.Current.OperatorState);
    }

    [Fact]
    public void Connecting_ChallengeReceived_StaysConnecting()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.ChallengeReceived));
        Assert.Equal(RoleConnectionState.Connecting, _sm.Current.OperatorState);
    }

    [Fact]
    public void Connecting_WebSocketConnected_StaysConnecting()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketConnected));
        Assert.Equal(RoleConnectionState.Connecting, _sm.Current.OperatorState);
    }

    // ─── Operator: Pairing ───

    [Fact]
    public void Connecting_PairingPending_TransitionsToPairingRequired()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.PairingPending));
        Assert.Equal(OverallConnectionState.PairingRequired, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.PairingRequired, _sm.Current.OperatorState);
        Assert.True(_sm.Current.OperatorPairingRequired);
    }

    [Fact]
    public void PairingRequired_PairingApproved_TransitionsToConnecting()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.TryTransition(ConnectionTrigger.PairingPending);
        Assert.True(_sm.TryTransition(ConnectionTrigger.PairingApproved));
        Assert.Equal(RoleConnectionState.Connecting, _sm.Current.OperatorState);
    }

    [Fact]
    public void PairingRequired_PairingRejected_TransitionsToError()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.TryTransition(ConnectionTrigger.PairingPending);
        Assert.True(_sm.TryTransition(ConnectionTrigger.PairingRejected));
        Assert.Equal(OverallConnectionState.Error, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Error, _sm.Current.OperatorState);
    }

    [Fact]
    public void PairingRequired_WebSocketDisconnected_StaysInPairingRequired()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.TryTransition(ConnectionTrigger.PairingPending);
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketDisconnected));
        Assert.Equal(OverallConnectionState.PairingRequired, _sm.Current.OverallState);
    }

    // ─── Operator: Error states ───

    [Fact]
    public void Connecting_AuthenticationFailed_TransitionsToError()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.AuthenticationFailed, "bad token"));
        Assert.Equal(OverallConnectionState.Error, _sm.Current.OverallState);
        Assert.Equal("bad token", _sm.Current.OperatorError);
    }

    [Fact]
    public void Connecting_RateLimited_TransitionsToError()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.RateLimited));
        Assert.Equal(OverallConnectionState.Error, _sm.Current.OverallState);
    }

    [Fact]
    public void Connecting_WebSocketError_TransitionsToError()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketError));
        Assert.Equal(OverallConnectionState.Error, _sm.Current.OverallState);
    }

    [Fact]
    public void Connected_WebSocketDisconnected_TransitionsToConnecting()
    {
        GoToConnected();
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketDisconnected));
        Assert.Equal(OverallConnectionState.Connecting, _sm.Current.OverallState);
    }

    [Fact]
    public void Connected_WebSocketError_TransitionsToError()
    {
        GoToConnected();
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketError));
        Assert.Equal(OverallConnectionState.Error, _sm.Current.OverallState);
    }

    // ─── Error → Reconnect ───

    [Fact]
    public void Error_ConnectRequested_TransitionsToConnecting()
    {
        GoToError();
        Assert.True(_sm.TryTransition(ConnectionTrigger.ConnectRequested));
        Assert.Equal(OverallConnectionState.Connecting, _sm.Current.OverallState);
    }

    [Fact]
    public void Error_ReconnectScheduled_TransitionsToConnecting()
    {
        GoToError();
        Assert.True(_sm.TryTransition(ConnectionTrigger.ReconnectScheduled));
        Assert.Equal(OverallConnectionState.Connecting, _sm.Current.OverallState);
    }

    [Fact]
    public void Error_ReconnectSuppressed_StaysInError()
    {
        GoToError();
        Assert.True(_sm.TryTransition(ConnectionTrigger.ReconnectSuppressed));
        Assert.Equal(OverallConnectionState.Error, _sm.Current.OverallState);
    }

    [Fact]
    public void Error_DisconnectRequested_TransitionsToIdle()
    {
        GoToError();
        Assert.True(_sm.TryTransition(ConnectionTrigger.DisconnectRequested));
        Assert.Equal(OverallConnectionState.Idle, _sm.Current.OverallState);
    }

    // ─── Operator: Cancelled ───

    [Fact]
    public void Connecting_Cancelled_TransitionsToIdle()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.True(_sm.TryTransition(ConnectionTrigger.Cancelled));
        Assert.Equal(OverallConnectionState.Idle, _sm.Current.OverallState);
    }

    // ─── Disposed (from any state) ───

    [Fact]
    public void Disposed_FromAnyState_TransitionsToIdle()
    {
        GoToConnected();
        Assert.True(_sm.TryTransition(ConnectionTrigger.Disposed));
        Assert.Equal(OverallConnectionState.Idle, _sm.Current.OverallState);
    }

    // ─── Invalid transitions ───

    [Fact]
    public void InvalidTransition_ReturnsFalse()
    {
        // Can't handshake from Idle
        Assert.False(_sm.TryTransition(ConnectionTrigger.HandshakeSucceeded));
    }

    [Fact]
    public void InvalidTransition_DoesNotChangeState()
    {
        var before = _sm.Current;
        _sm.TryTransition(ConnectionTrigger.HandshakeSucceeded);
        Assert.Equal(before, _sm.Current);
    }

    [Fact]
    public void CannotConnectFromConnecting()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        Assert.False(_sm.TryTransition(ConnectionTrigger.ConnectRequested));
    }

    // ─── Node sub-FSM ───

    [Fact]
    public void NodeConnected_WithOperatorConnected_DerivesReady()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeConnected));
        Assert.Equal(OverallConnectionState.Ready, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Connected, _sm.Current.NodeState);
    }

    [Fact]
    public void NodeError_WithOperatorConnected_DerivesDegraded()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.TryTransition(ConnectionTrigger.NodeConnected);
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeError, "node failed"));
        Assert.Equal(OverallConnectionState.Degraded, _sm.Current.OverallState);
        Assert.Equal("node failed", _sm.Current.NodeError);
    }

    [Fact]
    public void NodePairingRequired_WithOperatorConnected_DerivesPairingRequired()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodePairingRequired));
        Assert.Equal(OverallConnectionState.PairingRequired, _sm.Current.OverallState);
        Assert.Equal(OpenClaw.Shared.PairingStatus.Pending, _sm.Current.NodePairingStatus);
    }

    [Fact]
    public void NodePaired_TransitionsToConnected()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.TryTransition(ConnectionTrigger.NodePairingRequired);
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodePaired));
        Assert.Equal(RoleConnectionState.Connected, _sm.Current.NodeState);
        Assert.Equal(OpenClaw.Shared.PairingStatus.Paired, _sm.Current.NodePairingStatus);
    }

    [Fact]
    public void NodePairingRejected_DerivesDegraded()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.TryTransition(ConnectionTrigger.NodePairingRequired);
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodePairingRejected));
        Assert.Equal(OverallConnectionState.Degraded, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.PairingRejected, _sm.Current.NodeState);
    }

    [Fact]
    public void NodeDisconnected_FromConnected_DerivesConnected()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.TryTransition(ConnectionTrigger.NodeConnected);
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeDisconnected));
        // Operator still connected, node idle → Connected (not Ready)
        Assert.Equal(RoleConnectionState.Idle, _sm.Current.NodeState);
    }

    [Fact]
    public void NodeRateLimited_DerivesDegraded()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.TryTransition(ConnectionTrigger.NodeConnected);
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeRateLimited));
        Assert.Equal(OverallConnectionState.Degraded, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.RateLimited, _sm.Current.NodeState);
    }

    // ─── Node disabled ───

    [Fact]
    public void NodeDisabled_OperatorConnected_DerivesReady()
    {
        _sm.SetNodeEnabled(false);
        GoToConnected();
        Assert.Equal(OverallConnectionState.Ready, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Disabled, _sm.Current.NodeState);
    }

    // ─── SetNodeEnabled ───

    [Fact]
    public void SetNodeEnabled_True_SetsNodeToIdle()
    {
        _sm.SetNodeEnabled(true);
        Assert.Equal(RoleConnectionState.Idle, _sm.Current.NodeState);
    }

    [Fact]
    public void SetNodeEnabled_False_SetsNodeToDisabled()
    {
        _sm.SetNodeEnabled(false);
        Assert.Equal(RoleConnectionState.Disabled, _sm.Current.NodeState);
    }

    // ─── Reset ───

    [Fact]
    public void Reset_ReturnsToIdle()
    {
        GoToConnected();
        _sm.Reset();
        Assert.Equal(OverallConnectionState.Idle, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Idle, _sm.Current.OperatorState);
    }

    // ─── DeriveOverall static method ───

    [Theory]
    [InlineData(RoleConnectionState.Error, RoleConnectionState.Idle, true, OverallConnectionState.Error)]
    [InlineData(RoleConnectionState.PairingRequired, RoleConnectionState.Idle, true, OverallConnectionState.PairingRequired)]
    [InlineData(RoleConnectionState.Connecting, RoleConnectionState.Idle, true, OverallConnectionState.Connecting)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Connected, true, OverallConnectionState.Ready)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Connected, false, OverallConnectionState.Ready)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Disabled, true, OverallConnectionState.Ready)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Error, true, OverallConnectionState.Degraded)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.PairingRejected, true, OverallConnectionState.Degraded)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.RateLimited, true, OverallConnectionState.Degraded)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.PairingRequired, true, OverallConnectionState.PairingRequired)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Connecting, true, OverallConnectionState.Connecting)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Idle, false, OverallConnectionState.Ready)]
    [InlineData(RoleConnectionState.Idle, RoleConnectionState.Idle, true, OverallConnectionState.Idle)]
    public void DeriveOverall_ReturnsCorrectState(
        RoleConnectionState op, RoleConnectionState node, bool nodeEnabled, OverallConnectionState expected)
    {
        Assert.Equal(expected, GatewayConnectionSnapshot.DeriveOverall(op, node, nodeEnabled));
    }

    // ─── CanTransition ───

    [Fact]
    public void CanTransition_ReflectsTryTransition()
    {
        Assert.True(_sm.CanTransition(ConnectionTrigger.ConnectRequested));
        Assert.False(_sm.CanTransition(ConnectionTrigger.HandshakeSucceeded));
    }

    // ─── Helpers ───

    private void GoToConnected()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.TryTransition(ConnectionTrigger.HandshakeSucceeded);
    }

    private void GoToError()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.TryTransition(ConnectionTrigger.WebSocketError, "test error");
    }
}
