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
    private readonly HashSet<string> _failedThreads = new();
    private readonly Dictionary<string, long> _revisions = new();
    private readonly Dictionary<string, ChatHistoryPageRequest> _olderPages = new();
    private readonly Dictionary<string, string> _resetClearedSessionIds = new();
    private readonly Dictionary<string, long> _replacementGenerations = new();

    private long _connectionGeneration;
    private bool _generationReady = true;
    private TaskCompletionSource _generationActivation =
        CompletedActivation();

    internal long ConnectionGeneration => _connectionGeneration;

    internal long Revision(string threadId) => _revisions.GetValueOrDefault(threadId);
    internal bool HasCurrentHistory(string threadId) => _loadedThreads.Contains(threadId);

    internal string? ResolveSessionId(string threadId) =>
        _sessionIds.TryGetValue(threadId, out var sessionId)
            ? sessionId
            : null;

    internal IReadOnlyDictionary<string, long> SnapshotRevisions() =>
        new Dictionary<string, long>(_revisions);

    internal IReadOnlySet<string> SnapshotCurrentHistoryThreads() =>
        _loadedThreads.ToImmutableHashSet(StringComparer.Ordinal);

    internal IReadOnlySet<string> SnapshotFailedHistoryThreads() =>
        _failedThreads.ToImmutableHashSet(StringComparer.Ordinal);

    internal IReadOnlySet<string> SnapshotOlderHistoryThreads() =>
        _olderPages.Keys.Where(key => OlderPage(key) is not null).ToImmutableHashSet(StringComparer.Ordinal);

    internal ChatHistoryPageRequest? OlderPage(string threadId) =>
        _loadedThreads.Contains(threadId) && !_failedThreads.Contains(threadId) &&
            _olderPages.TryGetValue(threadId, out var page)
            && ResolveSessionId(threadId) == page.SessionId ? page : null;

    /// <summary>Records that history retries for the thread are exhausted until the next commit or retry.</summary>
    internal void MarkFailed(string threadId) => _failedThreads.Add(threadId);

    internal bool ClearFailed(string threadId) => _failedThreads.Remove(threadId);

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
        _failedThreads.Remove(threadId);
        _olderPages.Remove(threadId);
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
        if (disposed || !force && (_loadedThreads.Contains(threadId) || _failedThreads.Contains(threadId)))
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
        string? sessionId, string? olderCursor = null, bool olderPage = false, string? olderMessageId = null)
    {
        if (!string.IsNullOrEmpty(sessionId))
            _sessionIds[token.ThreadId] = sessionId;
        if (!olderPage)
            _revisions[token.ThreadId] =
                (_revisions.TryGetValue(token.ThreadId, out var revision) ? revision : 0) + 1;
        if (!string.IsNullOrEmpty(sessionId) &&
            (!string.IsNullOrEmpty(olderCursor) || !string.IsNullOrEmpty(olderMessageId)))
            _olderPages[token.ThreadId] = new(olderCursor, sessionId, olderMessageId);
        else
            _olderPages.Remove(token.ThreadId);
        _loadedThreads.Add(token.ThreadId);
        _failedThreads.Remove(token.ThreadId);
    }

    internal long AdvanceConnectionGeneration(bool clearLoaded)
    {
        _connectionGeneration++;
        _generationActivation.TrySetResult();
        _generationReady = false;
        _generationActivation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _failedThreads.Clear();
        _olderPages.Clear();
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
        _olderPages.Remove(threadId);
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

    internal static (ChatTimelineState Timeline, Dictionary<string, ChatEntryMetadata> Metadata)
        PrependOlderPage(ChatHistoryRebuildPlan plan, ChatTimelineState prior,
            IReadOnlyDictionary<string, ChatEntryMetadata> priorMetadata)
    {
        var known = priorMetadata.Values.Where(value => !string.IsNullOrEmpty(value.GatewayMessageId))
            .Select(value => value.GatewayMessageId!).ToHashSet(StringComparer.Ordinal);
        var entries = ImmutableList.CreateBuilder<ChatTimelineItem>();
        var metadata = new Dictionary<string, ChatEntryMetadata>(priorMetadata);
        var reunitedTools = new HashSet<string>(StringComparer.Ordinal);
        var nextId = prior.NextId;
        foreach (var entry in plan.Timeline.Entries)
        {
            plan.Metadata.TryGetValue(entry.Id, out var detail);
            if (detail?.GatewayMessageId is { Length: > 0 } messageId && known.Contains(messageId))
                continue;
            if (entry.Kind == ChatTimelineItemKind.ToolCall && entry.ToolName is { } toolName &&
                entry.ToolRunId is { Length: > 0 } runId && entry.ToolCallId is { Length: > 0 } callId &&
                prior.Entries.FirstOrDefault(existing => existing.Kind == ChatTimelineItemKind.ToolCall &&
                    existing.ToolRunId == runId && existing.ToolCallId == callId) is { } current)
            {
                var enriched = ChatTimelineReducer.Apply(
                    ChatTimelineState.Initial() with { Entries = ImmutableList.Create(current) },
                    new ChatToolStartEvent(entry.Text, toolName, entry.ToolArgs, callId,
                        IdentityStrength: entry.ToolIdentityStrength, RunId: runId,
                        HandoffInput: entry.HandoffInput, InputDetails: entry.ToolInputDetails,
                        ParentCallId: entry.ToolParentCallId));
                entries.Add(enriched.Entries.Single());
                reunitedTools.Add(current.Id);
                continue;
            }
            if (detail?.Timestamp is { } timestamp && prior.Entries.Any(existing =>
                existing.Kind == entry.Kind && existing.Text == entry.Text &&
                priorMetadata.TryGetValue(existing.Id, out var existingDetail) && existingDetail.Timestamp == timestamp))
                continue;
            var id = $"e{nextId++}";
            entries.Add(entry with { Id = id });
            if (detail is not null)
                metadata[id] = detail;
        }
        entries.AddRange(prior.Entries.Where(entry => !reunitedTools.Contains(entry.Id)));
        // A page is historical only. Preserve every active-run identity and live receipt.
        return (prior with { Entries = entries.ToImmutable(), NextId = nextId }, metadata);
    }

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
        if (prior.Entries.Count == 0 && !prior.TurnActive && prior.TurnStartedAt is null && prior.RunUsage is null)
            return (rebuilt, rebuiltMetadata);

        static string ContentKey(ChatTimelineItemKind kind, string text) =>
            $"{kind}|{text}";
        static string SequenceKey(ChatTimelineItemKind kind, int sequence) =>
            $"{kind}|{sequence}";

        var contentTimestamps = new Dictionary<string, List<(long Timestamp, ChatTimelineItem Entry)>>(
            StringComparer.Ordinal);
        var messageIds = new Dictionary<string, ChatTimelineItem>(StringComparer.Ordinal);
        var sequenceEntries = new Dictionary<string, (ChatTimelineItem FinalEntry, int Count)>(StringComparer.Ordinal);
        foreach (var entry in rebuilt.Entries)
        {
            rebuiltMetadata.TryGetValue(entry.Id, out var metadata);
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId))
            {
                if (entry.Kind == ChatTimelineItemKind.Assistant ||
                    !messageIds.ContainsKey(metadata.GatewayMessageId))
                    messageIds[metadata.GatewayMessageId] = entry;
            }
            if (metadata?.OpenClawSeq is { } sequence)
                RecordSequenceEntry(sequenceEntries, SequenceKey(entry.Kind, sequence), entry);
            if (metadata?.Timestamp is { } timestamp && timestamp != default)
            {
                var key = ContentKey(entry.Kind, entry.Text);
                if (!contentTimestamps.TryGetValue(key, out var timestamps))
                {
                    timestamps = [];
                    contentTimestamps[key] = timestamps;
                }
                timestamps.Add((timestamp.ToUnixTimeSeconds(), entry));
            }
        }

        void PreserveMatchedUsage(ChatTimelineItem historyEntry, ChatTimelineItemKind kind, ChatEntryMetadata? liveMetadata)
        {
            if (kind != ChatTimelineItemKind.Assistant ||
                historyEntry.Kind != ChatTimelineItemKind.Assistant ||
                liveMetadata?.UsageSnapshotTimestamp is not { } latest ||
                !rebuiltMetadata.TryGetValue(historyEntry.Id, out var historyMetadata) ||
                (historyMetadata.UsageSnapshotTimestamp > latest ||
                 historyMetadata.UsageSnapshotTimestamp == latest && !liveMetadata.UsageSnapshotIsAuthoritative))
            {
                return;
            }
            // A usage-free final advances freshness without erasing known history usage.
            var hasLiveUsage = liveMetadata.UsageContributionTokens is > 0 ||
                               liveMetadata.UsageSnapshotIsAuthoritative;
            var useLiveUsage = hasLiveUsage && liveMetadata.UsageMeasurementTimestamp is { } measured &&
                (historyMetadata.UsageMeasurementTimestamp is not { } historyMeasured ||
                 measured > historyMeasured ||
                 measured == historyMeasured && liveMetadata.UsageSnapshotIsAuthoritative);
            var usage = useLiveUsage ? liveMetadata : historyMetadata;
            rebuiltMetadata[historyEntry.Id] = historyMetadata with
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                ResponseTokens = usage.ResponseTokens,
                ContextPercent = usage.ContextPercent,
                ContextTokens = usage.ContextTokens,
                UsageContributionTokens = usage.UsageContributionTokens,
                UsageMeasurementTimestamp = usage.UsageMeasurementTimestamp,
                UsageSnapshotTimestamp = latest,
                UsageSnapshotIsAuthoritative = usage.UsageSnapshotIsAuthoritative,
            };
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
        var remappedEntryIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in prior.Entries)
        {
            priorMetadata.TryGetValue(entry.Id, out var metadata);
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId) &&
                messageIds.TryGetValue(metadata.GatewayMessageId, out var matchingId))
            {
                remappedEntryIds[entry.Id] = matchingId.Id;
                PreserveMatchedUsage(matchingId, entry.Kind, metadata);
                ConsumeAnyTimestamp(
                    contentTimestamps,
                    ContentKey(entry.Kind, entry.Text));
                continue;
            }
            if (metadata?.OpenClawSeq is { } sequence &&
                TryConsumeEntry(
                    sequenceEntries,
                    SequenceKey(entry.Kind, sequence),
                    out var sequenceEntryId))
            {
                remappedEntryIds[entry.Id] = sequenceEntryId.Id;
                PreserveMatchedUsage(sequenceEntryId, entry.Kind, metadata);
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
                    Math.Abs(value.Timestamp - priorSeconds) <= 2);
                if (match >= 0)
                {
                    remappedEntryIds[entry.Id] = rebuiltTimes[match].Entry.Id;
                    PreserveMatchedUsage(rebuiltTimes[match].Entry, entry.Kind, metadata);
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
            remappedEntryIds[entry.Id] = entryToAdd.Id;
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
                timestamps.Add((addedTimestamp.ToUnixTimeSeconds(), entryToAdd));
            }
            if (!string.IsNullOrEmpty(metadata?.GatewayMessageId))
                messageIds.TryAdd(metadata.GatewayMessageId, entryToAdd);
            if (metadata?.OpenClawSeq is { } addedSequence)
            {
                RecordSequenceEntry(
                    sequenceEntries,
                    SequenceKey(entryToAdd.Kind, addedSequence),
                    entryToAdd);
            }
            if (metadata is not null)
                rebuiltMetadata[entryToAdd.Id] = metadata;
        }

        var merged = rebuilt with
            {
                Entries = entries.ToImmutable(),
                NextId = nextId,
                TurnActive = prior.TurnActive,
                TurnStartedAt = prior.TurnStartedAt,
                TurnEndedAt = prior.TurnEndedAt,
                ProgressText = prior.ProgressText,
                ProgressRetryAtMs = prior.ProgressRetryAtMs,
                RunUsage = prior.RunUsage,
                ReasoningStream = prior.TurnActive && prior.ReasoningStream is { } reasoning
                    ? reasoning with
                    {
                        Items = reasoning.Items.ConvertAll(item => item with
                        {
                            AfterEntryId = item.AfterEntryId is { } anchor
                                ? remappedEntryIds.GetValueOrDefault(anchor) : null,
                        }),
                    } : null,
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
        merged = ChatReasoningPresentation.Reconcile(merged, rebuiltMetadata);
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

    private static void RecordSequenceEntry(
        Dictionary<string, (ChatTimelineItem FinalEntry, int Count)> entries,
        string key,
        ChatTimelineItem entry)
    {
        entries.TryGetValue(key, out var existing);
        entries[key] = (entry, existing.Count + 1);
    }

    private static bool TryConsumeEntry(
        Dictionary<string, (ChatTimelineItem FinalEntry, int Count)> entries,
        string key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChatTimelineItem? entry)
    {
        entry = null;
        if (!entries.TryGetValue(key, out var existing))
            return false;
        // Each matching part preserves freshness on the final assistant usage owner.
        entry = existing.FinalEntry;
        if (existing.Count == 1)
            entries.Remove(key);
        else
            entries[key] = (existing.FinalEntry, existing.Count - 1);
        return true;
    }

    private static void ConsumeAnyTimestamp(
        Dictionary<string, List<(long Timestamp, ChatTimelineItem Entry)>> timestamps,
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
