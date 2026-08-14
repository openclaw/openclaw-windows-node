using OpenClaw.Connection;
using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClawTray.Presentation;

internal enum PermissionsCapabilityKey
{
    SystemRun,
    BrowserProxy,
    Camera,
    Canvas,
    Screen,
    Location,
    TextToSpeech,
    SpeechToText,
}

internal sealed record PermissionsCapabilityState(
    PermissionsCapabilityKey Key,
    bool IsOn,
    bool IsInteractive);

internal enum PermissionsNodeStatusKind
{
    Disabled,
    McpOnly,
    McpError,
    Active,
    Starting,
    NotConnected,
}

internal enum PermissionsMcpTokenState
{
    None,
    Pending,
    Ready,
}

internal enum PermissionsVoiceSetupRequirement
{
    None,
    SpeechModel,
    VoiceSetup,
    SpeechModelAndVoiceSetup,
}

internal enum PermissionsExecApprovalsStatus
{
    None,
    Saved,
    SaveFailed,
    ExternalInvalid,
}

internal enum PermissionsGatewayAllowlistState
{
    NoConfig,
    ParseFailed,
    NoCommands,
    Commands,
}

internal sealed record PermissionsExecApprovalRule(
    Guid? Id,
    string Pattern,
    string? ArgPattern,
    double? LastUsedAt,
    string? LastResolvedPath);

internal sealed record PermissionsRuntimeSourceSnapshot(
    GatewayConnectionSnapshot ConnectionSnapshot,
    string? McpStartupError,
    string McpEndpoint,
    bool IsMcpTokenReady,
    int McpServedCapabilityCount,
    IReadOnlyList<string> LocalNodeCapabilities,
    IReadOnlyList<string> GatewayAllowCommands,
    PermissionsGatewayAllowlistState GatewayAllowlistState,
    PermissionsVoiceSetupRequirement VoiceSetupRequirement);

internal sealed class PermissionsRuntimeSourceChangedEventArgs : EventArgs
{
    public PermissionsRuntimeSourceChangedEventArgs(PermissionsRuntimeSourceSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public PermissionsRuntimeSourceSnapshot Snapshot { get; }
}

internal interface IPermissionsPageRuntimeHost
{
    event EventHandler? Changed;

    GatewayConnectionSnapshot ConnectionSnapshot { get; }
    GatewayNodeInfo[] Nodes { get; }
    string? LocalNodeDeviceId { get; }
    JsonElement? GatewayConfig { get; }
    string? McpStartupError { get; }
    string McpEndpoint { get; }
    bool IsMcpTokenReady { get; }
    int McpServedCapabilityCount { get; }
    PermissionsVoiceSetupRequirement VoiceSetupRequirement { get; }
}

internal interface IPermissionsPageRuntimeSource
{
    event EventHandler<PermissionsRuntimeSourceChangedEventArgs>? Changed;

    PermissionsRuntimeSourceSnapshot Current { get; }
}
