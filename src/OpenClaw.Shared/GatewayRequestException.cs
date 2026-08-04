namespace OpenClaw.Shared;

/// <summary>Structured gateway RPC failure. The message is safe protocol error text, never request data.</summary>
public sealed class GatewayRequestException : InvalidOperationException
{
    public GatewayRequestException(string? code, string message)
        : base(message)
    {
        Code = code;
    }

    public string? Code { get; }
}
