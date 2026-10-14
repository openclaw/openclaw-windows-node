using System;
using System.Collections.Generic;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>
/// Maintains the LATEST QUALIFYING assistant usage over the FULL retained timeline, so windowing the
/// display never loses it (the old formatter scanned the entries it was given; a 400-entry window could
/// hide a qualifying assistant). Semantics match ChatUsageFormatter.Format(entries, metadata): walk
/// newest -> oldest and return the first Assistant entry whose metadata yields a non-empty summary.
/// The walk STOPS at the first qualifying entry, so work is bounded by the distance to that entry, not
/// the retained length; an unchanged (entries reference, metadata revision) pair is a cached O(1) hit and
/// never rescans on streaming-only text changes that keep the same reference.
/// </summary>
/// <summary>
/// Usage attribution policy shared by the WinUI timeline and portable tests: only the GLOBAL latest
/// qualifying assistant may carry the full-retained usage summary. When the global id is unknown, the
/// window-local latest assistant is the fallback (previous behaviour).
/// </summary>
public static class ChatTimelineUsageAttribution
{
    public static string? Resolve(string? globalLatestEntryId, string? windowLatestEntryId) =>
        !string.IsNullOrEmpty(globalLatestEntryId) ? globalLatestEntryId : windowLatestEntryId;

    public static bool IsGlobalLatestAssistant(string? entryId, string? attributionEntryId) =>
        !string.IsNullOrEmpty(entryId) &&
        !string.IsNullOrEmpty(attributionEntryId) &&
        string.Equals(entryId, attributionEntryId, StringComparison.Ordinal);
}

public sealed class ChatLatestUsageProjection
{
    /// <summary>Latest qualifying usage summary plus the work actually performed for this call.</summary>
    public sealed record Result(string? Summary, string? EntryId, int ScannedEntries);

    // Topology key: identity + explicit usage revision + retained shape (count/first/last id). A
    // text-only streaming edit preserves all of these, so it is an O(1) HIT and never rescans.
    private sealed record State(
        string? SessionId,
        long UsageRevision,
        int Count,
        string? FirstId,
        string? LastId,
        Result Cached);

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves the latest qualifying usage summary for a thread over the FULL retained timeline.
    /// <paramref name="lookup"/> fetches metadata by entry id (no full-dictionary copy).
    /// </summary>
    public Result Get(
        string threadId,
        string? sessionId,
        long usageRevision,
        ChatTimelineState timeline,
        Func<string, ChatEntryMetadata?> lookup)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(lookup);

        var entries = timeline.Entries;
        var firstId = entries.Count > 0 ? entries[0].Id : null;
        var lastId = entries.Count > 0 ? entries[^1].Id : null;
        if (_states.TryGetValue(threadId, out var cached) &&
            cached.UsageRevision == usageRevision &&
            cached.Count == entries.Count &&
            string.Equals(cached.SessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(cached.FirstId, firstId, StringComparison.Ordinal) &&
            string.Equals(cached.LastId, lastId, StringComparison.Ordinal))
        {
            return cached.Cached with { ScannedEntries = 0 };   // O(1): no rescan on text-only edits
        }
        var scanned = 0;
        string? summary = null;
        string? entryId = null;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            scanned++;
            var entry = entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;
            var meta = lookup(entry.Id);
            if (meta is null)
                continue;
            var text = ChatUsageFormatter.Format(meta);
            if (string.IsNullOrWhiteSpace(text))
                continue;                 // a non-qualifying recent row must not hide a prior usable one
            summary = text;
            entryId = entry.Id;
            break;
        }

        var result = new Result(summary, entryId, scanned);
        _states[threadId] = new State(sessionId, usageRevision, entries.Count, firstId, lastId, result);
        return result;
    }

    /// <summary>Drops the cached projection for a thread (reset / identity change).</summary>
    public void Forget(string threadId) => _states.Remove(threadId);

    public void Clear() => _states.Clear();
}
