namespace OpenClaw.Shared;

public enum AssistantMediaResolutionStatus
{
    Ready,
    Preparing,
    Unavailable,
}

public sealed record AssistantMediaResolutionResult(
    AssistantMediaResolutionStatus Status,
    byte[]? Data = null,
    string? MimeType = null)
{
    public static AssistantMediaResolutionResult Preparing { get; } =
        new(AssistantMediaResolutionStatus.Preparing);

    public static AssistantMediaResolutionResult Unavailable { get; } =
        new(AssistantMediaResolutionStatus.Unavailable);
}

internal readonly record struct GatewayConnectionLease(
    Guid ClientId,
    long Generation,
    Uri HttpBaseUri);
