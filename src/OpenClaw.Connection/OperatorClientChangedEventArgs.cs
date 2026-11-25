using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Event args for when the operator client instance changes (connect, disconnect, reconnect, gateway switch).
/// </summary>
public sealed class OperatorClientChangedEventArgs : EventArgs
{
    /// <summary>Registry target captured with NewClient under the manager lifecycle gate; null for retirement.</summary>
    public string? GatewayRecordId { get; init; }
    public IOperatorGatewayClient? OldClient { get; init; }
    public IOperatorGatewayClient? NewClient { get; init; }
}
