using OpenClaw.Shared;
using OpenClaw.Chat;
using System.Collections.Immutable;

namespace OpenClawTray.Chat;

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

    private long _connectionGeneration;
    private bool _generationReady = true;
    private TaskCompletionSource _generationActivation =
        CompletedActivation();

    internal long ConnectionGeneration => _connectionGeneration;

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
        string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
            _sessionIds[token.ThreadId] = sessionId;
        _revisions[token.ThreadId] =
            (_revisions.TryGetValue(token.ThreadId, out var revision)
                ? revision
                : 0) + 1;
        _loadedThreads.Add(token.ThreadId);
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
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        var sequenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in rebuilt.Entries)
        {
            rebuiltMetadata.TryGetValue(entry.Id, out var metadata);
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId))
                messageIds.Add(metadata.GatewayMessageId);
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
                messageIds.Contains(metadata.GatewayMessageId))
            {
                ConsumeAnyTimestamp(
                    contentTimestamps,
                    ContentKey(entry.Kind, entry.Text));
                continue;
            }
            if (metadata?.OpenClawSeq is { } sequence &&
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
            if (metadata?.Timestamp is { } timestamp &&
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
                messageIds.Add(metadata.GatewayMessageId);
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
