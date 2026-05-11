using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Event args for when the operator client instance changes (connect, disconnect, reconnect, gateway switch).
/// </summary>
public sealed class OperatorClientChangedEventArgs : EventArgs
{
    public OpenClawGatewayClient? OldClient { get; init; }
    public OpenClawGatewayClient? NewClient { get; init; }
}
