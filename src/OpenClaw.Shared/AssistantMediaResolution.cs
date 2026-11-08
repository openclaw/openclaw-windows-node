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

/// <summary>
/// Outcome of a connection-owned assistant file download. The download URL and its
/// ticket stay inside the chat bridge; renderers only see this result.
/// </summary>
public enum AssistantFileDownloadStatus
{
    /// <summary>The default browser accepted the download.</summary>
    Started,
    /// <summary>The Gateway did not return a usable download for this attachment.</summary>
    Unavailable,
    /// <summary>A download was resolved but the browser could not be launched.</summary>
    Failed,
}

internal readonly record struct GatewayConnectionLease(
    Guid ClientId,
    long Generation,
    Uri HttpBaseUri);
