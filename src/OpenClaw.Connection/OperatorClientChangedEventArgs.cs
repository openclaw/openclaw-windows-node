using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Event args for when the operator client instance changes (connect, disconnect, reconnect, gateway switch).
/// </summary>
public sealed class OperatorClientChangedEventArgs : EventArgs
{
    public IOperatorGatewayClient? OldClient { get; init; }
    public IOperatorGatewayClient? NewClient { get; init; }
}
