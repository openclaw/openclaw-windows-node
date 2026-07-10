using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Local MCP-only app connection controls. Do not register this capability with
/// the gateway node transport; it can repoint the tray to another gateway.
/// </summary>
public sealed class AppConnectionCapability : NodeCapabilityBase
{
    public override string Category => "app.connection";

    private static readonly string[] s_commands =
    [
        "app.connection.status",
        "app.connection.gateways",
        "app.connection.applySetupCode",
        "app.connection.connectSharedToken",
        "app.connection.pendingApprovals",
        "app.connection.approveDevicePairing",
        "app.connection.rejectDevicePairing",
        "app.connection.approveNodePairing",
        "app.connection.rejectNodePairing",
        "app.connection.reconnect",
        "app.connection.reconnectNode",
    ];

    public override IReadOnlyList<string> Commands => s_commands;

    public Func<string, Task<object?>>? ApplySetupCodeHandler;
    public Func<string, string, Task<object?>>? ConnectSharedTokenHandler;
    public Func<Task<object?>>? StatusHandler;
    public Func<Task<object?>>? GatewaysHandler;
    public Func<Task<object?>>? PendingApprovalsHandler;
    public Func<string, Task<object?>>? ApproveDevicePairingHandler;
    public Func<string, Task<object?>>? RejectDevicePairingHandler;
    public Func<string, Task<object?>>? ApproveNodePairingHandler;
    public Func<string, Task<object?>>? RejectNodePairingHandler;
    public Func<Task<object?>>? ReconnectHandler;
    public Func<Task<object?>>? ReconnectNodeHandler;

    public AppConnectionCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "app.connection.status" => await HandleStatus(),
            "app.connection.gateways" => await HandleGateways(),
            "app.connection.applySetupCode" => await HandleApplySetupCode(request),
            "app.connection.connectSharedToken" => await HandleConnectSharedToken(request),
            "app.connection.pendingApprovals" => await HandlePendingApprovals(),
            "app.connection.approveDevicePairing" => await HandleApproveDevicePairing(request),
            "app.connection.rejectDevicePairing" => await HandleRejectDevicePairing(request),
            "app.connection.approveNodePairing" => await HandleApproveNodePairing(request),
            "app.connection.rejectNodePairing" => await HandleRejectNodePairing(request),
            "app.connection.reconnect" => await HandleReconnect(),
            "app.connection.reconnectNode" => await HandleReconnectNode(),
            _ => Error($"Unknown command: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandleStatus()
    {
        if (StatusHandler == null)
            return Error("Connection status handler not registered");
        var result = await StatusHandler();
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleGateways()
    {
        if (GatewaysHandler == null)
            return Error("Connection gateways handler not registered");
        var result = await GatewaysHandler();
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleApplySetupCode(NodeInvokeRequest request)
    {
        var setupCode = GetStringArg(request.Args, "setupCode");
        if (string.IsNullOrWhiteSpace(setupCode))
            return Error("Missing required arg: setupCode");
        if (ApplySetupCodeHandler == null)
            return Error("Apply setup code handler not registered");
        var result = await ApplySetupCodeHandler(setupCode);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleConnectSharedToken(NodeInvokeRequest request)
    {
        var gatewayUrl = GetStringArg(request.Args, "gatewayUrl");
        var token = GetStringArg(request.Args, "token");
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return Error("Missing required arg: gatewayUrl");
        if (string.IsNullOrWhiteSpace(token))
            return Error("Missing required arg: token");
        if (ConnectSharedTokenHandler == null)
            return Error("Connect shared token handler not registered");
        var result = await ConnectSharedTokenHandler(gatewayUrl, token);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandlePendingApprovals()
    {
        if (PendingApprovalsHandler == null)
            return Error("Pending approvals handler not registered");
        var result = await PendingApprovalsHandler();
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleApproveDevicePairing(NodeInvokeRequest request)
    {
        var requestId = GetPairingRequestId(request);
        if (string.IsNullOrWhiteSpace(requestId))
            return Error("Missing required arg: requestId");
        if (ApproveDevicePairingHandler == null)
            return Error("Approve device pairing handler not registered");
        var result = await ApproveDevicePairingHandler(requestId);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleRejectDevicePairing(NodeInvokeRequest request)
    {
        var requestId = GetPairingRequestId(request);
        if (string.IsNullOrWhiteSpace(requestId))
            return Error("Missing required arg: requestId");
        if (RejectDevicePairingHandler == null)
            return Error("Reject device pairing handler not registered");
        var result = await RejectDevicePairingHandler(requestId);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleApproveNodePairing(NodeInvokeRequest request)
    {
        var requestId = GetPairingRequestId(request);
        if (string.IsNullOrWhiteSpace(requestId))
            return Error("Missing required arg: requestId");
        if (ApproveNodePairingHandler == null)
            return Error("Approve node pairing handler not registered");
        var result = await ApproveNodePairingHandler(requestId);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleRejectNodePairing(NodeInvokeRequest request)
    {
        var requestId = GetPairingRequestId(request);
        if (string.IsNullOrWhiteSpace(requestId))
            return Error("Missing required arg: requestId");
        if (RejectNodePairingHandler == null)
            return Error("Reject node pairing handler not registered");
        var result = await RejectNodePairingHandler(requestId);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleReconnect()
    {
        if (ReconnectHandler == null)
            return Error("Reconnect handler not registered");
        var result = await ReconnectHandler();
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleReconnectNode()
    {
        if (ReconnectNodeHandler == null)
            return Error("Reconnect node handler not registered");
        var result = await ReconnectNodeHandler();
        return Success(result);
    }

    private string? GetPairingRequestId(NodeInvokeRequest request) =>
        GetStringArg(request.Args, "requestId") ??
        GetStringArg(request.Args, "id");
}
