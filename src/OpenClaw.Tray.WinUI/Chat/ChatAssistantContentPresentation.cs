using OpenClaw.Shared;

namespace OpenClawTray.Chat;

public sealed record ChatAssistantContentPresentation(
    IReadOnlyList<ChatAssistantMediaPresentation> Media);

public sealed record ChatAssistantMediaPresentation(
    ChatMediaContentKind Kind,
    string DisplayName,
    string? MimeType,
    string? Alt,
    ChatMediaContentInfo Reference);

internal sealed record ChatAssistantMediaRenderPlan(
    IReadOnlyList<ChatAssistantMediaPresentation> Media,
    int OmittedImages);

/// <summary>
/// Converts protocol media into renderer-safe presentation metadata. Transport
/// references remain opaque and are never included in display strings.
/// </summary>
internal static class ChatAssistantContentProjector
{
    internal const int MaximumInlineImages = 4;

    public static ChatAssistantContentPresentation? Project(
        IEnumerable<ChatMessageContentPartInfo>? contentParts)
    {
        if (contentParts is null)
            return null;

        var media = contentParts
            .Where(static part => part.Kind == ChatMessageContentPartKind.Media)
            .Select(static part => part.Media)
            .Where(static item => item is not null)
            .Select(static item => new ChatAssistantMediaPresentation(
                item!.Kind,
                SafeDisplayName(item),
                item.MimeType,
                item.Alt,
                item))
            .ToArray();
        return media.Length == 0 ? null : new ChatAssistantContentPresentation(media);
    }

    public static ChatAssistantMediaRenderPlan BuildRenderPlan(
        IReadOnlyList<ChatAssistantMediaPresentation> media)
    {
        var renderedImages = 0;
        var planned = new List<ChatAssistantMediaPresentation>(media.Count);
        foreach (var item in media)
        {
            if (item.Kind == ChatMediaContentKind.Image
                && ++renderedImages > MaximumInlineImages)
            {
                continue;
            }
            planned.Add(item);
        }
        return new ChatAssistantMediaRenderPlan(
            planned,
            Math.Max(0, renderedImages - MaximumInlineImages));
    }

    public static ChatAssistantContentPresentation MergeLiveUpdate(
        ChatAssistantContentPresentation? existing,
        ChatAssistantContentPresentation incoming)
    {
        if (existing is null || existing.Media.Count != incoming.Media.Count)
            return incoming;

        var merged = incoming.Media.ToArray();
        for (var index = 0; index < merged.Length; index++)
        {
            var previous = existing.Media[index];
            var next = incoming.Media[index];
            if (previous.Kind == next.Kind
                && previous.Reference.Source == ChatMediaContentSource.LegacyDirective
                && next.Reference.Source == ChatMediaContentSource.Structured
                && (string.IsNullOrWhiteSpace(next.Reference.FileName)
                    || string.Equals(
                        previous.Reference.FileName,
                        next.Reference.FileName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                merged[index] = previous;
            }
        }
        return new ChatAssistantContentPresentation(merged);
    }

    private static string SafeDisplayName(ChatMediaContentInfo media)
    {
        if (!string.IsNullOrWhiteSpace(media.FileName))
        {
            var normalized = media.FileName.Replace('\\', '/');
            var leaf = normalized[(normalized.LastIndexOf('/') + 1)..].Trim();
            if (leaf.Length > 0)
                return leaf;
        }
        return string.Empty;
    }
}
