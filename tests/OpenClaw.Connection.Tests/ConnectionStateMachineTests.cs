using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

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

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void HandshakeSucceeded_PreservesAcceptedOperatorProtocol(int protocol)
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.SetOperatorProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility.Compatible(protocol));

        Assert.True(_sm.TryTransition(ConnectionTrigger.HandshakeSucceeded));

        Assert.Equal(
            protocol,
            _sm.Current.OperatorProtocolCompatibility.SelectedProtocol);
        Assert.Equal(protocol, _sm.Current.ProtocolCompatibility.SelectedProtocol);
        Assert.Equal(
            GatewayProtocolCompatibilityRole.Operator,
            _sm.Current.ProtocolCompatibilityRole);
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
    public void PairingRequired_HandshakeSucceeded_TransitionsOperatorToConnected()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.TryTransition(ConnectionTrigger.PairingPending);

        Assert.True(_sm.TryTransition(ConnectionTrigger.HandshakeSucceeded));
        Assert.Equal(OverallConnectionState.Ready, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Connected, _sm.Current.OperatorState);
        Assert.False(_sm.Current.OperatorPairingRequired);
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
    public void TypedOperatorFailureKind_IsPreservedInSnapshot_AndClearedOnReconnect()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.SetOperatorErrorKind(OpenClaw.Shared.GatewayErrorKind.Tls);
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketError, "Transport error"));
        Assert.Equal(OpenClaw.Shared.GatewayErrorKind.Tls, _sm.Current.OperatorErrorKind);

        Assert.True(_sm.TryTransition(ConnectionTrigger.ReconnectScheduled));
        Assert.Null(_sm.Current.OperatorErrorKind);
    }

    [Fact]
    public void OperatorProtocolMismatch_IsDerivedAndClearedOnReconnect()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.SetOperatorErrorKind(OpenClaw.Shared.GatewayErrorKind.ProtocolMismatch);
        _sm.SetOperatorProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility.FromGatewayExpectation(2, 2));
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketError, "Transport error"));

        Assert.Equal(
            OpenClaw.Shared.GatewayProtocolCompatibilityState.GatewayTooOld,
            _sm.Current.ProtocolCompatibility.State);
        Assert.Equal(GatewayProtocolCompatibilityRole.Operator, _sm.Current.ProtocolCompatibilityRole);
        Assert.Equal(2, _sm.Current.ProtocolCompatibility.GatewayExpectedProtocol);
        Assert.False(_sm.Current.ProtocolCompatibility.Retryable);

        Assert.True(_sm.TryTransition(ConnectionTrigger.ReconnectScheduled));
        Assert.Equal(
            OpenClaw.Shared.GatewayProtocolCompatibilityState.Unknown,
            _sm.Current.ProtocolCompatibility.State);
        Assert.Null(_sm.Current.ProtocolCompatibilityRole);
    }

    [Fact]
    public void OperatorDisconnected_AfterProtocolMismatch_PreservesTerminalRecoveryState()
    {
        _sm.TryTransition(ConnectionTrigger.ConnectRequested);
        _sm.SetOperatorErrorKind(OpenClaw.Shared.GatewayErrorKind.ProtocolMismatch);
        _sm.SetOperatorProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility.FromGatewayExpectation(2, 2));
        Assert.True(_sm.TryTransition(ConnectionTrigger.WebSocketError, "Transport error"));

        Assert.False(_sm.TryTransition(ConnectionTrigger.WebSocketDisconnected));

        Assert.Equal(RoleConnectionState.Error, _sm.Current.OperatorState);
        Assert.Equal(
            OpenClaw.Shared.GatewayErrorKind.ProtocolMismatch,
            _sm.Current.OperatorErrorKind);
        Assert.Equal(
            OpenClaw.Shared.GatewayProtocolCompatibilityState.GatewayTooOld,
            _sm.Current.ProtocolCompatibility.State);
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

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void NodeConnected_PreservesAcceptedNodeProtocol(int protocol)
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.SetNodeProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility.Compatible(protocol));

        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeConnected));

        Assert.Equal(
            protocol,
            _sm.Current.NodeProtocolCompatibility.SelectedProtocol);
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
    public void NodeProtocolMismatch_IsDerivedWithoutOverwritingCompatibleOperator()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.SetNodeErrorKind(OpenClaw.Shared.GatewayErrorKind.ProtocolMismatch);
        _sm.SetNodeProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility.FromGatewayExpectation(5, 3));
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeError, "Node transport error"));

        Assert.Equal(OpenClaw.Shared.GatewayErrorKind.ProtocolMismatch, _sm.Current.NodeErrorKind);
        Assert.Equal(
            OpenClaw.Shared.GatewayProtocolCompatibilityState.GatewayTooNew,
            _sm.Current.ProtocolCompatibility.State);
        Assert.Equal(GatewayProtocolCompatibilityRole.Node, _sm.Current.ProtocolCompatibilityRole);
        Assert.Equal(5, _sm.Current.ProtocolCompatibility.GatewayExpectedProtocol);
        Assert.False(_sm.Current.ProtocolCompatibility.Retryable);
    }

    [Fact]
    public void NodeDisconnected_AfterProtocolMismatch_PreservesTerminalRecoveryState()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.SetNodeErrorKind(OpenClaw.Shared.GatewayErrorKind.ProtocolMismatch);
        _sm.SetNodeProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility.FromGatewayExpectation(5, 3));
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeError, "Node transport error"));

        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeDisconnected));

        Assert.Equal(RoleConnectionState.Error, _sm.Current.NodeState);
        Assert.Equal(
            OpenClaw.Shared.GatewayErrorKind.ProtocolMismatch,
            _sm.Current.NodeErrorKind);
        Assert.Equal(
            OpenClaw.Shared.GatewayProtocolCompatibilityState.GatewayTooNew,
            _sm.Current.ProtocolCompatibility.State);
        Assert.Equal(GatewayProtocolCompatibilityRole.Node, _sm.Current.ProtocolCompatibilityRole);
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
    public void NodePairingRequired_FromNodeError_ClearsStaleNodeError()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeError, "transport failed"));

        Assert.True(_sm.TryTransition(ConnectionTrigger.NodePairingRequired));

        Assert.Equal(OverallConnectionState.PairingRequired, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.PairingRequired, _sm.Current.NodeState);
        Assert.Null(_sm.Current.NodeError);
        Assert.Equal(OpenClaw.Shared.PairingStatus.Pending, _sm.Current.NodePairingStatus);
    }

    [Fact]
    public void SetNodeInfo_PendingWithoutRequestId_ClearsStaleRequestIdAndKind()
    {
        _sm.SetNodeInfo(
            "node-1",
            OpenClaw.Shared.PairingStatus.Pending,
            "req-1",
            OpenClaw.Shared.PairingApprovalKind.DevicePair);

        _sm.SetNodeInfo("node-1", OpenClaw.Shared.PairingStatus.Pending);

        Assert.Null(_sm.Current.NodePairingRequestId);
        Assert.Equal(OpenClaw.Shared.PairingApprovalKind.Unknown, _sm.Current.NodePairingApprovalKind);
    }

    [Fact]
    public void SetNodeInfo_UnknownKindForSameRequestId_PreservesKnownKind()
    {
        _sm.SetNodeInfo(
            "node-1",
            OpenClaw.Shared.PairingStatus.Pending,
            "req-1",
            OpenClaw.Shared.PairingApprovalKind.DevicePair);

        _sm.SetNodeInfo(
            "node-1",
            OpenClaw.Shared.PairingStatus.Pending,
            "req-1",
            OpenClaw.Shared.PairingApprovalKind.Unknown);

        Assert.Equal("req-1", _sm.Current.NodePairingRequestId);
        Assert.Equal(OpenClaw.Shared.PairingApprovalKind.DevicePair, _sm.Current.NodePairingApprovalKind);
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
    public void NodePaired_PreservesCurrentAttemptProtocol_AndNextAttemptClearsIt()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.SetNodeProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility.Compatible(5));
        _sm.TryTransition(ConnectionTrigger.NodePairingRequired);

        Assert.True(_sm.TryTransition(ConnectionTrigger.NodePaired));
        Assert.Equal(5, _sm.Current.NodeProtocolCompatibility.SelectedProtocol);

        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeDisconnected));
        _sm.StartNodeConnecting();
        Assert.Equal(
            OpenClaw.Shared.GatewayProtocolCompatibilityState.Unknown,
            _sm.Current.NodeProtocolCompatibility.State);
        Assert.Null(_sm.Current.NodeProtocolCompatibility.SelectedProtocol);
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
    public void NodeDisconnected_FromConnected_DerivesDegradedWhenNodeStillIntended()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();
        _sm.StartNodeConnecting();
        _sm.TryTransition(ConnectionTrigger.NodeConnected);
        Assert.True(_sm.TryTransition(ConnectionTrigger.NodeDisconnected));
        // Operator still connected, node mode still intended, node idle → Degraded (not healthy).
        Assert.Equal(RoleConnectionState.Idle, _sm.Current.NodeState);
        Assert.Equal(OverallConnectionState.Degraded, _sm.Current.OverallState);
        Assert.True(_sm.Current.NodeConnectionIntended);
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
        Assert.True(_sm.Current.NodeConnectionIntended);
    }

    [Fact]
    public void SetNodeEnabled_False_SetsNodeToDisabled()
    {
        _sm.SetNodeEnabled(false);
        Assert.Equal(RoleConnectionState.Disabled, _sm.Current.NodeState);
        Assert.False(_sm.Current.NodeConnectionIntended);
    }

    [Fact]
    public void BlockNodeStart_WithOperatorConnected_DerivesDegradedAndKeepsReason()
    {
        _sm.SetNodeEnabled(true);
        GoToConnected();

        _sm.BlockNodeStart("No node credential available");

        Assert.Equal(OverallConnectionState.Degraded, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Error, _sm.Current.NodeState);
        Assert.Equal("No node credential available", _sm.Current.NodeError);
        Assert.True(_sm.Current.NodeConnectionIntended);
    }

    [Fact]
    public void BlockNodeStart_WithoutOperatorConnected_DerivesErrorAndKeepsReason()
    {
        _sm.BlockNodeStart("No node credential available");

        Assert.Equal(OverallConnectionState.Error, _sm.Current.OverallState);
        Assert.Equal(RoleConnectionState.Error, _sm.Current.NodeState);
        Assert.Equal("No node credential available", _sm.Current.NodeError);
        Assert.True(_sm.Current.NodeConnectionIntended);
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
    // Node errors are suppressed when node mode is disabled → Ready (not Degraded).
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Error, false, OverallConnectionState.Ready)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.PairingRejected, false, OverallConnectionState.Ready)]
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.RateLimited, false, OverallConnectionState.Ready)]
    // Node connecting is ignored when node mode is disabled → Ready (not Connecting).
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Connecting, false, OverallConnectionState.Ready)]
    // Operator connected, node idle, node enabled → intended node is blocked/degraded.
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.Idle, true, OverallConnectionState.Degraded)]
    // Node PairingRequired is reported regardless of nodeEnabled.
    [InlineData(RoleConnectionState.Connected, RoleConnectionState.PairingRequired, false, OverallConnectionState.PairingRequired)]
    [InlineData(RoleConnectionState.Idle, RoleConnectionState.Connecting, true, OverallConnectionState.Connecting)]
    [InlineData(RoleConnectionState.Idle, RoleConnectionState.Error, true, OverallConnectionState.Error)]
    [InlineData(RoleConnectionState.Idle, RoleConnectionState.PairingRequired, true, OverallConnectionState.PairingRequired)]
    [InlineData(RoleConnectionState.Idle, RoleConnectionState.Connected, true, OverallConnectionState.Connected)]
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
