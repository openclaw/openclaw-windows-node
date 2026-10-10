using OpenClaw.Shared;
using OpenClaw.Chat;
using System.Collections.Immutable;

namespace OpenClawTray.Chat;

/// <summary>
/// State-owned bounded chat.history window carried from the last ACCEPTED commit: echoed session
/// identity, whether older pages remain, the next older offset, and the source total. Generation
/// validity comes from the commit token stored alongside it; the window is cleared on reset,
/// reconnect, replacement and dispose so a stale window can never be retrieved.
/// </summary>
internal readonly record struct ChatHistoryWindowState(
    string SessionKey,
    string? SessionId,
    bool HasMore,
    int? NextOffset,
    int? Total,
    int? ResponseOffset,
    // SOURCE-GROUNDED completeness carried with the accepted window: lets the actual caller/UI distinguish a
    // snapshot-explicit-complete page, an ordinary page (not loss), and an ACTUAL reported omission.
    ChatHistoryPageCompleteness Completeness = ChatHistoryPageCompleteness.UnknownLegacy,
    int? OmittedCount = null,
    long? NormalizedBytes = null);

/// <summary>
/// Atomic capture of the accepted window together with its commit token and revision. A commit that
/// carries a lease is accepted only while the currently stored window is still exactly this one, so a
/// delayed older-page response can never regress a newer accepted cursor in the same generation.
/// </summary>
internal readonly record struct ChatHistoryWindowLease(
    ChatHistoryWindowState Window,
    ChatHistoryCommitToken Token,
    long Revision);

/// <summary>
/// Owns session identity, transcript freshness/revisions, and the single
/// connection-generation activation/commit-token state. The root supplies
/// reset generations and serializes every operation under its sole lock.
/// </summary>
internal sealed class ChatHistoryState
{
    private readonly Dictionary<string, string> _sessionIds = new();
    private readonly HashSet<string> _loadedThreads = new();
    private readonly Dictionary<string, long> _revisions = new();
    private readonly Dictionary<string, string> _resetClearedSessionIds = new();
    private readonly Dictionary<string, long> _replacementGenerations = new();
    private readonly Dictionary<string, (ChatHistoryWindowState Window, ChatHistoryCommitToken Token, long Revision)> _historyWindows =
        new(StringComparer.Ordinal);

    // ACCEPTED RETAINED TRANSCRIPT identity, deliberately separate from the catalog/send address. It
    // survives paging-window invalidation (disconnect/reconnect) while the retained timeline survives,
    // and changes only on an accepted history commit or an explicit timeline reset/replacement.
    private readonly Dictionary<string, string> _acceptedTranscriptIds = new(StringComparer.Ordinal);

    private long _connectionGeneration;
    private bool _generationReady = true;
    private TaskCompletionSource _generationActivation =
        CompletedActivation();

    internal long ConnectionGeneration => _connectionGeneration;

    /// <summary>
    /// The accepted retained-transcript identity for a thread (survives window invalidation), or null
    /// when no accepted commit has established one.
    /// </summary>
    /// <summary>Coherent copy of the accepted session UUIDs, captured under the SAME lock as the timeline.</summary>
    internal IReadOnlyDictionary<string, string> SnapshotAcceptedTranscriptIds() =>
        new Dictionary<string, string>(_acceptedTranscriptIds, StringComparer.Ordinal);

    internal string? GetAcceptedTranscriptId(string threadId) =>
        _acceptedTranscriptIds.TryGetValue(threadId, out var id) ? id : null;

    internal string? ResolveSessionId(string threadId) =>
        _sessionIds.TryGetValue(threadId, out var sessionId)
            ? sessionId
            : null;

    internal IReadOnlyDictionary<string, long> SnapshotRevisions() =>
        new Dictionary<string, long>(_revisions);

    internal ChatHistoryCommitToken CreateCommitToken(
        string threadId,
        long resetGeneration) =>
        new(
            threadId,
            _connectionGeneration,
            resetGeneration,
            GetReplacementGeneration(threadId));

    internal ChatHistoryCommitToken BeginReplacement(
        string threadId,
        long resetGeneration)
    {
        _replacementGenerations[threadId] =
            GetReplacementGeneration(threadId) + 1;
        _loadedThreads.Remove(threadId);
        _historyWindows.Remove(threadId);
        _acceptedTranscriptIds.Remove(threadId);   // explicit replacement drops the transcript identity
        return CreateCommitToken(threadId, resetGeneration);
    }

    internal bool TryBegin(
        string threadId,
        bool force,
        ChatHistoryCommitToken? expectedToken,
        long resetGeneration,
        ConnectionStatus status,
        bool disposed,
        out ChatHistoryCommitToken token,
        out Task? generationActivation)
    {
        token = CreateCommitToken(threadId, resetGeneration);
        generationActivation = null;
        if (disposed || !force && _loadedThreads.Contains(threadId))
            return false;

        if (!_generationReady)
        {
            generationActivation = _generationActivation.Task;
            return false;
        }

        return expectedToken is not { } expected ||
               expected.ConnectionGeneration == _connectionGeneration &&
               expected.ResetGeneration == resetGeneration &&
               expected.ReplacementGeneration ==
                   GetReplacementGeneration(threadId) &&
               (force || status == ConnectionStatus.Connected);
    }

    internal bool IsCurrent(
        ChatHistoryCommitToken token,
        long resetGeneration,
        bool disposed) =>
        !disposed &&
        token.ConnectionGeneration == _connectionGeneration &&
        token.ResetGeneration == resetGeneration &&
        token.ReplacementGeneration ==
            GetReplacementGeneration(token.ThreadId);

    internal bool CanRetry(
        ChatHistoryCommitToken token,
        long resetGeneration,
        ConnectionStatus status,
        bool authoritative,
        bool disposed) =>
        IsCurrent(token, resetGeneration, disposed) &&
        status == ConnectionStatus.Connected &&
        (authoritative || !_loadedThreads.Contains(token.ThreadId));

    internal void MarkCommitted(
        ChatHistoryCommitToken token,
        string? sessionId,
        ChatHistoryWindowState? window = null)
    {
        if (!string.IsNullOrEmpty(sessionId))
            _sessionIds[token.ThreadId] = sessionId;
        _revisions[token.ThreadId] =
            (_revisions.TryGetValue(token.ThreadId, out var revision)
                ? revision
                : 0) + 1;
        _loadedThreads.Add(token.ThreadId);
        if (!string.IsNullOrEmpty(sessionId))
            _acceptedTranscriptIds[token.ThreadId] = sessionId;
        if (window is { } acceptedWindow)
            _historyWindows[token.ThreadId] = (
                acceptedWindow,
                token,
                _revisions[token.ThreadId]);
    }

    /// <summary>
    /// The window accepted by the most recent current commit for a thread, or null when there is no
    /// valid window (never committed, superseded by reset/reconnect/replacement, or disposed).
    /// </summary>
    internal ChatHistoryWindowState? GetWindow(
        string threadId,
        long resetGeneration,
        bool disposed) =>
        CaptureWindow(threadId, resetGeneration, disposed)?.Window;

    /// <summary>
    /// Atomically captures the accepted window with its commit token and revision, or null when there
    /// is no valid window (never committed, superseded, or disposed).
    /// </summary>
    internal ChatHistoryWindowLease? CaptureWindow(
        string threadId,
        long resetGeneration,
        bool disposed) =>
        !disposed &&
        _historyWindows.TryGetValue(threadId, out var stored) &&
        IsCurrent(stored.Token, resetGeneration, disposed)
            ? new ChatHistoryWindowLease(stored.Window, stored.Token, stored.Revision)
            : null;

    /// <summary>
    /// Advances the accepted window for an OLDER page without touching session identity (unlike
    /// <see cref="MarkCommitted"/>, which owns identity). Bumps the revision so consumers republish.
    /// </summary>
    internal void MarkOlderCommitted(
        ChatHistoryCommitToken token,
        ChatHistoryWindowState window)
    {
        _revisions[token.ThreadId] =
            (_revisions.TryGetValue(token.ThreadId, out var revision)
                ? revision
                : 0) + 1;
        _historyWindows[token.ThreadId] = (window, token, _revisions[token.ThreadId]);
    }

    internal long AdvanceConnectionGeneration(bool clearLoaded)
    {
        _connectionGeneration++;
        _generationActivation.TrySetResult();
        _generationReady = false;
        _generationActivation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (clearLoaded)
            _loadedThreads.Clear();
        _historyWindows.Clear();
        return _connectionGeneration;
    }

    internal void ActivateConnectionGeneration(
        long generation,
        bool disposed)
    {
        if (disposed ||
            generation != _connectionGeneration ||
            _generationReady)
        {
            return;
        }
        _generationReady = true;
        _generationActivation.TrySetResult();
    }

    internal string? ClearSessionForReset(string threadId)
    {
        var oldSessionId = ResolveSessionId(threadId);
        if (!string.IsNullOrEmpty(oldSessionId))
            _resetClearedSessionIds[threadId] = oldSessionId;
        else
            _resetClearedSessionIds.Remove(threadId);
        _sessionIds.Remove(threadId);
        _loadedThreads.Add(threadId);
        _historyWindows.Remove(threadId);
        _acceptedTranscriptIds.Remove(threadId);   // explicit timeline reset drops the transcript identity
        return oldSessionId;
    }

    internal void SeedSessionIds(IEnumerable<SessionInfo> sessions)
    {
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Key) ||
                string.IsNullOrWhiteSpace(session.SessionId))
            {
                continue;
            }

            if (_resetClearedSessionIds.TryGetValue(
                    session.Key,
                    out var clearedSessionId) &&
                string.Equals(
                    clearedSessionId,
                    session.SessionId,
                    StringComparison.Ordinal))
            {
                continue;
            }
            _sessionIds[session.Key] = session.SessionId;
            _resetClearedSessionIds.Remove(session.Key);
        }
    }

    private long GetReplacementGeneration(string threadId) =>
        _replacementGenerations.TryGetValue(threadId, out var generation)
            ? generation
            : 0;

    internal static (
        ChatTimelineState Timeline,
        Dictionary<string, ChatEntryMetadata> Metadata)
        MergeWithLiveEntries(
            ChatHistoryRebuildPlan plan,
            ChatTimelineState prior,
            IReadOnlyDictionary<string, ChatEntryMetadata> priorMetadata,
            DateTimeOffset requestStartedAt,
            bool authoritative)
    {
        var rebuilt = plan.Timeline;
        var rebuiltMetadata = new Dictionary<string, ChatEntryMetadata>(
            plan.Metadata,
            StringComparer.Ordinal);
        if (prior.Entries.Count == 0)
            return (rebuilt, rebuiltMetadata);

        static string ContentKey(ChatTimelineItemKind kind, string text) =>
            $"{kind}|{text}";
        static string SequenceKey(ChatTimelineItemKind kind, int sequence) =>
            $"{kind}|{sequence}";

        var contentTimestamps = new Dictionary<string, List<long>>(
            StringComparer.Ordinal);
        var messageIds = new HashSet<(string RawId, string? DisplayItemId)>();
        var sequenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in rebuilt.Entries)
        {
            rebuiltMetadata.TryGetValue(entry.Id, out var metadata);
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId))
                messageIds.Add((metadata.GatewayMessageId, metadata.GatewayDisplayItemId));
            if (metadata?.OpenClawSeq is { } sequence)
                IncrementCount(sequenceCounts, SequenceKey(entry.Kind, sequence));
            if (metadata?.Timestamp is { } timestamp && timestamp != default)
            {
                var key = ContentKey(entry.Kind, entry.Text);
                if (!contentTimestamps.TryGetValue(key, out var timestamps))
                {
                    timestamps = [];
                    contentTimestamps[key] = timestamps;
                }
                timestamps.Add(timestamp.ToUnixTimeSeconds());
            }
        }

        var existingIds = rebuilt.Entries
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        var maxSuffix = rebuilt.Entries
            .Select(entry =>
                entry.Id.Length > 1 &&
                entry.Id[0] == 'e' &&
                int.TryParse(entry.Id.AsSpan(1), out var suffix)
                    ? suffix
                    : 0)
            .DefaultIfEmpty()
            .Max();
        var nextId = Math.Max(rebuilt.NextId, maxSuffix + 1);
        var entries = rebuilt.Entries.ToBuilder();
        foreach (var entry in prior.Entries)
        {
            priorMetadata.TryGetValue(entry.Id, out var metadata);
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId) &&
                messageIds.Contains((metadata.GatewayMessageId, metadata.GatewayDisplayItemId)))
            {
                ConsumeAnyTimestamp(
                    contentTimestamps,
                    ContentKey(entry.Kind, entry.Text));
                continue;
            }
            if (string.IsNullOrEmpty(metadata?.GatewayMessageId) &&
                metadata?.OpenClawSeq is { } sequence &&
                TryConsumeCount(
                    sequenceCounts,
                    SequenceKey(entry.Kind, sequence)))
            {
                ConsumeAnyTimestamp(
                    contentTimestamps,
                    ContentKey(entry.Kind, entry.Text));
                continue;
            }
            if (authoritative &&
                !ShouldPreserveLiveEntryDuringAuthoritativeReload(
                    metadata,
                    plan.MaxHistorySequence,
                    requestStartedAt))
            {
                continue;
            }
            if (string.IsNullOrEmpty(metadata?.GatewayMessageId) &&
                metadata?.Timestamp is { } timestamp &&
                timestamp != default &&
                contentTimestamps.TryGetValue(
                    ContentKey(entry.Kind, entry.Text),
                    out var rebuiltTimes))
            {
                var priorSeconds = timestamp.ToUnixTimeSeconds();
                var match = rebuiltTimes.FindIndex(value =>
                    Math.Abs(value - priorSeconds) <= 2);
                if (match >= 0)
                {
                    rebuiltTimes.RemoveAt(match);
                    continue;
                }
            }

            var entryToAdd = entry;
            if (existingIds.Contains(entry.Id))
                entryToAdd = entry with { Id = $"e{nextId++}" };
            else if (entry.Id.Length > 1 &&
                     entry.Id[0] == 'e' &&
                     int.TryParse(entry.Id.AsSpan(1), out var suffix) &&
                     suffix >= nextId)
                nextId = suffix + 1;
            entries.Add(entryToAdd);
            existingIds.Add(entryToAdd.Id);
            if (metadata?.Timestamp is { } addedTimestamp &&
                addedTimestamp != default)
            {
                var key = ContentKey(entryToAdd.Kind, entryToAdd.Text);
                if (!contentTimestamps.TryGetValue(key, out var timestamps))
                {
                    timestamps = [];
                    contentTimestamps[key] = timestamps;
                }
                timestamps.Add(addedTimestamp.ToUnixTimeSeconds());
            }
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId))
                messageIds.Add((metadata.GatewayMessageId, metadata.GatewayDisplayItemId));
            if (metadata?.OpenClawSeq is { } addedSequence)
            {
                IncrementCount(
                    sequenceCounts,
                    SequenceKey(entryToAdd.Kind, addedSequence));
            }
            if (metadata is not null)
                rebuiltMetadata[entryToAdd.Id] = metadata;
        }

        var merged = rebuilt with
            {
                Entries = entries.ToImmutable(),
                NextId = nextId,
                TurnActive = prior.TurnActive,
                PendingToolPresentations = prior.PendingToolPresentations,
                PendingToolOutcomes = prior.PendingToolOutcomes,
                TerminalToolCorrelations =
                    prior.TerminalToolCorrelations,
                NextToolOutcomeSequence =
                    prior.NextToolOutcomeSequence,
                NextToolCorrelationSequence =
                    prior.NextToolCorrelationSequence,
                ToolLegacyTurn = prior.ToolLegacyTurn,
            };
        merged = ChatTimelineReducer.RebuildActiveToolTracking(merged);
        return (merged, rebuiltMetadata);
    }

    /// <summary>
    /// Prepends an OLDER bounded page to the held timeline: entries already present (replay overlap) are
    /// dropped, surviving older entries are renumbered and placed before the held newer entries, and
    /// metadata is unioned. Session identity is not changed here.
    /// </summary>
    internal static (
        ChatTimelineState Timeline,
        Dictionary<string, ChatEntryMetadata> Metadata)
        MergeOlderPage(
            ChatHistoryRebuildPlan older,
            ChatTimelineState existing,
            IReadOnlyDictionary<string, ChatEntryMetadata> existingMetadata,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();   // stage entry (before any real work)
        // BOUNDED cancellable copies: each stage checks the token every 64 real entries so a large
        // full-history metadata copy cannot run to completion after cancellation.
        var olderMetadata = CopyMetadataCancellable(older.Metadata, cancellationToken);
        var mergedMetadata = CopyMetadataCancellable(existingMetadata, cancellationToken);

        static string OlderContentKey(ChatTimelineItemKind kind, string text) => $"{kind}|{text}";
        static string OlderSequenceKey(ChatTimelineItemKind kind, int sequence) => $"{kind}|{sequence}";

        var messageIds = new HashSet<(string RawId, string? DisplayItemId)>();
        var sequenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var contentTimestamps = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var maxSuffix = 0;
        var visitedExisting = 0;
        foreach (var entry in existing.Entries)
        {
            // Bounded cancellation responsiveness INSIDE a potentially long full-history scan.
            if ((++visitedExisting & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            existingMetadata.TryGetValue(entry.Id, out var metadata);
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId))
                messageIds.Add((metadata.GatewayMessageId, metadata.GatewayDisplayItemId));
            if (metadata?.OpenClawSeq is { } sequence)
                IncrementCount(sequenceCounts, OlderSequenceKey(entry.Kind, sequence));
            // Content+timestamp fallback is registered ONLY for existing entries with no source
            // identity, so a fuzzy match can never erase a distinct known-ID/sequence row.
            var existingHasIdentity =
                !string.IsNullOrEmpty(metadata?.GatewayMessageId) || metadata?.OpenClawSeq is not null;
            if (!existingHasIdentity && metadata?.Timestamp is { } timestamp && timestamp != default)
            {
                var key = OlderContentKey(entry.Kind, entry.Text);
                if (!contentTimestamps.TryGetValue(key, out var times))
                {
                    times = [];
                    contentTimestamps[key] = times;
                }
                times.Add(timestamp.ToUnixTimeSeconds());
            }
            if (entry.Id.Length > 1 && entry.Id[0] == 'e' &&
                int.TryParse(entry.Id.AsSpan(1), out var suffix) && suffix > maxSuffix)
                maxSuffix = suffix;
        }

        var nextId = Math.Max(existing.NextId, maxSuffix + 1);
        var builder = existing.Entries.ToBuilder();
        var inserted = 0;
        var visitedOlder = 0;
        foreach (var entry in older.Timeline.Entries)
        {
            if ((++visitedOlder & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            olderMetadata.TryGetValue(entry.Id, out var metadata);
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId) &&
                messageIds.Contains((metadata.GatewayMessageId, metadata.GatewayDisplayItemId)))
            {
                ConsumeAnyTimestamp(contentTimestamps, OlderContentKey(entry.Kind, entry.Text));
                continue;
            }
            // IDENTITY PRECEDENCE: the seq fallback applies ONLY to an entry with NO known source id. A distinct
            // KNOWN GatewayMessageId must SURVIVE even when it shares kind/seq/content with an inserted sibling.
            var olderHasKnownId = !string.IsNullOrEmpty(metadata?.GatewayMessageId);
            if (!olderHasKnownId &&
                metadata?.OpenClawSeq is { } sequence &&
                TryConsumeCount(sequenceCounts, OlderSequenceKey(entry.Kind, sequence)))
            {
                ConsumeAnyTimestamp(contentTimestamps, OlderContentKey(entry.Kind, entry.Text));
                continue;
            }
            var olderHasIdentity =
                !string.IsNullOrEmpty(metadata?.GatewayMessageId) || metadata?.OpenClawSeq is not null;
            if (!olderHasIdentity &&
                metadata?.Timestamp is { } timestamp && timestamp != default &&
                contentTimestamps.TryGetValue(OlderContentKey(entry.Kind, entry.Text), out var times))
            {
                var seconds = timestamp.ToUnixTimeSeconds();
                var match = times.FindIndex(value => Math.Abs(value - seconds) <= 2);
                if (match >= 0)
                {
                    times.RemoveAt(match);
                    continue;
                }
            }

            var added = entry with { Id = $"e{nextId++}" };
            builder.Insert(inserted++, added);
            if (metadata is not null)
                mergedMetadata[added.Id] = metadata;
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId))
                messageIds.Add((metadata.GatewayMessageId, metadata.GatewayDisplayItemId));
            if (metadata?.OpenClawSeq is { } addedSequence)
                IncrementCount(sequenceCounts, OlderSequenceKey(added.Kind, addedSequence));
        }

        cancellationToken.ThrowIfCancellationRequested();   // stage boundary before the immutable build
        var timeline = existing with { Entries = builder.ToImmutable(), NextId = nextId };
        // Bounded cancellation INSIDE the final active-tool-tracking rebuild (entry/exit + every 64).
        timeline = ChatTimelineReducer.RebuildActiveToolTracking(timeline, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();   // stage exit before returning the plan
        return (timeline, mergedMetadata);
    }

    /// <summary>
    /// Bounded, cancellable copy of a metadata map: checks the token every 64 real entries so a large
    /// full-history copy cannot run to completion after cancellation. Preserves the exact entry sequence.
    /// </summary>
    internal static Dictionary<string, ChatEntryMetadata> CopyMetadataCancellable(
        IEnumerable<KeyValuePair<string, ChatEntryMetadata>> source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();   // stage entry
        var capacity = source is IReadOnlyCollection<KeyValuePair<string, ChatEntryMetadata>> counted
            ? counted.Count
            : 0;
        var copy = new Dictionary<string, ChatEntryMetadata>(capacity, StringComparer.Ordinal);
        var visited = 0;
        foreach (var pair in source)
        {
            if ((++visited & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            copy[pair.Key] = pair.Value;
        }
        // Stage exit: a copy shorter than the 64-entry checkpoint interval still observes a cancellation
        // that landed during enumeration instead of returning normally.
        cancellationToken.ThrowIfCancellationRequested();
        return copy;
    }

    /// <summary>
    /// Bounded, cancellable conversion of a metadata map to an IMMUTABLE map (the O(1) commit payload).
    /// Checks the token every 64 real entries plus entry/exit, so the final immutable-map construction is
    /// itself cancellable without a full scan on the UI thread.
    /// </summary>
    internal static System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>
        ToImmutableMetadataCancellable(
            IEnumerable<KeyValuePair<string, ChatEntryMetadata>> source,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();   // stage entry
        var builder = System.Collections.Immutable.ImmutableDictionary
            .CreateBuilder<string, ChatEntryMetadata>(StringComparer.Ordinal);
        var visited = 0;
        foreach (var pair in source)
        {
            if ((++visited & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            builder[pair.Key] = pair.Value;
        }
        cancellationToken.ThrowIfCancellationRequested();   // stage exit
        return builder.ToImmutable();
    }

    internal static bool ShouldPreserveLiveEntryDuringAuthoritativeReload(
        ChatEntryMetadata? metadata,
        int maxHistorySequence,
        DateTimeOffset requestStartedAt) =>
        metadata is null ||
        metadata.OpenClawSeq is null ||
        metadata.OpenClawSeq is { } sequence && sequence > maxHistorySequence ||
        metadata.Timestamp is { } timestamp && timestamp >= requestStartedAt ||
        metadata.IsLocalQueuedSend;

    private static void IncrementCount(
        Dictionary<string, int> counts,
        string key) =>
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;

    private static bool TryConsumeCount(
        Dictionary<string, int> counts,
        string key)
    {
        if (!counts.TryGetValue(key, out var count) || count <= 0)
            return false;
        if (count == 1)
            counts.Remove(key);
        else
            counts[key] = count - 1;
        return true;
    }

    private static void ConsumeAnyTimestamp(
        Dictionary<string, List<long>> timestamps,
        string key)
    {
        if (timestamps.TryGetValue(key, out var values) && values.Count > 0)
            values.RemoveAt(0);
    }

    private static TaskCompletionSource CompletedActivation()
    {
        var activation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        activation.TrySetResult();
        return activation;
    }
}
