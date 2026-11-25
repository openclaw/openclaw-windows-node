namespace OpenClaw.Shared;

/// <summary>
/// Indicates that the Gateway transport confirmed a request was not written.
/// </summary>
public sealed class GatewayRequestNotSentException : InvalidOperationException
{
    public GatewayRequestNotSentException(string message)
        : base(message)
    {
    }
}

public sealed class GatewayConnectionLostException : OperationCanceledException
{
    public GatewayConnectionLostException(
        int? closeStatusCode,
        string? closeStatusDescription)
        : base(BuildMessage(closeStatusCode))
    {
        CloseStatusCode = closeStatusCode;
        CloseStatusDescription = closeStatusDescription;
    }

    public int? CloseStatusCode { get; }

    public string? CloseStatusDescription { get; }

    private static string BuildMessage(int? closeStatusCode) =>
        closeStatusCode is null
            ? "Gateway connection lost while waiting for wizard response"
            : $"Gateway connection lost while waiting for wizard response (WebSocket close {closeStatusCode})";
}
