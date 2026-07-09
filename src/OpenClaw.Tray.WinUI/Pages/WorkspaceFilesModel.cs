using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Pages;

/// <summary>
/// Pure projection layer for the Workspace page file rail. Adapts the current
/// read-only agent workspace browser (<c>agents.workspace.list/get</c>) plus
/// legacy/typed DTO shapes into view rows and owns the search/sort/format logic
/// so the code-behind stays a thin view.
///
/// This layer <i>consumes</i> the protocol DTOs — it does not re-parse wire JSON
/// or redefine the protocol contract (that lives in
/// <c>OpenClaw.Shared.GatewayProtocolModels</c> /
/// <c>OpenClawGatewayClient.Protocol</c>).
/// </summary>
internal static class WorkspaceFilesModel
{
    /// <summary>One row in the workspace file rail.</summary>
    internal sealed record WorkspaceFileEntry
    {
        /// <summary>Display name (leaf name, never a path).</summary>
        public required string Name { get; init; }

        /// <summary>
        /// Normalized workspace-relative path used for grouping/search/display.
        /// </summary>
        public required string RelativePath { get; init; }

        /// <summary>
        /// The original path string from the gateway DTO — used verbatim when
        /// requesting content from the gateway so normalization
        /// never diverges from what the gateway expects.
        /// </summary>
        public required string RequestPath { get; init; }

        /// <summary>Size in bytes; <c>null</c> when unknown.</summary>
        public long? Size { get; init; }

        /// <summary>False when the file is tracked but missing on disk.</summary>
        public bool Exists { get; init; } = true;

        /// <summary>True for browser/directory entries (not openable as text).</summary>
        public bool IsDirectory { get; init; }

        /// <summary>True when this row came from transcript/session relevance.</summary>
        public bool IsSessionFile { get; init; }

        /// <summary>True when selecting this file may request a gateway preview.</summary>
        public bool CanPreview { get; init; }

        /// <summary>The agent wrote/modified this file during the session.</summary>
        public bool Touched { get; init; }

        /// <summary>The agent read this file during the session.</summary>
        public bool Read { get; init; }

        /// <summary>Last-modified time (UTC) when the gateway reports it.</summary>
        public DateTimeOffset? ModifiedUtc { get; init; }
    }

    /// <summary>View state derived from a <see cref="SessionFileList"/>.</summary>
    internal sealed record WorkspaceListState
    {
        /// <summary>Absolute workspace root, or empty when not reported.</summary>
        public string WorkspacePath { get; init; } = string.Empty;

        /// <summary>
        /// False when the connected gateway does not implement the method
        /// (older gateway). Mirrors <see cref="SessionFileList.IsSupported"/>.
        /// </summary>
        public bool Supported { get; init; } = true;

        public IReadOnlyList<WorkspaceFileEntry> Entries { get; init; } =
            Array.Empty<WorkspaceFileEntry>();

        public string BrowserPath { get; init; } = string.Empty;

        public string? BrowserParentPath { get; init; }

        public string BrowserSearch { get; init; } = string.Empty;

        public bool BrowserTruncated { get; init; }
    }

    /// <summary>
    /// Project a typed <see cref="SessionFileList"/> into sorted view rows.
    /// Merges the transcript-referenced <see cref="SessionFileList.Files"/> with
    /// any optional <see cref="SessionFileList.Browser"/> entries (deduped by
    /// path, preferring the richer file entry).
    /// </summary>
    public static WorkspaceListState FromSessionFileList(SessionFileList list)
    {
        if (list == null) return new WorkspaceListState();

        if (!list.IsSupported)
            return new WorkspaceListState { Supported = false, WorkspacePath = list.Root ?? string.Empty };

        // Identity is case-sensitive: a gateway-backed workspace can contain
        // distinct paths that differ only by case.
        var byPath = new Dictionary<string, WorkspaceFileEntry>(StringComparer.Ordinal);
        var order = new List<string>();

        void Add(WorkspaceFileEntry entry)
        {
            if (!byPath.ContainsKey(entry.RelativePath))
                order.Add(entry.RelativePath);
            byPath[entry.RelativePath] = entry;
        }

        foreach (var f in list.Files ?? Array.Empty<SessionFileEntry>())
        {
            if (MapFileEntry(f) is { } entry) Add(entry);
        }

        // Browser entries (present only when a path/search was requested) fill in
        // directory rows and any files not already referenced by the transcript.
        foreach (var b in list.Browser?.Entries ?? Array.Empty<SessionFileBrowserEntry>())
        {
            if (MapBrowserEntry(b) is not { } entry) continue;
            if (byPath.ContainsKey(entry.RelativePath)) continue; // file entry wins
            Add(entry);
        }

        var entries = Sort(order.Select(p => byPath[p]).ToList());
        var browser = list.Browser;

        return new WorkspaceListState
        {
            WorkspacePath = list.Root ?? string.Empty,
            Supported = true,
            Entries = entries,
            BrowserPath = NormalizeBrowserPath(browser?.Path),
            BrowserParentPath = browser?.ParentPath is { Length: > 0 } parent
                ? NormalizeBrowserPath(parent)
                : browser?.ParentPath,
            BrowserSearch = browser?.Search ?? string.Empty,
            BrowserTruncated = browser?.Truncated ?? false,
        };
    }

    /// <summary>
    /// Project the read-only <c>agents.workspace.list</c> payload into rows.
    /// Unlike <c>sessions.files.*</c>, this is not limited to files mentioned
    /// in the transcript, so every regular file row is previewable.
    /// </summary>
    public static WorkspaceListState FromAgentWorkspaceList(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new WorkspaceListState();

        var entries = new List<WorkspaceFileEntry>();
        if (payload.TryGetProperty("entries", out var entriesEl) &&
            entriesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entryEl in entriesEl.EnumerateArray())
            {
                if (MapAgentWorkspaceEntry(entryEl) is { } entry)
                    entries.Add(entry);
            }
        }

        var path = NormalizeBrowserPath(GetString(payload, "path"));
        var parentPathRaw = GetString(payload, "parentPath");
        var totalEntries = GetLong(payload, "totalEntries");
        var offset = GetLong(payload, "offset") ?? 0;
        var truncated = totalEntries is { } total && offset + entries.Count < total;

        return new WorkspaceListState
        {
            WorkspacePath = GetString(payload, "workspace") ??
                            GetString(payload, "root") ??
                            "Agent workspace",
            Supported = true,
            Entries = Sort(entries),
            BrowserPath = path,
            BrowserParentPath = parentPathRaw is not null
                ? NormalizeBrowserPath(parentPathRaw)
                : null,
            BrowserTruncated = truncated,
        };
    }

    /// <summary>
    /// Project the legacy <c>agents.files.list</c> payload used by older
    /// gateways. It does not support path browsing or session relevance badges,
    /// but the rows remain previewable through <c>agents.files.get</c>.
    /// </summary>
    public static WorkspaceListState FromLegacyAgentFilesList(JsonElement payload)
    {
        var entries = new List<WorkspaceFileEntry>();
        if (payload.ValueKind != JsonValueKind.Object)
            return new WorkspaceListState();

        if (payload.TryGetProperty("files", out var filesEl) &&
            filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileEl in filesEl.EnumerateArray())
            {
                if (MapLegacyFileEntry(fileEl) is { } entry)
                    entries.Add(entry);
            }
        }

        return new WorkspaceListState
        {
            WorkspacePath = GetString(payload, "workspace") ?? GetString(payload, "root") ?? string.Empty,
            Supported = true,
            Entries = Sort(entries),
        };
    }

    private static WorkspaceFileEntry? MapFileEntry(SessionFileEntry f)
    {
        var requestPath = !string.IsNullOrEmpty(f.Path) ? f.Path : f.Name;
        if (string.IsNullOrEmpty(requestPath)) return null;

        var relative = NormalizePath(requestPath);
        var name = !string.IsNullOrEmpty(f.Name) ? f.Name : LeafName(relative);
        if (string.IsNullOrEmpty(name)) return null;

        return new WorkspaceFileEntry
        {
            Name = name,
            RelativePath = relative,
            RequestPath = requestPath,
            Size = f.Size is >= 0 ? f.Size : null,
            Exists = !f.Missing,
            IsDirectory = false,
            IsSessionFile = true,
            CanPreview = true,
            Touched = string.Equals(f.Kind, "modified", StringComparison.OrdinalIgnoreCase),
            Read = string.Equals(f.Kind, "read", StringComparison.OrdinalIgnoreCase),
            ModifiedUtc = ToUtc(f.UpdatedAt),
        };
    }

    private static WorkspaceFileEntry? MapBrowserEntry(SessionFileBrowserEntry b)
    {
        var requestPath = !string.IsNullOrEmpty(b.Path) ? b.Path : b.Name;
        if (string.IsNullOrEmpty(requestPath)) return null;

        var relative = NormalizePath(requestPath);
        var name = !string.IsNullOrEmpty(b.Name) ? b.Name : LeafName(relative);
        if (string.IsNullOrEmpty(name)) return null;

        // SessionKind reflects relevance: "modified" / "read" / "mixed".
        // Compare case-insensitively for consistency with MapFileEntry.
        bool touched = string.Equals(b.SessionKind, "modified", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(b.SessionKind, "mixed", StringComparison.OrdinalIgnoreCase);
        bool read = string.Equals(b.SessionKind, "read", StringComparison.OrdinalIgnoreCase);
        bool isDirectory = b.IsDirectory;
        bool isSessionFile = touched || read;

        return new WorkspaceFileEntry
        {
            Name = name,
            RelativePath = relative,
            RequestPath = requestPath,
            Size = b.Size is >= 0 ? b.Size : null,
            Exists = true,
            IsDirectory = isDirectory,
            IsSessionFile = isSessionFile,
            CanPreview = !isDirectory && isSessionFile,
            Touched = touched,
            Read = read,
            ModifiedUtc = ToUtc(b.UpdatedAt),
        };
    }

    private static WorkspaceFileEntry? MapAgentWorkspaceEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var requestPath = GetString(item, "path") ?? GetString(item, "name");
        if (string.IsNullOrEmpty(requestPath)) return null;

        var relative = NormalizePath(requestPath);
        var name = GetString(item, "name") ?? LeafName(relative);
        if (string.IsNullOrEmpty(name)) return null;

        var kind = GetString(item, "kind");
        var isDirectory = string.Equals(kind, "directory", StringComparison.OrdinalIgnoreCase);

        return new WorkspaceFileEntry
        {
            Name = name,
            RelativePath = relative,
            RequestPath = requestPath,
            Size = GetLong(item, "size"),
            Exists = true,
            IsDirectory = isDirectory,
            IsSessionFile = false,
            CanPreview = !isDirectory,
            Touched = false,
            Read = false,
            ModifiedUtc = ToUtc(GetLong(item, "updatedAtMs")),
        };
    }

    private static WorkspaceFileEntry? MapLegacyFileEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var displayPath = GetString(item, "path") ?? GetString(item, "name");
        if (string.IsNullOrEmpty(displayPath)) return null;

        var relative = NormalizePath(displayPath);
        var name = GetString(item, "name") ?? LeafName(relative);
        if (string.IsNullOrEmpty(name)) return null;

        return new WorkspaceFileEntry
        {
            Name = name,
            RelativePath = relative,
            RequestPath = name,
            Size = GetLong(item, "size"),
            Exists = GetBool(item, "exists") ?? !GetBool(item, "missing").GetValueOrDefault(),
            IsDirectory = false,
            IsSessionFile = true,
            CanPreview = true,
            ModifiedUtc = ToUtc(GetDateTime(item, "updatedAt") ?? GetDateTime(item, "modifiedAt")),
        };
    }

    /// <summary>
    /// Filter entries by a free-text query matched (case-insensitive) against
    /// the leaf name and the relative path. Whitespace-only queries return the
    /// full list unchanged. Ordering is stable (input order preserved).
    /// </summary>
    public static IReadOnlyList<WorkspaceFileEntry> Filter(
        IReadOnlyList<WorkspaceFileEntry> entries, string? query)
    {
        if (entries.Count == 0)
            return entries;

        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return entries;

        return entries
            .Where(e =>
                e.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                e.RelativePath.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Default rail ordering: directories first, then files the agent edited,
    /// then files it only read, then the rest; alphabetical by relative path
    /// within each tier. Deterministic so the UI does not jitter between
    /// refreshes.
    /// </summary>
    public static IReadOnlyList<WorkspaceFileEntry> Sort(
        IReadOnlyList<WorkspaceFileEntry> entries)
    {
        return entries
            .OrderBy(Rank)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static int Rank(WorkspaceFileEntry e) =>
            e.IsDirectory ? 0 : e.Touched ? 1 : e.Read ? 2 : 3;
    }

    /// <summary>Human-readable byte size (B / KB / MB).</summary>
    public static string FormatSize(long? bytes)
    {
        if (bytes is not { } b || b < 0) return string.Empty;
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        return $"{b / (1024.0 * 1024.0):F1} MB";
    }

    /// <summary>
    /// The parent folder portion of a relative path, or empty when the file
    /// sits at the workspace root. Used to render a subtle path caption.
    /// </summary>
    public static string DirectoryOf(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return string.Empty;
        var idx = relativePath.LastIndexOf('/');
        return idx <= 0 ? string.Empty : relativePath[..idx];
    }

    public static string NormalizeBrowserPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : NormalizePath(path.Trim()).TrimEnd('/');

    private static string LeafName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx < 0 ? normalized : normalized[(idx + 1)..];
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static DateTimeOffset? ToUtc(DateTime? value)
    {
        if (value is not { } dt) return null;
        return dt.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
            : new DateTimeOffset(dt.ToUniversalTime());
    }

    private static DateTimeOffset? ToUtc(long? unixTimeMilliseconds)
    {
        if (unixTimeMilliseconds is not { } value) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? GetBool(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static long? GetLong(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var result) &&
               result >= 0
            ? result
            : null;
    }

    private static DateTime? GetDateTime(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               DateTime.TryParse(value.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;
    }
}
