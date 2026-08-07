namespace OpenClawTray.Chat;

public enum ChatAttachmentOrigin
{
    Local,
    GatewayReference,
}

/// <summary>
/// Safe, renderer-facing attachment metadata. Preview cache access is allowed
/// only for local attachments that carry an opaque cache key.
/// </summary>
public sealed record ChatAttachmentPresentation(
    ChatAttachmentOrigin Origin,
    string DisplayFileName,
    string MimeType,
    bool IsImage,
    string? PreviewCacheKey = null)
{
    public bool CanAccessPreviewCache =>
        Origin == ChatAttachmentOrigin.Local &&
        !string.IsNullOrWhiteSpace(PreviewCacheKey);
}

internal static class ChatAttachmentPreviewResolver
{
    internal static bool TryGetBytes(
        ChatAttachmentPresentation attachment,
        IReadOnlyDictionary<string, byte[]> previewCache,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        return attachment.CanAccessPreviewCache &&
            previewCache.TryGetValue(attachment.PreviewCacheKey!, out bytes!);
    }
}
