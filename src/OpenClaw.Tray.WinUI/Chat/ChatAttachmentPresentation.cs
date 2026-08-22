using OpenClaw.Shared;

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
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        return attachment.CanAccessPreviewCache &&
            ChatImagePreviewCache.TryGet(attachment.PreviewCacheKey!, out bytes);
    }

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

internal static class ChatImagePreviewCache
{
    internal const int MaximumEntries = 32;
    internal const long MaximumTotalBytes = 64L * 1024 * 1024;
    internal const int MaximumPreviewBytes = (int)ChatAttachment.MaxSizeBytes;

    private static readonly object s_gate = new();
    private static readonly Dictionary<string, byte[]> s_entries = new(StringComparer.Ordinal);
    private static readonly Queue<string> s_insertionOrder = new();
    private static long s_totalBytes;

    internal static int Count
    {
        get
        {
            lock (s_gate)
                return s_entries.Count;
        }
    }

    internal static long TotalBytes
    {
        get
        {
            lock (s_gate)
                return s_totalBytes;
        }
    }

    internal static bool TryStoreBase64(string key, string encoded)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            !TryDecodeBoundedBase64(encoded, MaximumPreviewBytes, out var bytes))
        {
            return false;
        }

        lock (s_gate)
        {
            if (s_entries.TryGetValue(key, out var previous))
            {
                s_totalBytes -= previous.Length;
            }
            else
            {
                s_insertionOrder.Enqueue(key);
            }

            s_entries[key] = bytes;
            s_totalBytes += bytes.Length;
            TrimLocked();
            return s_entries.ContainsKey(key);
        }
    }

    internal static bool TryGet(string key, out byte[] bytes)
    {
        lock (s_gate)
            return s_entries.TryGetValue(key, out bytes!);
    }

    internal static bool Contains(string key)
    {
        lock (s_gate)
            return s_entries.ContainsKey(key);
    }

    internal static bool TryDecodeBoundedBase64(
        string? encoded,
        int maximumBytes,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(encoded) || maximumBytes <= 0)
            return false;

        var maximumEncodedLength = ((maximumBytes + 2L) / 3L) * 4L;
        if (encoded.Length > maximumEncodedLength)
            return false;

        try
        {
            bytes = Convert.FromBase64String(encoded);
            if (bytes.Length <= maximumBytes)
                return true;
        }
        catch (FormatException)
        {
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    internal static void Clear()
    {
        lock (s_gate)
        {
            s_entries.Clear();
            s_insertionOrder.Clear();
            s_totalBytes = 0;
        }
    }

    private static void TrimLocked()
    {
        while ((s_entries.Count > MaximumEntries ||
                s_totalBytes > MaximumTotalBytes) &&
               s_insertionOrder.TryDequeue(out var oldestKey))
        {
            if (s_entries.Remove(oldestKey, out var removed))
                s_totalBytes -= removed.Length;
        }
    }
}
