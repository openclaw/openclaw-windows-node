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
        "app.connection.applySetupCode",
        "app.connection.connectSharedToken",
    ];

    public override IReadOnlyList<string> Commands => s_commands;

    public Func<string, Task<object?>>? ApplySetupCodeHandler;
    public Func<string, string, Task<object?>>? ConnectSharedTokenHandler;

    public AppConnectionCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "app.connection.applySetupCode" => await HandleApplySetupCode(request),
            "app.connection.connectSharedToken" => await HandleConnectSharedToken(request),
            _ => Error($"Unknown command: {request.Command}")
        };
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
}
