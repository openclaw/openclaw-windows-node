// Identity and outcome of a read-only workspace file opened from a chat tool row.
// Usage: panels.OpenFile(new ChatWorkspaceFileRequest(sessionKey, path), revision);
//        var result = await provider.LoadWorkspaceFileAsync(request, ct);
namespace OpenClawTray.Chat;

/// <summary>A file named by a tool row, scoped to the chat session whose workspace the Gateway resolves.</summary>
internal sealed record ChatWorkspaceFileRequest(string SessionKey, string Path)
{
    /// <summary>The last path segment, for the tab title.</summary>
    internal string Name => FileName(Path);

    internal static string FileName(string path) =>
        path.Replace('\\', '/').TrimEnd('/') is var normalized && normalized.LastIndexOf('/') is >= 0 and var slash
            ? normalized[(slash + 1)..] : path;
}

internal enum ChatWorkspaceFileStatus
{
    Loaded,
    Missing,
    /// <summary>The file exists but is an image, binary or too large to show as text.</summary>
    NotPreviewable,
    /// <summary>The Gateway refused or failed the read, or it timed out; a retry may succeed.</summary>
    Unavailable,
    /// <summary>The connected Gateway does not implement <c>sessions.files.get</c>.</summary>
    Unsupported,
    /// <summary>The connection or session changed before the read finished; its result was discarded.</summary>
    Stale,
}

internal sealed record ChatWorkspaceFileResult(ChatWorkspaceFileStatus Status, string? Content = null);
