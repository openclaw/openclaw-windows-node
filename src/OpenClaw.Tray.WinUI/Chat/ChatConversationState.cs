using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Single lock root for atomic conversation, queue, reset, connection, and
/// history commit state. All exposed operations are closed domain transitions.
/// </summary>
internal sealed class ChatConversationState
{
    private static readonly TimeSpan LocalEchoSuppressionWindow = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly ChatApprovalState _approval = new();
    private readonly ChatHistoryState _history = new();
    private readonly ChatPresentationState _presentation;
    private readonly ChatQueueState _queue = new();
    private readonly ChatLifecycleState _lifecycle = new();
    private readonly ChatResetState _reset = new();
    private readonly Dictionary<string, ChatTimelineState> _timelines = new();

    // Explicit USAGE/TOPOLOGY revision, mutated ONLY under _gate. Bumped when the retained usage
    // topology or usage metadata changes (accepted commit, older page, session usage) - NOT on
    // text-only streaming edits, so the usage projection is not invalidated per streamed token.
    private readonly Dictionary<string, long> _usageRevisions = new(StringComparer.Ordinal);

    // RETAINED-CONTENT version: bumped under _gate for EVERY retained timeline or metadata mutation
    // (streaming text/tool/media/permission, ingest, commits, reset/replacement, session usage). It is the
    // EXACT compare-and-commit guard for the older-page merge: a history lease alone cannot detect a
    // concurrent content/metadata change, so a stale older merge must be rejected rather than applied.
    private readonly Dictionary<string, long> _retainedVersions = new(StringComparer.Ordinal);

    private void BumpRetainedVersionLocked(string threadId) =>
        _retainedVersions[threadId] =
            (_retainedVersions.TryGetValue(threadId, out var currentVersion) ? currentVersion : 0) + 1;

    /// <summary>Exact retained-content version for a thread (under _gate).</summary>
    internal long RetainedVersion(string threadId)
    {
        lock (_gate)
            return _retainedVersions.TryGetValue(threadId, out var current) ? current : 0;
    }

    /// <summary>
    /// Atomically captures the accepted window lease AND the exact retained-content version, so an
    /// older-page merge can prove nothing changed between capture and commit.
    /// </summary>
    internal bool TryCaptureOlderMergeLease(
        string threadId, out ChatHistoryWindowLease lease, out long retainedVersion)
    {
        lock (_gate)
        {
            if (_history.CaptureWindow(
                    threadId,
                    GetResetVersionLocked(threadId),
                    _disposed) is not { } captured)
            {
                lease = default;
                retainedVersion = 0;
                return false;
            }
            lease = captured;
            retainedVersion = _retainedVersions.TryGetValue(threadId, out var current) ? current : 0;
            return true;
        }
    }
    // COW: each thread's entry metadata is an IMMUTABLE map, so an off-lock capture by reference is safe and
    // any later write produces a NEW reference (cheap exact identity compare) without cloning under the lock.
    private readonly Dictionary<string, System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>> _entryMeta = new();
    private ConnectionStatus _status;
    private bool _disposed;

    internal ChatConversationState(
        ConnectionStatus status,
        OpenClawChatDataProvider.LastChatState? lastChatState,
        ModelsListInfo? seedModels)
    {
        _status = status;
        _presentation = new ChatPresentationState(lastChatState, seedModels);
    }

    internal bool IsResponseSuppressed
    {
        get
        {
            lock (_gate)
                return _lifecycle.IsResponseSuppressed;
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    internal ConnectionStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    internal long HistoryGeneration
    {
        get
        {
            lock (_gate)
                return _history.ConnectionGeneration;
        }
    }

    internal OpenClawChatDataProvider.LastChatState? CachedLastChatState
    {
        get
        {
            lock (_gate)
                return _presentation.CachedLastChatState;
        }
    }

    internal ChatDataSnapshot Load(
        SessionInfo[] sessions,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _presentation.ReplaceSessions(
                sessions,
                receivedFromGateway: false);
            EnsureTimelinesForSessionsLocked();
            _presentation.RememberLastSessionState(context);
            return BuildSnapshotLocked(context);
        }
    }

    internal IReadOnlyDictionary<string, ChatEntryMetadata> GetEntryMetadata(string threadId)
    {
        lock (_gate)
        {
            return _entryMeta.TryGetValue(threadId, out var metadata)
                ? new Dictionary<string, ChatEntryMetadata>(metadata)
                : new Dictionary<string, ChatEntryMetadata>();
        }
    }

    /// <summary>Metadata for ONE entry id (no full-dictionary copy) - used by the usage projection.</summary>
    internal ChatEntryMetadata? GetEntryMetadataById(string threadId, string entryId)
    {
        lock (_gate)
            return _entryMeta.TryGetValue(threadId, out var metadata) &&
                   metadata.TryGetValue(entryId, out var entry)
                ? entry
                : null;
    }

    /// <summary>
    /// Bounded metadata snapshot: copies ONLY the requested (visible) entry ids, so a windowed render
    /// does not copy the entire retained metadata dictionary under the lock.
    /// </summary>
    internal IReadOnlyDictionary<string, ChatEntryMetadata> GetEntryMetadataFor(
        string threadId, IReadOnlyCollection<string> entryIds)
    {
        lock (_gate)
        {
            var visible = new Dictionary<string, ChatEntryMetadata>(entryIds.Count, StringComparer.Ordinal);
            if (_entryMeta.TryGetValue(threadId, out var metadata))
            {
                foreach (var id in entryIds)
                {
                    if (metadata.TryGetValue(id, out var entry))
                        visible[id] = entry;
                }
            }
            return visible;
        }
    }

    /// <summary>Cheap metadata/history revision for projection invalidation (bumped by accepted commits).</summary>
    internal long MetadataRevision(string threadId)
    {
        lock (_gate)   // ChatHistoryState requires serialization under the root lock
            return _history.SnapshotRevisions().TryGetValue(threadId, out var revision) ? revision : 0;
    }

    /// <summary>
    /// Atomically captures the ACCEPTED identity and usage/topology revision as a coherent pair (the
    /// projection key), so a concurrent writer cannot produce a torn identity/revision combination.
    /// </summary>
    internal (string? AcceptedIdentity, long UsageRevision) TryCaptureUsageSnapshot(string threadId)
    {
        lock (_gate)
        {
            var identity = _history.GetAcceptedTranscriptId(threadId);
            var revision = _usageRevisions.TryGetValue(threadId, out var current) ? current : 0;
            return (identity, revision);
        }
    }

    /// <summary>
    /// Per-thread BOUNDED, RESUMABLE usage scan. Everything (retained timeline, per-entry metadata and
    /// the usage revision) is read under _gate, so the produced result is coherent by construction - there
    /// is no capture/release window in which metadata can advance under an older revision. A revision or
    /// identity change resets the scan (stale partial work is discarded).
    /// </summary>
    private sealed class UsageScanState
    {
        public long Revision;
        public string? Identity;
        public int NextIndex;
        public string? Summary;
        public string? EntryId;
        public bool Complete;
        public bool SummaryFromThisPass;
    }

    private readonly Dictionary<string, UsageScanState> _usageScans = new(StringComparer.Ordinal);

    /// <summary>Entries examined per call - keeps a first initialization/miss off the unbounded-UI path.</summary>
    internal const int UsageScanStepsPerCall = 64;

    /// <summary>Drops the bounded usage scan for a thread (reset / identity change / removal).</summary>
    internal void ForgetUsageScan(string threadId)
    {
        lock (_gate)
            _usageScans.Remove(threadId);
    }

    /// <summary>
    /// Advances the bounded usage scan by at most <paramref name="maxSteps"/> entries and returns the
    /// current faithful result. While incomplete the PREVIOUS faithful result (if any) is returned together
    /// with Complete=false, so the UI never stalls unboundedly and never shows a torn value.
    /// </summary>
    internal (string? Summary, string? EntryId, bool Complete) AdvanceLatestUsage(
        string threadId, int maxSteps = UsageScanStepsPerCall)
    {
        lock (_gate)
        {
            if (!_timelines.TryGetValue(threadId, out var timeline))
                return (null, null, true);
            var identity = _history.GetAcceptedTranscriptId(threadId);
            var revision = _usageRevisions.TryGetValue(threadId, out var current) ? current : 0;
            if (!_usageScans.TryGetValue(threadId, out var scan))
            {
                scan = new UsageScanState
                {
                    Revision = revision,
                    Identity = identity,
                    NextIndex = timeline.Entries.Count - 1,
                };
                _usageScans[threadId] = scan;
            }
            else if (scan.Revision != revision ||
                     !string.Equals(scan.Identity, identity, StringComparison.Ordinal))
            {
                // New revision/identity: discard stale partial work and restart from the retained tail.
                // The previous faithful result is carried ONLY while the transcript identity is unchanged,
                // so a pending pass never shows a torn/older value; an identity change clears it.
                var sameTranscript = string.Equals(scan.Identity, identity, StringComparison.Ordinal);
                scan = new UsageScanState
                {
                    Revision = revision,
                    Identity = identity,
                    NextIndex = timeline.Entries.Count - 1,
                    Summary = sameTranscript ? scan.Summary : null,
                    EntryId = sameTranscript ? scan.EntryId : null,
                    SummaryFromThisPass = false,
                };
                _usageScans[threadId] = scan;
            }

            var steps = 0;
            while (!scan.Complete && steps < maxSteps && scan.NextIndex >= 0)
            {
                var entry = timeline.Entries[scan.NextIndex];
                scan.NextIndex--;
                steps++;
                if (entry.Kind != ChatTimelineItemKind.Assistant)
                    continue;
                var metadata = _entryMeta.TryGetValue(threadId, out var map) &&
                               map.TryGetValue(entry.Id, out var entryMeta)
                    ? entryMeta
                    : null;
                if (metadata is null)
                    continue;
                var text = ChatUsageFormatter.Format(metadata);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                scan.Summary = text;
                scan.EntryId = entry.Id;
                scan.Complete = true;
                scan.SummaryFromThisPass = true;
            }
            if (scan.NextIndex < 0)
                scan.Complete = true;
            if (scan.Complete && !scan.SummaryFromThisPass)
            {
                // The pass found nothing for the new revision: drop a carried (now stale) result.
                scan.Summary = null;
                scan.EntryId = null;
            }

            return (scan.Summary, scan.EntryId, scan.Complete);
        }
    }


    /// <summary>
    /// Immutable roots captured UNDER the gate for an OFF-LOCK tentative older-page merge. The retained
    /// ChatTimelineState reference changes on ANY timeline write (streaming/permission/local-user/status),
    /// and the immutable metadata map reference changes on ANY metadata write, so comparing these two
    /// references at commit is an exact, comprehensive guard (a content-revision counter alone is not).
    /// </summary>
    internal sealed record OlderMergeRoots(
        ChatHistoryWindowLease Lease,
        int ExpectedOffset,
        string ThreadId,
        ChatTimelineState Timeline,
        System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata> Metadata,
        ChatHistoryWindowState Window,
        long RetainedVersion);

    /// <summary>
    /// Captures the cheap immutable roots for an older-page merge AFTER the page fetch, so the tentative
    /// reconstruction can run OFF LOCK against a consistent snapshot.
    /// </summary>
    internal bool TryPrepareOlderMergeRoots(
        string threadId, int expectedOffset, ChatHistoryWindowState window,
        ChatHistoryWindowLease originalLease, out OlderMergeRoots roots)
    {
        roots = null!;
        lock (_gate)
        {
            if (_disposed)
                return false;
            // The ORIGINAL NETWORK lease (captured when the request started) is the window authority; the
            // content roots are merely refreshed after the fetch. A fresh capture here would admit a stale
            // older response against a NEWER window revision committed while the request was in flight.
            if (!_history.IsCurrent(originalLease.Token, GetResetVersionLocked(threadId), _disposed))
                return false;
            if (_history.CaptureWindow(
                    threadId,
                    GetResetVersionLocked(threadId),
                    _disposed) is not { } lease ||
                lease.Revision != originalLease.Revision ||
                !Equals(lease.Window, originalLease.Window))
            {
                return false;
            }
            if (lease.Window.NextOffset != expectedOffset)
                return false;
            var timeline = GetOrCreateTimelineLocked(threadId);
            var metadata = _entryMeta.TryGetValue(threadId, out var map)
                ? map
                : System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>.Empty;
            roots = new OlderMergeRoots(
                lease,
                expectedOffset,
                threadId,
                timeline,
                metadata,
                window,
                _retainedVersions.TryGetValue(threadId, out var rv) ? rv : 0);
            return true;
        }
    }

    /// <summary>
    /// Atomic compare-and-commit for an OFF-LOCK tentative older merge. Publishes ONLY when the captured
    /// timeline AND metadata references are still current (plus the lease/identity/offset guards), so a
    /// concurrent streaming/permission/local-user/metadata change rejects the tentative result instead of
    /// overwriting newer state. Rejection is cheap and does not require refetching the network page.
    /// </summary>
    internal bool TryCommitPreparedOlderMerge(
        OlderMergeRoots roots,
        ChatHistoryRebuildPlan olderPlan,
        ChatTimelineState mergedTimeline,
        System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata> mergedMetadata,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || _disposed)
                return false;
            var threadId = roots.ThreadId;
            if (!_history.IsCurrent(roots.Lease.Token, GetResetVersionLocked(threadId), _disposed))
                return false;
            if (_history.CaptureWindow(
                    threadId,
                    GetResetVersionLocked(threadId),
                    _disposed) is not { } current ||
                current.Revision != roots.Lease.Revision ||
                !Equals(current.Window, roots.Lease.Window))
            {
                return false;
            }
            if (!SameIdentity(current.Window.SessionId, olderPlan.SessionId) ||
                !SameIdentity(current.Window.SessionId, roots.Window.SessionId) ||
                !string.Equals(roots.Window.SessionKey, threadId, StringComparison.Ordinal) ||
                roots.Window.ResponseOffset != roots.ExpectedOffset)
            {
                return false;
            }
            // EXACT captured-root compare: any timeline or metadata write since capture rejects.
            if (!ReferenceEquals(GetOrCreateTimelineLocked(threadId), roots.Timeline))
                return false;
            var metadataNow = _entryMeta.TryGetValue(threadId, out var map)
                ? map
                : System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>.Empty;
            if (!ReferenceEquals(metadataNow, roots.Metadata))
                return false;
            if ((_retainedVersions.TryGetValue(threadId, out var rvNow) ? rvNow : 0) != roots.RetainedVersion)
                return false;

            _timelines[threadId] = mergedTimeline;
            _entryMeta[threadId] = mergedMetadata;   // already immutable: O(1) assignment, no conversion under the gate
            _history.MarkOlderCommitted(roots.Lease.Token, roots.Window);
            BumpUsageRevisionLocked(threadId);
            BumpRetainedVersionLocked(threadId);
            return true;
        }
    }

    /// <summary>
    /// Signals a retained usage/topology change (accepted history commit, older page, session usage).
    /// Callers must NOT call this for text-only streaming edits.
    /// </summary>
    internal void NoteUsageTopologyChanged(string threadId)
    {
        lock (_gate)
            BumpUsageRevisionLocked(threadId);
    }

    /// <summary>Bumps the usage/topology revision. Callers must already hold _gate.</summary>
    private void BumpUsageRevisionLocked(string threadId) =>
        _usageRevisions[threadId] =
            (_usageRevisions.TryGetValue(threadId, out var current) ? current : 0) + 1;

    /// <summary>The accepted retained-transcript identity for a thread (null when none has been accepted).</summary>
    internal string? GetAcceptedTranscriptId(string threadId)
    {
        lock (_gate)
            return _history.GetAcceptedTranscriptId(threadId);
    }

    internal OpenClawChatDataProvider.LastChatState? RememberSelectedThread(string threadId)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;
            return _presentation.RememberSelectedThread(threadId);
        }
    }

    internal ChatDataSnapshot Snapshot(ChatProjectionContext context)
    {
        lock (_gate)
            return BuildSnapshotLocked(context);
    }

    internal string? ResolveDefaultThreadId(ChatProjectionContext context)
    {
        lock (_gate)
        {
            return ChatSnapshotProjector.ResolveDefaultThreadId(
                CaptureProjectionInputLocked(context));
        }
    }

    internal (string CacheKey, long ResetGeneration) ResolveMetadataKey(string threadId)
    {
        lock (_gate)
        {
            var key = _history.ResolveSessionId(threadId) ?? threadId;
            return (key, GetResetVersionLocked(threadId));
        }
    }

    internal long GetResetGeneration(string threadId)
    {
        lock (_gate)
            return GetResetVersionLocked(threadId);
    }

    internal ChatHistoryCommitToken CaptureHistoryToken(string threadId)
    {
        lock (_gate)
        {
            return _history.CreateCommitToken(
                threadId,
                GetResetVersionLocked(threadId));
        }
    }

    internal ChatHistoryReplacementTransition? BeginHistoryReplacement(
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;

            var token = _history.BeginReplacement(
                threadId,
                GetResetVersionLocked(threadId));
            _timelines[threadId] = ChatTimelineState.Initial();
            _entryMeta.Remove(threadId);
            return new(BuildSnapshotLocked(context), token);
        }
    }

    internal bool TryBeginHistory(
        string threadId,
        bool force,
        ChatHistoryCommitToken? expectedToken,
        out ChatHistoryCommitToken token,
        out string? model,
        out Task? generationActivation)
    {
        lock (_gate)
        {
            var canBegin = _history.TryBegin(
                threadId,
                force,
                expectedToken,
                GetResetVersionLocked(threadId),
                _status,
                _disposed,
                out token,
                out generationActivation);
            model = _presentation.ModelForThread(threadId);
            return canBegin;
        }
    }

    internal bool IsHistoryRequestCurrent(ChatHistoryCommitToken token)
    {
        lock (_gate)
            return _history.IsCurrent(
                token,
                GetResetVersionLocked(token.ThreadId),
                _disposed);
    }

    /// <summary>
    /// The accepted bounded page window for a thread, generation-validated against the current
    /// reset/connection/replacement generations. Null when no valid window has been accepted.
    /// </summary>
    internal ChatHistoryWindowState? GetHistoryWindow(string threadId)
    {
        lock (_gate)
            return _history.GetWindow(
                threadId,
                GetResetVersionLocked(threadId),
                _disposed);
    }

    internal bool CanRetryHistory(
        ChatHistoryCommitToken token,
        bool authoritative)
    {
        lock (_gate)
            return _history.CanRetry(
                token,
                GetResetVersionLocked(token.ThreadId),
                _status,
                authoritative,
                _disposed);
    }

    internal bool CommitHistory(
        ChatHistoryCommitToken token,
        ChatHistoryRebuildPlan plan,
        DateTimeOffset requestStartedAt,
        bool authoritative,
        ChatHistoryWindowState? window = null)
    {
        lock (_gate)
        {
            if (!_history.IsCurrent(
                    token,
                    GetResetVersionLocked(token.ThreadId),
                    _disposed))
            {
                return false;
            }

            // A bounded initial page is an INCOMPLETE tail. It must never take the authoritative
            // replacement path, which would drop previously seen older entries. Only a complete
            // window (or a caller that supplied no window) keeps the authoritative merge.
            var effectiveAuthoritative =
                authoritative && (window is null || !window.Value.HasMore);

            // Explicit initial-page identity replacement: if the accepted identity for this key changed
            // (e.g. a new session UUID), drop the previous identity's entries instead of silently mixing
            // two sessions under one key. Same-identity commits keep prior entries.
            // Use the ACCEPTED history/window identity first, NOT the (possibly catalog-seeded) session
            // id: ApplySessions can seed a new UUID before the first commit for a new transcript, which
            // must not be mistaken for the identity of the retained timeline. An empty/failed catalog
            // never clears anything (the fallback keeps the prior behaviour).
            var acceptedSessionId =
                _history.GetAcceptedTranscriptId(token.ThreadId)
                ?? _history.GetWindow(token.ThreadId, GetResetVersionLocked(token.ThreadId), _disposed)?.SessionId
                ?? _history.ResolveSessionId(token.ThreadId);
            var identityReplaced =
                !string.IsNullOrEmpty(plan.SessionId) &&
                !string.IsNullOrEmpty(acceptedSessionId) &&
                !string.Equals(plan.SessionId, acceptedSessionId, StringComparison.Ordinal);
            var prior = identityReplaced
                ? ChatTimelineState.Initial()
                : GetOrCreateTimelineLocked(token.ThreadId);
            var priorMetadata = identityReplaced
                ? System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>.Empty
                : _entryMeta.TryGetValue(
                    token.ThreadId,
                    out var metadata)
                    ? metadata
                    : System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>.Empty;
            var merged = ChatHistoryState.MergeWithLiveEntries(
                plan,
                prior,
                priorMetadata,
                requestStartedAt,
                effectiveAuthoritative);
            _timelines[token.ThreadId] = merged.Timeline;
            _entryMeta[token.ThreadId] = System.Collections.Immutable.ImmutableDictionary.CreateRange(StringComparer.Ordinal, merged.Metadata);
            // Window storage shares this token-validated, locked commit: a stale or failed
            // reconstruction can never publish window metadata.
            _history.MarkCommitted(token, plan.SessionId, window);
            BumpUsageRevisionLocked(token.ThreadId);   // accepted history commit changes usage topology
            BumpRetainedVersionLocked(token.ThreadId); // ... and retained content
            return true;
        }
    }

    /// <summary>
    /// Atomic older-page commit. Identity-validated against the accepted window and the plan; merges
    /// the older page into the held timeline (older entries prepended, replay overlap dropped, newer
    /// and live entries preserved) and advances the window. A mismatched identity is rejected WITHOUT
    /// clearing held state.
    /// </summary>
    /// <summary>
    /// Atomically captures the accepted window with its token/revision so an older-page request can be
    /// compared against it at commit time.
    /// </summary>
    internal bool TryCaptureHistoryWindowLease(string threadId, out ChatHistoryWindowLease lease)
    {
        lock (_gate)
        {
            if (_history.CaptureWindow(
                    threadId,
                    GetResetVersionLocked(threadId),
                    _disposed) is { } captured)
            {
                lease = captured;
                return true;
            }
            lease = default;
            return false;
        }
    }

    /// <summary>
    /// Strict session identity: both sides must be a known, non-empty, ordinal-equal UUID. An unknown
    /// or empty candidate against a known accepted identity is refused (unsupported/failure), never
    /// admitted as verified identity.
    /// </summary>
    private static bool SameIdentity(string? accepted, string? candidate) =>
        !string.IsNullOrEmpty(accepted) &&
        !string.IsNullOrEmpty(candidate) &&
        string.Equals(accepted, candidate, StringComparison.Ordinal);

    /// <summary>The current model for a thread (used by the older-page reconstruction worker).</summary>
    internal string? ModelForThread(string threadId)
    {
        lock (_gate)
            return _presentation.ModelForThread(threadId);
    }

    internal ChatDataSnapshot? SnapshotIfHistoryTokenCurrent(
        ChatHistoryCommitToken token,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            return _history.IsCurrent(
                       token,
                       GetResetVersionLocked(token.ThreadId),
                       _disposed)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    /// <summary>
    /// SOURCE-OWNED delivery fence reused exactly by the WinUI provider consumer. Builds the snapshot
    /// only when the generation token AND (when the result carries one) the FULL ORIGINAL WINDOW LEASE are
    /// still current. A same-cursor refresh keeps the generation token but replaces the window revision, so
    /// a generation token alone cannot fence an exhausted/error notification; the lease compare closes it.
    /// The fence and the snapshot are one atomic observation under _gate (no second-read race).
    /// </summary>
    internal ChatDataSnapshot? SnapshotIfHistoryResultCurrent(
        ChatHistoryLoadResult result,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            return IsHistoryResultCurrentLocked(result)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    private bool IsHistoryResultCurrentLocked(ChatHistoryLoadResult result)
    {
        var threadId = result.Token.ThreadId;
        var reset = GetResetVersionLocked(threadId);
        if (!_history.IsCurrent(result.Token, reset, _disposed))
            return false;
        if (result.Lease is not { } lease)
            return true;   // no window authority attached: generation-token fence only
        // FULL LEASE AUTHORITY: deliverable only while the accepted window is still exactly the original
        // lease (revision + echoed window identity/cursor).
        return _history.CaptureWindow(threadId, reset, _disposed) is { } current &&
               current.Revision == lease.Revision &&
               Equals(current.Window, lease.Window);
    }

    internal bool IsCurrentResetGeneration(string threadId, long generation)
    {
        lock (_gate)
            return GetResetVersionLocked(threadId) == generation;
    }

    internal ChatStatusTransition ApplyStatus(
        ConnectionStatus status,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return new(
                    BuildSnapshotLocked(context),
                    false,
                    false,
                    [],
                    _history.ConnectionGeneration);
            }

            var reconnected = status == ConnectionStatus.Connected &&
                              _status != ConnectionStatus.Connected;
            var disconnected = status != ConnectionStatus.Connected &&
                               _status == ConnectionStatus.Connected;
            _status = status;
            if (status != ConnectionStatus.Connected)
                _presentation.LeaveConnected();
            if (disconnected)
                _approval.Reset();

            string[] interruptedThreads = [];
            if (reconnected)
            {
                _history.AdvanceConnectionGeneration(clearLoaded: true);
                _queue.ClearForReconnect();
                _reset.ClearSubmittedEchoesForReconnect();
                _lifecycle.ClearForReconnect();
                _presentation.ResetKeylessDiagnostic();
                foreach (var threadId in _timelines.Keys.ToArray())
                {
                    _timelines[threadId] = ChatTimelineReducer.Apply(
                        _timelines[threadId],
                        new ChatToolReplayResetEvent());
                }
            }
            if (disconnected)
            {
                _history.AdvanceConnectionGeneration(clearLoaded: false);
                _reset.ClearSubmittedEchoesForReconnect();
                interruptedThreads = _timelines
                    .Where(pair => pair.Value.TurnActive)
                    .Select(pair => pair.Key)
                    .ToArray();
                _lifecycle.ClearActiveRuns(interruptedThreads);
            }
            return new(
                BuildSnapshotLocked(context),
                reconnected,
                disconnected,
                interruptedThreads,
                _history.ConnectionGeneration);
        }
    }

    internal void ClearToolReplayState()
    {
        lock (_gate)
        {
            foreach (var threadId in _timelines.Keys.ToArray())
            {
                _timelines[threadId] = ChatTimelineReducer.Apply(
                    _timelines[threadId],
                    new ChatToolReplayResetEvent());
            }
        }
    }

    internal ChatSessionsTransition ApplySessions(
        SessionInfo[] sessions,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
                return new(BuildSnapshotLocked(context), []);
            var previousUsage = _presentation.SnapshotUsage();
            _presentation.ReplaceSessions(sessions);
            var currentSessions = _presentation.SessionSnapshot();
            _history.SeedSessionIds(currentSessions);
            EnsureTimelinesForSessionsLocked();
            _presentation.RememberLastSessionState(context);
            foreach (var session in currentSessions)
            {
                if (string.IsNullOrEmpty(session.Key))
                    continue;
                var usage = new ChatUsageSnapshot(
                    session.InputTokens,
                    session.OutputTokens,
                    session.TotalTokens,
                    session.ContextTokens);
                if (!previousUsage.TryGetValue(session.Key, out var previous) ||
                    previous != usage)
                {
                    SnapshotLatestAssistantUsageLocked(
                        session,
                        _presentation.ResolveTimelineKey(session, _timelines));
                }
            }
            return new(
                BuildSnapshotLocked(context),
                _status == ConnectionStatus.Connected
                    ? _queue.ThreadsWithMessages()
                    : []);
        }
    }

    internal ChatDataSnapshot ApplyModels(
        ModelsListInfo models,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
                return BuildSnapshotLocked(context);
            _presentation.ApplyModels(models);
            return BuildSnapshotLocked(context);
        }
    }

    internal ChatSessionOptionPatchLease BeginSessionOptionPatch(string threadId)
    {
        lock (_gate)
            return _presentation.BeginSessionOptionPatch(threadId);
    }

    internal void CompleteSessionOptionPatch(ChatSessionOptionPatchLease lease, Exception? error)
    {
        lock (_gate)
            _presentation.CompleteSessionOptionPatch(lease, error);
    }

    internal Task? GetPendingSessionOptionPatch(string threadId)
    {
        lock (_gate)
            return _presentation.GetPendingSessionOptionPatch(threadId);
    }

    internal bool TryBeginCommandCatalogFetch(out int epoch)
    {
        lock (_gate)
            return _presentation.TryBeginCommandCatalogFetch(_status, out epoch);
    }

    internal bool CompleteCommandCatalogFetch(int epoch, CommandCatalog catalog)
    {
        lock (_gate)
            return _presentation.CompleteCommandCatalogFetch(
                epoch,
                _status,
                catalog);
    }

    internal bool FailCommandCatalogFetch(int epoch)
    {
        lock (_gate)
            return _presentation.FailCommandCatalogFetch(epoch, _status);
    }

    internal ChatDataSnapshot? SnapshotCommandCatalogIfFresh(
        int epoch,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            return !_disposed &&
                   _presentation.IsCommandCatalogEpochCurrent(epoch)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    internal ChatDisposeTransition DisposeState()
    {
        lock (_gate)
        {
            if (_disposed)
                return new(
                    _history.ConnectionGeneration,
                    IsFirstDispose: false);
            _disposed = true;
            _history.AdvanceConnectionGeneration(clearLoaded: false);
            _queue.ClearForDispose();
            _lifecycle.ClearForDispose();
            _reset.ClearSubmittedEchoesForReconnect();
            return new(
                _history.ConnectionGeneration,
                IsFirstDispose: true);
        }
    }

    internal bool TryRaiseKeylessDiagnostic()
    {
        lock (_gate)
            return _presentation.TryRaiseKeylessDiagnostic();
    }

    internal string? PendingPermissionId(string threadId)
    {
        lock (_gate)
            return GetOrCreateTimelineLocked(threadId).PendingPermission?.RequestId;
    }

    internal bool CanRespondToPermission(string threadId, string requestId, string action)
    {
        lock (_gate)
        {
            var pending = GetOrCreateTimelineLocked(threadId).PendingPermission;
            return pending is not null
                && string.Equals(pending.RequestId, requestId, StringComparison.Ordinal)
                && ChatPermissionActionKeys.NormalizeActions(pending.Actions)
                    .Contains(action, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal void ActivateHistoryGeneration(long generation)
    {
        lock (_gate)
            _history.ActivateConnectionGeneration(generation, _disposed);
    }

    private bool SnapshotLatestAssistantUsageLocked(
        SessionInfo session,
        string threadId)
    {
        if (string.IsNullOrEmpty(session.Key))
            return false;
        var usedTokens = session.TotalTokens;
        if (usedTokens <= 0)
            usedTokens = session.InputTokens + session.OutputTokens;
        if (usedTokens <= 0 ||
            string.IsNullOrEmpty(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline))
        {
            return false;
        }
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            if (timeline.Entries[i].Kind != ChatTimelineItemKind.Assistant)
                continue;
            var metadata = GetOrCreateThreadMetaLocked(threadId);
            metadata.TryGetValue(timeline.Entries[i].Id, out var existing);
            var usageSnapshot = Math.Max(
                usedTokens,
                existing?.ResponseTokens ?? 0);
            var usageTokens = ToIntIfPositive(usageSnapshot);
            var contextTokens = session.ContextTokens > 0
                ? session.ContextTokens
                : existing?.ContextTokens;
            if (existing is not null &&
                existing.ResponseTokens == usageTokens &&
                existing.ContextTokens == contextTokens)
            {
                return false;
            }
            metadata = metadata.SetItem(timeline.Entries[i].Id, (existing ?? BuildLiveMetaLocked(threadId)) with
            {
                InputTokens = ToIntIfPositive(session.InputTokens),
                OutputTokens = ToIntIfPositive(session.OutputTokens),
                ResponseTokens = usageTokens,
                ContextTokens = contextTokens,
                ContextPercent = existing?.ContextPercent,
                UsageContributionTokens = existing?.UsageContributionTokens,
            });
            _entryMeta[threadId] = metadata;   // publish the new immutable snapshot (COW)
            BumpUsageRevisionLocked(threadId);   // session usage mutation, atomically with the write
            BumpRetainedVersionLocked(threadId); // metadata-only writes must invalidate the merge snapshot
            return true;
        }
        return false;
    }

    private static int? ToIntIfPositive(long value) =>
        value > 0 && value <= int.MaxValue ? (int)value : null;

    internal static bool ShouldPreserveLiveEntryDuringAuthoritativeReload(
        ChatEntryMetadata? metadata,
        int maxHistorySequence,
        DateTimeOffset requestStartedAt) =>
        ChatHistoryState.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            metadata,
            maxHistorySequence,
            requestStartedAt);

    internal ChatQueuedAdmission AdmitMessage(
        string threadId,
        string text,
        string displayText,
        string nonce,
        IReadOnlyList<ChatAttachment>? attachments,
        DateTimeOffset createdAt,
        ChatProjectionContext context,
        string? timelineText = null,
        IReadOnlyList<ChatAttachmentPresentation>? attachmentPresentations = null,
        string attachmentCorrelationSignature = "")
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var messageId = _queue.NextMessageId();
            if (CanClearAssistantFallbackPromotionLocked(threadId))
                _queue.ClearAssistantFallbackPromotion(threadId);

            _lifecycle.ClearThreadSuppression(threadId);
            _lifecycle.TakePendingAbortCount(threadId);
            var request = new ChatQueuedSendRequest(
                messageId,
                Guid.NewGuid().ToString(),
                threadId,
                text,
                displayText,
                nonce,
                attachments?.ToArray(),
                TimelineText: timelineText ?? displayText,
                AttachmentPresentations: attachmentPresentations,
                AttachmentCorrelationSignature: attachmentCorrelationSignature);

            var sendDirectly = CanSendDirectlyLocked(threadId);
            ChatQueuedSendDispatch? dispatch;
            if (sendDirectly)
            {
                dispatch = StartDirectSendLocked(request);
            }
            else
            {
                _queue.AddMessage(threadId, new ChatQueuedMessage(
                    messageId,
                    displayText,
                    createdAt,
                    nonce));
                _queue.AddRequest(request);
                dispatch = TryStartNextQueuedSendLocked(
                    threadId,
                    requireConnected: false,
                    out _);
            }

            return new ChatQueuedAdmission(
                messageId,
                Queued: !sendDirectly,
                dispatch,
                BuildSnapshotLocked(context),
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    internal ChatDataSnapshot EnqueueCompact(
        string threadId,
        DateTimeOffset createdAt,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var messageId = _queue.NextMessageId();
            var request = new ChatQueuedSendRequest(
                messageId,
                Guid.NewGuid().ToString(),
                threadId,
                "/compact",
                "/compact",
                Guid.NewGuid().ToString(),
                Attachments: null,
                LifecycleCommand: ChatLifecycleCommandKind.Compact);
            _queue.AddMessage(threadId, new ChatQueuedMessage(
                messageId,
                request.DisplayText,
                createdAt,
                request.LocalNonce));
            _queue.AddRequest(request);
            return BuildSnapshotLocked(context);
        }
    }

    internal (bool Canceled, ChatDataSnapshot? Snapshot) CancelQueuedMessage(
        string threadId,
        string messageId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var canceled = _queue.CancelMessage(threadId, messageId);
            if (canceled)
            {
                _queue.ClearLocallyInitiatedIfIdle(
                    threadId,
                    _lifecycle.HasActiveRun(threadId),
                    _timelines.TryGetValue(threadId, out var timeline) &&
                    timeline.TurnActive);
            }
            return (
                canceled,
                canceled ? BuildSnapshotLocked(context) : null);
        }
    }

    internal ChatQueueStart TryStartNextQueuedSend(
        string threadId,
        bool requireConnected,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
                return new(null, null, null);
            var dispatch = TryStartNextQueuedSendLocked(
                threadId,
                requireConnected,
                out var delayedRetry);
            return new ChatQueueStart(
                dispatch,
                delayedRetry,
                dispatch is null ? null : BuildSnapshotLocked(context));
        }
    }

    internal bool TryScheduleQueueDrain(string threadId)
    {
        lock (_gate)
        {
            return !_disposed && _queue.TryScheduleDrain(threadId);
        }
    }

    internal void CompleteQueueDrainSchedule(string threadId)
    {
        lock (_gate)
            _queue.CompleteDrainSchedule(threadId);
    }

    internal ChatSendPreparation PrepareSendAttempt(
        ChatQueuedSendDispatch dispatch,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (!IsDispatchGenerationCurrentLocked(dispatch) ||
                !dispatch.StartedDirectly &&
                _queue.FindRequest(
                    dispatch.Request.ThreadId,
                    dispatch.Request.Id) is null)
            {
                return new(
                    false,
                    CleanupStaleDispatchTurnLocked(dispatch, context));
            }
            _queue.TrackRun(
                dispatch.Request.ThreadId,
                dispatch.Request.SendRunId,
                dispatch.Request.Id);
            return new(true, null);
        }
    }

    internal ChatSendCommit CommitSendResult(
        ChatQueuedSendDispatch dispatch,
        ChatSendResult sendResult,
        ChatProjectionContext context)
    {
        var request = dispatch.Request;
        var threadId = request.ThreadId;
        var acceptedRunId = string.IsNullOrWhiteSpace(sendResult.RunId)
            ? null
            : sendResult.RunId;
        lock (_gate)
        {
            if (!IsDispatchGenerationCurrentLocked(dispatch))
            {
                _reset.RemovePendingLocalSubmission(
                    threadId,
                    request.Id,
                    dispatch.ResetVersion);
                var staleRunId = acceptedRunId ?? request.SendRunId;
                _reset.AddIgnoredRun(threadId, staleRunId);
                return new(
                    IsCurrent: false,
                    AcceptedSnapshot:
                        CleanupStaleDispatchTurnLocked(dispatch, context),
                    RequeuedSnapshot: null,
                    StaleRunIdToAbort: staleRunId,
                    BindAcceptedRun: false,
                    RequeueRequired: false,
                    RetryDeferredSend: false,
                    DeferredRetryDelay: ChatSendQueuePolicy.DrainDelay,
                    OpenedLifecycle: null,
                    CurrentRuntimeGenerationLocked(threadId));
            }

            ChatDataSnapshot? acceptedSnapshot = null;
            ChatDataSnapshot? requeuedSnapshot = null;
            ChatOpenedLifecycleTransition? openedLifecycle = null;
            var bindAcceptedRun = false;
            var requeueRequired = false;
            var retryDeferredSend = false;
            var deferredRetryDelay = ChatSendQueuePolicy.DrainDelay;
            if (ChatSendQueuePolicy.IsDeferredAdmissionStatus(sendResult.Status))
            {
                _reset.RemovePendingLocalSubmission(
                    threadId,
                    request.Id,
                    dispatch.ResetVersion);
                var runAlreadyStarted = !string.IsNullOrEmpty(acceptedRunId)
                    && _lifecycle.HasRunStartedAfter(
                        threadId,
                        acceptedRunId,
                        dispatch.StartedRunStartSequence);
                if (runAlreadyStarted)
                {
                    bindAcceptedRun = true;
                    _queue.TrackRun(threadId, acceptedRunId!, request.Id);
                    AddResetAcceptedRunIdLocked(threadId, acceptedRunId!);
                    if (PromoteQueuedMessageLocked(threadId, request.Id))
                        acceptedSnapshot = BuildSnapshotLocked(context);
                    else
                        _queue.RemoveRunMappingByMessageId(threadId, request.Id);
                }
                else if (_queue.RequeueDeferredAdmission(
                             threadId,
                             request.Id,
                             _lifecycle.HasActiveRun(threadId)) is
                         { Requeued: true } retry)
                {
                    deferredRetryDelay = retry.Delay;
                    if (retry.ShouldEndTurn)
                    {
                        _timelines[threadId] = ChatTimelineReducer.Apply(
                            GetOrCreateTimelineLocked(threadId),
                            new ChatTurnEndEvent());
                    }
                    requeueRequired = true;
                    if (!string.IsNullOrEmpty(acceptedRunId))
                    {
                        _queue.TrackRun(threadId, acceptedRunId, request.Id);
                        openedLifecycle =
                            AddResetAcceptedRunIdLocked(
                                threadId,
                                acceptedRunId);
                    }
                    requeuedSnapshot = BuildSnapshotLocked(context);
                    retryDeferredSend = true;
                }
                else if (dispatch.StartedDirectly)
                {
                    throw new InvalidOperationException(
                        $"Gateway returned chat.send status {sendResult.Status} before admitting the direct send.");
                }
            }
            else if (!string.IsNullOrEmpty(acceptedRunId))
            {
                bindAcceptedRun = true;
                _queue.TrackRun(threadId, acceptedRunId, request.Id);
                openedLifecycle =
                    AddResetAcceptedRunIdLocked(
                        threadId,
                        acceptedRunId);
                var runAlreadyStarted =
                    _lifecycle.HasRunStartedAfter(
                        threadId,
                        acceptedRunId,
                        dispatch.StartedRunStartSequence);
                if (PromoteQueuedMessageLocked(threadId, request.Id))
                    acceptedSnapshot = BuildSnapshotLocked(context);
                else if (runAlreadyStarted)
                    _queue.RemoveRunMappingByMessageId(threadId, request.Id);
            }
            else if (_reset.IsAwaitingUserMessage(threadId))
            {
                _queue.RemoveRunMappingByRunId(threadId, request.SendRunId);
                openedLifecycle =
                    ApplyBufferedLifecycleOpenLocked(
                        threadId,
                        _reset.RecordLocalSendWithoutRun(
                            threadId,
                            dispatch.ResetVersion,
                            dispatch.StartedLifecycleSequence,
                            request.Id),
                        allowRemoteTurn: false);
                if (PromoteQueuedMessageLocked(threadId, request.Id))
                    acceptedSnapshot = BuildSnapshotLocked(context);
            }
            else if (PromoteQueuedMessageLocked(threadId, request.Id))
            {
                _queue.RemoveRunMappingByRunId(threadId, request.SendRunId);
                acceptedSnapshot = BuildSnapshotLocked(context);
            }

            return new(
                IsCurrent: true,
                acceptedSnapshot,
                requeuedSnapshot,
                StaleRunIdToAbort: null,
                bindAcceptedRun,
                requeueRequired,
                retryDeferredSend,
                deferredRetryDelay,
                openedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    internal ChatSendFailure FailSend(
        ChatQueuedSendDispatch dispatch,
        string queueError,
        string timelineError,
        ChatProjectionContext context)
    {
        var request = dispatch.Request;
        lock (_gate)
        {
            _reset.RemovePendingLocalSubmission(
                request.ThreadId,
                request.Id,
                dispatch.ResetVersion);
            if (!IsDispatchGenerationCurrentLocked(dispatch))
            {
                return new(
                    false,
                    CleanupStaleDispatchTurnLocked(dispatch, context));
            }

            _queue.RemovePendingLocalEcho(request.ThreadId, request.Id);
            _queue.MarkFailed(request.ThreadId, request.Id, queueError);
            _queue.RemoveRequest(request.ThreadId, request.Id);
            _queue.RemoveRunMappingByMessageId(request.ThreadId, request.Id);
            if (!_queue.HasSendingMessages(request.ThreadId))
                _queue.ClearLocallyInitiated(request.ThreadId);
            ApplyEventLocked(
                request.ThreadId,
                ChatContentFormatting.TruncateChatEvent(
                    new ChatErrorEvent(timelineError)),
                metadata: null);
            ApplyEventLocked(request.ThreadId, new ChatTurnEndEvent(), metadata: null);
            return new(true, BuildSnapshotLocked(context));
        }
    }

    internal bool IsQueuedDispatchCurrent(ChatQueuedSendDispatch dispatch)
    {
        lock (_gate)
        {
            return IsDispatchGenerationCurrentLocked(dispatch)
                && _queue.FindRequest(
                    dispatch.Request.ThreadId,
                    dispatch.Request.Id) is not null;
        }
    }

    internal (bool Succeeded, ChatDataSnapshot? Snapshot) CompleteQueuedLifecycle(
        ChatQueuedSendDispatch dispatch,
        bool succeeded,
        string? error,
        ChatProjectionContext context)
    {
        var request = dispatch.Request;
        lock (_gate)
        {
            if (!IsDispatchGenerationCurrentLocked(dispatch) ||
                _queue.FindRequest(request.ThreadId, request.Id) is null)
            {
                return (false, null);
            }

            if (succeeded)
            {
                var removed = _queue.RemoveMessage(request.ThreadId, request.Id);
                return (true, removed ? BuildSnapshotLocked(context) : null);
            }

            ApplyEventLocked(
                request.ThreadId,
                new ChatErrorEvent(error ?? "The lifecycle command failed."),
                metadata: null);
            _queue.MarkFailed(
                request.ThreadId,
                request.Id,
                error ?? "The lifecycle command failed.");
            _queue.RemoveRequest(request.ThreadId, request.Id);
            return (true, BuildSnapshotLocked(context));
        }
    }

    private bool IsDispatchGenerationCurrentLocked(
        ChatQueuedSendDispatch dispatch) =>
        !_disposed &&
        _history.ConnectionGeneration == dispatch.ConnectionGeneration &&
        GetResetVersionLocked(dispatch.Request.ThreadId) == dispatch.ResetVersion;

    private ChatDataSnapshot? CleanupStaleDispatchTurnLocked(
        ChatQueuedSendDispatch dispatch,
        ChatProjectionContext context)
    {
        var threadId = dispatch.Request.ThreadId;
        if (_disposed ||
            _lifecycle.HasActiveRun(threadId) ||
            _queue.IsLocallyInitiated(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline) ||
            !timeline.TurnActive)
        {
            return null;
        }
        _timelines[threadId] = ChatTimelineReducer.Apply(
            timeline,
            new ChatTurnEndEvent());
        return BuildSnapshotLocked(context);
    }

    private bool CanSendDirectlyLocked(string threadId) =>
        _queue.CanSendDirectly(
            threadId,
            _lifecycle.HasActiveRun(threadId),
            _timelines.TryGetValue(threadId, out var timeline) && timeline.TurnActive);

    private bool CanClearAssistantFallbackPromotionLocked(string threadId) =>
        _queue.CanClearAssistantFallback(
            threadId,
            _lifecycle.HasActiveRun(threadId),
            _timelines.TryGetValue(threadId, out var timeline) &&
            timeline.TurnActive);

    private ChatQueuedSendDispatch StartDirectSendLocked(
        ChatQueuedSendRequest request)
    {
        var threadId = request.ThreadId;
        var resetVersion = GetResetVersionLocked(threadId);
        var current = GetOrCreateTimelineLocked(threadId);
        var entryId = $"e{current.NextId}";
        _timelines[threadId] = ChatTimelineReducer.AddLocalUser(
            current,
            request.EffectiveTimelineText,
            request.LocalNonce);
        var localMeta = GetOrCreateThreadMetaLocked(threadId);
        _entryMeta[threadId] = localMeta.SetItem(entryId, BuildLiveMetaLocked(
            threadId,
            isLocalQueuedSend: true,
            localQueuedMessageId: request.Id,
            attachments: request.AttachmentPresentations));
        var dispatch = _queue.StartDirect(
            request,
            _history.ResolveSessionId(threadId),
            _history.ConnectionGeneration,
            resetVersion,
            _reset.LifecycleStartSequence,
            _lifecycle.LifecycleStartSequence);
        RegisterResetSubmissionLocked(dispatch);
        return dispatch;
    }

    private ChatQueuedSendDispatch? TryStartNextQueuedSendLocked(
        string threadId,
        bool requireConnected,
        out TimeSpan? delayedRetry)
    {
        var turnActive = _timelines.TryGetValue(threadId, out var timeline) &&
                         timeline.TurnActive;
        var dispatch = _queue.TryStartNext(
            threadId,
            requireConnected,
            _status,
            _lifecycle.HasActiveRun(threadId),
            turnActive,
            _history.ResolveSessionId(threadId),
            _history.ConnectionGeneration,
            GetResetVersionLocked(threadId),
            _reset.LifecycleStartSequence,
            _lifecycle.LifecycleStartSequence,
            out delayedRetry);
        if (dispatch?.Request.LifecycleCommand is null && dispatch is not null)
        {
            RegisterResetSubmissionLocked(dispatch);
            _timelines[threadId] = ChatTimelineReducer.BeginLocalUserTurn(
                GetOrCreateTimelineLocked(threadId));
        }
        return dispatch;
    }

    private void RegisterResetSubmissionLocked(
        ChatQueuedSendDispatch dispatch)
    {
        _reset.RegisterPendingLocalSubmission(
            dispatch.Request.ThreadId,
            dispatch.Request.Id,
            dispatch.Request.Text,
            dispatch.ResetVersion,
            dispatch.StartedLifecycleSequence,
            DateTimeOffset.UtcNow,
            requiresEcho:
                !string.IsNullOrWhiteSpace(
                    dispatch.Request.Text));
    }

    private bool RemoveQueuedMessageLocked(string threadId, string messageId)
    {
        var removed = _queue.RemoveMessage(threadId, messageId);
        if (removed)
            ClearLocallyInitiatedIfIdleLocked(threadId);
        return removed;
    }

    private bool CancelQueuedMessageLocked(string threadId, string messageId)
    {
        var canceled = _queue.CancelMessage(threadId, messageId);
        if (canceled)
            ClearLocallyInitiatedIfIdleLocked(threadId);
        return canceled;
    }

    private bool PromoteQueuedMessageLocked(
        string threadId,
        string messageId,
        ChatEntryMetadata? confirmedMeta = null)
    {
        var request = _queue.FindRequest(threadId, messageId);
        if (!_queue.TryTakeForPromotion(threadId, messageId, out var queued))
            return false;

        var current = GetOrCreateTimelineLocked(threadId);
        var entryId = $"e{current.NextId}";
        _timelines[threadId] = ChatTimelineReducer.AddLocalUser(
            current,
            request?.EffectiveTimelineText ?? queued.Text,
            queued.LocalNonce);
        var meta = confirmedMeta is not null && HasGatewayIdentity(confirmedMeta)
            ? confirmedMeta with
            {
                IsLocalQueuedSend = false,
                LocalQueuedMessageId = messageId,
                Attachments = request?.AttachmentPresentations ?? confirmedMeta.Attachments,
            }
            : BuildLiveMetaLocked(
                threadId,
                isLocalQueuedSend: true,
                localQueuedMessageId: messageId,
                attachments: request?.AttachmentPresentations);
        var queuedMeta = GetOrCreateThreadMetaLocked(threadId);
        _entryMeta[threadId] = queuedMeta.SetItem(entryId, meta);
        return true;
    }

    private void ClearLocallyInitiatedIfIdleLocked(string threadId)
    {
        _queue.ClearLocallyInitiatedIfIdle(
            threadId,
            _lifecycle.HasActiveRun(threadId),
            _timelines.TryGetValue(threadId, out var timeline) &&
            timeline.TurnActive);
    }

    private static bool HasGatewayIdentity(ChatEntryMetadata metadata) =>
        !string.IsNullOrEmpty(metadata.GatewayMessageId) ||
        metadata.OpenClawSeq is not null;

    internal ChatDataSnapshot ApplyEvent(
        string threadId,
        ChatEvent evt,
        ChatEntryMetadata? metadata,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ApplyEventLocked(
                threadId,
                ChatContentFormatting.TruncateChatEvent(evt),
                metadata);
            return BuildSnapshotLocked(context);
        }
    }

    internal ChatDataSnapshot ClearPendingPermission(
        string threadId,
        string? expectedRequestId,
        ChatPermissionDecision decision,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var timeline = GetOrCreateTimelineLocked(threadId);
            if (expectedRequestId is not null &&
                !string.Equals(
                    timeline.PendingPermission?.RequestId,
                    expectedRequestId,
                    StringComparison.Ordinal))
            {
                return BuildSnapshotLocked(context);
            }
            _timelines[threadId] = ChatTimelineReducer.ResolvePermission(
                timeline,
                expectedRequestId,
                decision);
            return BuildSnapshotLocked(context);
        }
    }

    internal ChatEntryMetadata BuildLiveMetadata(
        string threadId,
        long? tsMs = null,
        string? gatewayMessageId = null,
        int? openClawSeq = null,
        bool isLocalQueuedSend = false,
        string? localQueuedMessageId = null,
        string? openClawKind = null,
        long? compactionTokensBefore = null,
        long? compactionTokensAfter = null,
        IReadOnlyList<ChatAttachmentPresentation>? attachments = null,
        ChatAssistantContentPresentation? assistantContent = null,
        string? gatewayDisplayItemId = null)
    {
        lock (_gate)
        {
            return BuildLiveMetaLocked(
                threadId,
                tsMs,
                gatewayMessageId,
                openClawSeq,
                isLocalQueuedSend,
                localQueuedMessageId,
                openClawKind,
                compactionTokensBefore,
                compactionTokensAfter,
                attachments,
                assistantContent,
                gatewayDisplayItemId: gatewayDisplayItemId);
        }
    }

    internal bool IsLateNonFinalAssistantFrame(string threadId)
    {
        lock (_gate)
        {
            if (!_timelines.TryGetValue(threadId, out var timeline) ||
                timeline.TurnActive)
            {
                return false;
            }
            for (var i = timeline.Entries.Count - 1; i >= 0; i--)
            {
                var entry = timeline.Entries[i];
                if (entry.Kind == ChatTimelineItemKind.User)
                    return false;
                if (entry.Kind == ChatTimelineItemKind.Assistant)
                    return !entry.IsStreaming;
            }
            return false;
        }
    }

    internal ChatAbortStart BeginAbort(string threadId)
    {
        lock (_gate)
        {
            var hadActiveTurn = _timelines.TryGetValue(threadId, out var timeline) &&
                                timeline.TurnActive;
            return _lifecycle.BeginAbort(threadId, hadActiveTurn);
        }
    }

    internal void RollbackAbort(string threadId, string runId)
    {
        lock (_gate)
        {
            _lifecycle.RollbackAbort(threadId, runId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
        }
    }

    internal ChatDataSnapshot? RollbackAbortAndEndTurnIfCurrent(
        string threadId,
        string runId,
        ChatRuntimeGeneration expectedGeneration,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed ||
                CurrentRuntimeGenerationLocked(threadId) != expectedGeneration ||
                !_lifecycle.TryGetActiveRun(threadId, out var activeRunId) ||
                !string.Equals(activeRunId, runId, StringComparison.Ordinal))
            {
                return null;
            }

            _lifecycle.RollbackAbort(threadId, runId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
            ApplyEventLocked(
                threadId,
                new ChatTurnEndEvent(),
                metadata: null);
            return BuildSnapshotLocked(context);
        }
    }

    internal void CompleteAbort(string threadId, string? runId)
    {
        lock (_gate)
        {
            _lifecycle.CompleteAbort(threadId, runId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
        }
    }

    internal bool ShouldSuppressChatMessage(string threadId)
    {
        lock (_gate)
            return _lifecycle.IsThreadSuppressed(threadId);
    }

    internal ChatResetTransition ResetThread(
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var oldSessionId = _history.ClearSessionForReset(threadId);

            var submittedRunIds = new HashSet<string>(StringComparer.Ordinal);
            if (_lifecycle.ActiveRunForReset(threadId) is { Length: > 0 } activeRunId)
            {
                submittedRunIds.Add(activeRunId);
            }

            foreach (var runId in _queue.RunIdsForThread(threadId))
                submittedRunIds.Add(runId);
            foreach (var localEcho in _queue.SnapshotLocalEchoes(threadId))
            {
                _reset.AddSubmittedLocalEcho(
                    threadId,
                    localEcho.Text,
                    localEcho.SentAt);
            }

            var generation = _reset.BeginReset(
                threadId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _timelines[threadId] = ChatTimelineState.Initial() with
            {
                HistoryLoaded = true,
            };
            _entryMeta.Remove(threadId);
            _lifecycle.ClearThreadForReset(threadId);
            _queue.ClearThreadForReset(threadId);
            foreach (var runId in submittedRunIds)
                _reset.AddIgnoredRun(threadId, runId);

            return new(
                BuildSnapshotLocked(context),
                oldSessionId,
                generation,
                threadId,
                submittedRunIds.ToArray());
        }
    }

    internal ChatIncomingMessageGate GateIncomingChatMessage(
        ChatMessageInfo message,
        ChatProjectionContext context,
        GatewayMediaMessageProjectionResult? projection = null)
    {
        var threadId = message.SessionKey!;
        var role = message.Role?.ToLowerInvariant() ?? string.Empty;
        var text = projection?.ReconciliationText ?? message.Text ?? string.Empty;
        var attachmentCorrelationSignature = projection?.AttachmentCorrelationSignature ?? "";
        var hasMediaEnvelope = projection?.HasMediaEnvelope ?? false;
        lock (_gate)
        {
            _lifecycle.TryGetActiveRun(
                threadId,
                out var activeRunId);
            var resetGate = _reset.EvaluateChatMessage(
                    threadId,
                    role,
                    text,
                    message.Ts,
                    _queue.HasPendingLocalEchoText(
                        threadId,
                        text,
                        attachmentCorrelationSignature,
                        hasMediaEnvelope),
                    activeRunId);
            var openedLifecycle =
                ApplyBufferedLifecycleOpenLocked(
                    threadId,
                    resetGate.OpenedLifecycleStart,
                    allowRemoteTurn:
                        resetGate.ConsumeEchoText is null);
            if (resetGate.Drop)
            {
                ChatDataSnapshot? snapshot = null;
                if (resetGate.ConsumeEchoText is not null &&
                    _queue.TryConsumeLocalEcho(
                        threadId,
                        resetGate.ConsumeEchoText,
                        attachmentCorrelationSignature,
                        hasMediaEnvelope,
                        out var queuedMessageId))
                {
                    var confirmed = BuildLiveMetaLocked(
                        threadId,
                        message.Ts,
                        message.OpenClawId,
                        message.OpenClawSeq,
                        gatewayDisplayItemId: message.OpenClawDisplayItemId);
                    if (ReconcileQueuedMessageEchoLocked(
                            threadId,
                            queuedMessageId,
                            confirmed))
                    {
                        snapshot = BuildSnapshotLocked(context);
                    }
                }
                return new(
                    true,
                    false,
                    resetGate.RequestRemoteBackfill,
                    snapshot,
                    openedLifecycle,
                    CurrentRuntimeGenerationLocked(threadId));
            }
            return new(
                Drop: false,
                Suppressed: _lifecycle.IsThreadSuppressed(threadId),
                RequestRemoteBackfill: false,
                Snapshot: null,
                openedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    internal ChatLocalEchoTransition ConsumeLocalEcho(
        ChatMessageInfo message,
        bool removeQueuedMessage,
        ChatProjectionContext context,
        GatewayMediaMessageProjectionResult? projection = null)
    {
        var threadId = message.SessionKey!;
        var text = projection?.ReconciliationText ??
            (message.Text ?? string.Empty).Trim();
        var attachmentCorrelationSignature = projection?.AttachmentCorrelationSignature ?? "";
        var hasMediaEnvelope = projection?.HasMediaEnvelope ?? false;
        lock (_gate)
        {
            if (!_queue.TryConsumeLocalEcho(
                    threadId,
                    text,
                    attachmentCorrelationSignature,
                    hasMediaEnvelope,
                    out var queuedMessageId))
            {
                return new(false, null);
            }
            if (removeQueuedMessage)
                RemoveQueuedMessageLocked(threadId, queuedMessageId);
            var confirmed = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                gatewayDisplayItemId: message.OpenClawDisplayItemId);
            return new(
                true,
                !removeQueuedMessage &&
                ReconcileQueuedMessageEchoLocked(
                    threadId,
                    queuedMessageId,
                    confirmed)
                    ? BuildSnapshotLocked(context)
                    : null);
        }
    }

    internal ChatLocalEchoTransition ReconcileExistingLocalQueuedUser(
        ChatMessageInfo message,
        string userText,
        ChatProjectionContext context,
        IReadOnlyList<ChatAttachmentPresentation>? attachments = null,
        string attachmentCorrelationSignature = "",
        bool hasMediaEnvelope = false)
    {
        var threadId = message.SessionKey!;
        lock (_gate)
        {
            var metadata = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                attachments: attachments,
                gatewayDisplayItemId: message.OpenClawDisplayItemId);
            if (TryReconcileExistingLocalQueuedUserEchoLocked(
                    threadId,
                    userText,
                    attachmentCorrelationSignature,
                    hasMediaEnvelope,
                    metadata))
            {
                return new(true, BuildSnapshotLocked(context));
            }

            var remoteSnapshot = ApplyProjectedRemoteUserMessageLocked(
                threadId,
                userText,
                attachmentCorrelationSignature,
                metadata,
                context);
            return new(false, remoteSnapshot);
        }
    }

    // Applies an incoming user message from another client (not a local
    // echo). Gateway retransmits of the same message (e.g. once with a
    // partial media resolve, once final) are merged into the existing
    // timeline row instead of appended as a duplicate, matched first by
    // gateway identity and — for identity-less rows — by same trailing-entry
    // text + attachment signature.
    private ChatDataSnapshot? ApplyProjectedRemoteUserMessageLocked(
        string threadId,
        string projectedText,
        string attachmentCorrelationSignature,
        ChatEntryMetadata incomingMeta,
        ChatProjectionContext context)
    {
        var timeline = GetOrCreateTimelineLocked(threadId);
        var threadMeta = GetOrCreateThreadMetaLocked(threadId);
        ChatTimelineItem? matched = null;
        ChatEntryMetadata? existingMeta = null;

        if (HasGatewayIdentity(incomingMeta))
        {
            for (var i = timeline.Entries.Count - 1; i >= 0; i--)
            {
                var candidate = timeline.Entries[i];
                if (candidate.Kind != ChatTimelineItemKind.User ||
                    !threadMeta.TryGetValue(candidate.Id, out var candidateMeta) ||
                    candidateMeta.IsLocalQueuedSend ||
                    !HasMatchingGatewayIdentity(candidateMeta, incomingMeta))
                {
                    continue;
                }

                matched = candidate;
                existingMeta = candidateMeta;
                break;
            }
        }

        // Identity-less history/live twins are only safe to correlate
        // against the current trailing user row. Crossing an
        // assistant/status boundary would collapse a legitimate later turn
        // that repeats the same prose.
        if (matched is null &&
            timeline.Entries.Count > 0 &&
            timeline.Entries[^1] is { Kind: ChatTimelineItemKind.User } latestUser &&
            string.Equals(latestUser.Text, projectedText, StringComparison.Ordinal) &&
            threadMeta.TryGetValue(latestUser.Id, out var latestMeta) &&
            !latestMeta.IsLocalQueuedSend &&
            !HasConflictingGatewayIdentity(latestMeta, incomingMeta) &&
            string.Equals(
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(
                    latestMeta.Attachments),
                attachmentCorrelationSignature,
                StringComparison.Ordinal))
        {
            matched = latestUser;
            existingMeta = latestMeta;
        }

        if (matched is null || existingMeta is null)
        {
            ApplyEventLocked(
                threadId,
                new ChatUserMessageEvent(projectedText),
                incomingMeta);
            return BuildSnapshotLocked(context);
        }

        var mergedMeta = MergeProjectedUserMetadata(existingMeta, incomingMeta);
        if (mergedMeta == existingMeta)
            return null;

        _entryMeta[threadId] = threadMeta.SetItem(matched.Id, mergedMeta);
        return HasRendererVisibleUserMetadataChange(existingMeta, mergedMeta)
            ? BuildSnapshotLocked(context)
            : null;
    }

    private static bool HasMatchingGatewayIdentity(
        ChatEntryMetadata existing,
        ChatEntryMetadata incoming) =>
        (!string.IsNullOrEmpty(incoming.GatewayMessageId) &&
         string.Equals(
             existing.GatewayMessageId,
             incoming.GatewayMessageId,
             StringComparison.Ordinal)) ||
        (incoming.OpenClawSeq is not null && existing.OpenClawSeq == incoming.OpenClawSeq);

    private static bool HasConflictingGatewayIdentity(
        ChatEntryMetadata existing,
        ChatEntryMetadata incoming) =>
        (!string.IsNullOrEmpty(existing.GatewayMessageId) &&
         !string.IsNullOrEmpty(incoming.GatewayMessageId) &&
         !string.Equals(
             existing.GatewayMessageId,
             incoming.GatewayMessageId,
             StringComparison.Ordinal)) ||
        (existing.OpenClawSeq is not null &&
         incoming.OpenClawSeq is not null &&
         existing.OpenClawSeq != incoming.OpenClawSeq);

    private static ChatEntryMetadata MergeProjectedUserMetadata(
        ChatEntryMetadata existing,
        ChatEntryMetadata incoming)
    {
        var mergedAttachments = existing.Attachments is { Count: > 0 }
            ? existing.Attachments
            : incoming.Attachments;
        return existing with
        {
            Timestamp = existing.Timestamp ?? incoming.Timestamp,
            Model = existing.Model ?? incoming.Model,
            GatewayMessageId = string.IsNullOrEmpty(existing.GatewayMessageId)
                ? incoming.GatewayMessageId
                : existing.GatewayMessageId,
            OpenClawSeq = existing.OpenClawSeq ?? incoming.OpenClawSeq,
            Attachments = mergedAttachments,
        };
    }

    private static bool HasRendererVisibleUserMetadataChange(
        ChatEntryMetadata existing,
        ChatEntryMetadata merged) =>
        existing.Timestamp != merged.Timestamp ||
        !AttachmentPresentationsEqual(existing.Attachments, merged.Attachments);

    private static bool AttachmentPresentationsEqual(
        IReadOnlyList<ChatAttachmentPresentation>? left,
        IReadOnlyList<ChatAttachmentPresentation>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
            return false;
        if (leftCount == 0)
            return true;

        for (var i = 0; i < leftCount; i++)
        {
            if (left![i] != right![i])
                return false;
        }
        return true;
    }

    internal (ChatEntryMetadata Metadata, string? ActiveRunId) BuildMetadataWithRun(
        ChatMessageInfo message)
    {
        lock (_gate)
        {
            var metadata = BuildLiveMetaLocked(
                message.SessionKey!,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                gatewayDisplayItemId: message.OpenClawDisplayItemId);
            _lifecycle.TryGetActiveRun(message.SessionKey!, out var runId);
            return (metadata, runId);
        }
    }

    internal ChatAssistantPreparation PrepareAssistant(
        ChatMessageInfo message,
        string assistantText,
        ChatProjectionContext context,
        ChatAssistantContentPresentation? assistantContent = null)
    {
        var threadId = message.SessionKey!;
        lock (_gate)
        {
            // A frame carrying only structured/legacy media directives (no
            // plain text) has nothing for the identified-duplicate/self-echo
            // classifier to compare against, and it never carries a gateway
            // identity either (media-only frames are synthesized locally
            // from ContentParts, not gateway-sequenced) — so it can't be a
            // resend of an already-rendered turn. Render it directly rather
            // than routing it through text-based classification.
            var disposition = assistantText.Length == 0 &&
                assistantContent is not null &&
                string.IsNullOrEmpty(message.OpenClawId) &&
                message.OpenClawSeq is null
                ? AssistantQueueFrameDisposition.Render
                : ClassifyAssistantQueueFrameLocked(
                    threadId,
                    assistantText,
                    message.OpenClawId,
                    message.OpenClawSeq);
            ChatDataSnapshot? promotionSnapshot = null;
            if (disposition == AssistantQueueFrameDisposition.Render &&
                _queue.IsLocallyInitiated(threadId) &&
                _queue.TryGetSingleSendingMessage(threadId, out var queued) &&
                !_lifecycle.HasActiveRun(threadId) &&
                !_queue.IsAssistantFallbackPromoted(threadId) &&
                PromoteQueuedMessageLocked(threadId, queued.Id))
            {
                promotionSnapshot = BuildSnapshotLocked(context);
            }

            var metadata = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                assistantContent: assistantContent,
                gatewayDisplayItemId: message.OpenClawDisplayItemId);
            var hasUsage = message.InputTokens is not null ||
                           message.OutputTokens is not null ||
                           message.ResponseTokens is not null ||
                           message.ContextPercent is not null;
            if (hasUsage)
            {
                var contextTokens = _presentation.ContextTokensForThread(threadId);
                metadata = metadata with
                {
                    InputTokens = message.InputTokens ?? metadata.InputTokens,
                    OutputTokens = message.OutputTokens ?? metadata.OutputTokens,
                    ResponseTokens = message.ResponseTokens ?? metadata.ResponseTokens,
                    ContextPercent = message.ContextPercent ?? metadata.ContextPercent,
                    ContextTokens = contextTokens is > 0
                        ? contextTokens
                        : metadata.ContextTokens,
                };
            }
            _lifecycle.TryGetActiveRun(threadId, out var activeRunId);
            return new(disposition, promotionSnapshot, metadata, activeRunId);
        }
    }

    internal string? CompleteAssistantFinal(string threadId)
    {
        lock (_gate)
        {
            var completedRunId = _lifecycle.CompleteAssistantFinal(threadId);
            _reset.CompleteRun(threadId, completedRunId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
            return completedRunId;
        }
    }

    internal ChatAgentEventTransition ProcessAgentEvent(
        AgentEventInfo evt,
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var gate = GateAgentEventLocked(evt, threadId);
            if (!gate.Process)
            {
                return new(
                    Process: false,
                    gate.ReloadHistory,
                    gate.DroppedTerminalReason,
                    DeferredAbortRunId: null,
                    DeferredAbortCount: 0,
                    CompletedRunId: null,
                    CompletionPhase: null,
                    FetchRemoteUser: false,
                    AllowRemoteTurn: false,
                    WasAborted: false,
                    Suppressed: false,
                    MappedEvent: null,
                    ToolMetadata: null,
                    Snapshots: [],
                    gate.OpenedLifecycle,
                    CurrentRuntimeGenerationLocked(threadId));
            }

            var run = UpdateRunTrackingLocked(evt, threadId, context);
            var snapshots = new List<ChatDataSnapshot>();
            if (run.Snapshot is not null)
                snapshots.Add(run.Snapshot);

            var suppressed = _lifecycle.ShouldSuppress(threadId, evt.RunId);
            ChatEvent? mapped = null;
            ChatToolMetadataWrite? toolMetadata = null;
            if (!suppressed)
            {
                var mapping = ChatEventMapper.Map(evt);
                mapped = mapping.Event;
                if (mapping.Approval is { } approval &&
                    !_approval.MarkSeen(approval.RequestId, approval.AlternateId))
                {
                    mapped = null;
                }

                if (mapped is not null)
                {
                    ApplyEventLocked(
                        threadId,
                        ChatContentFormatting.TruncateChatEvent(mapped),
                        BuildLiveMetaLocked(
                            threadId,
                            evt.Ts > 0 ? (long)evt.Ts : 0));
                    toolMetadata = BuildToolMetadataWriteLocked(
                        threadId,
                        mapped,
                        evt.Ts > 0 ? (long)evt.Ts : 0);
                    snapshots.Add(BuildSnapshotLocked(context));
                }
                else if (TryResolveTerminalApprovalLocked(evt, threadId))
                {
                    snapshots.Add(BuildSnapshotLocked(context));
                }
            }

            return new(
                Process: true,
                ReloadHistory: false,
                run.DroppedTerminalReason,
                run.DeferredAbortRunId,
                run.DeferredAbortCount,
                run.CompletedRunId,
                run.CompletionPhase,
                run.FetchRemoteUser,
                run.AllowRemoteTurn,
                run.WasAborted,
                suppressed,
                mapped,
                toolMetadata,
                snapshots.ToArray(),
                gate.OpenedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    private ChatToolMetadataWrite? BuildToolMetadataWriteLocked(
        string threadId,
        ChatEvent mapped,
        long timestampMs)
    {
        string toolName;
        string label;
        string? toolCallId;
        System.Text.Json.Nodes.JsonObject? toolArgs;
        ChatToolIdentityStrength identityStrength;
        string? runId;
        switch (mapped)
        {
            case ChatToolStartEvent start
                when !string.IsNullOrWhiteSpace(start.ToolName):
                toolName = start.ToolName;
                label = start.Text;
                toolCallId = start.ToolCallId;
                toolArgs = start.ToolArgs;
                identityStrength = start.IdentityStrength;
                runId = start.RunId;
                break;
            case ChatToolPresentationEvent presentation:
                toolName = presentation.ToolName;
                label = NativeToolProjector.FirstToolDisplayValue(
                    presentation.ToolArgs);
                toolCallId = presentation.ParentToolCallId;
                toolArgs = presentation.ToolArgs;
                identityStrength = presentation.IdentityStrength;
                runId = presentation.RunId;
                break;
            default:
                return null;
        }

        var legacyTurn = ResolveToolCacheLegacyTurnLocked(
            threadId,
            mapped,
            runId,
            toolCallId);
        return new ChatToolMetadataWrite(
            threadId,
            _history.ResolveSessionId(threadId) ?? threadId,
            GetResetVersionLocked(threadId),
            timestampMs,
            toolName,
            label,
            toolCallId,
            toolArgs,
            identityStrength,
            runId,
            legacyTurn);
    }

    private long ResolveToolCacheLegacyTurnLocked(
        string threadId,
        ChatEvent mapped,
        string? runId,
        string? toolCallId)
    {
        if (!string.IsNullOrWhiteSpace(runId))
            return 0;
        if (!_timelines.TryGetValue(threadId, out var timeline))
            return ChatTimelineState.Initial().ToolLegacyTurn;
        if (string.IsNullOrWhiteSpace(toolCallId))
            return timeline.ToolLegacyTurn;

        if (mapped is ChatToolPresentationEvent)
        {
            var pendingKey = timeline.PendingToolPresentations?.Keys
                .Where(key => key.RunId is null &&
                    string.Equals(
                        key.ToolCallId,
                        toolCallId,
                        StringComparison.Ordinal))
                .OrderByDescending(key => key.LegacyTurn)
                .FirstOrDefault();
            if (pendingKey is { ToolCallId.Length: > 0 })
                return pendingKey.Value.LegacyTurn;
        }

        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.ToolCall ||
                entry.ToolRunId is not null)
            {
                continue;
            }
            if (string.Equals(
                    entry.ToolCallId,
                    toolCallId,
                    StringComparison.Ordinal) ||
                entry.ToolCorrelationIds?.Contains(toolCallId) == true)
            {
                return entry.ToolLegacyTurn;
            }
        }
        return timeline.ToolLegacyTurn;
    }

    private ChatAgentEventGate GateAgentEventLocked(
        AgentEventInfo evt,
        string threadId)
    {
        var resetGate = _reset.EvaluateAgentEvent(evt, threadId);
        ChatOpenedLifecycleTransition? openedLifecycle = null;
        if (resetGate.OpenedLifecycleStart is { } openedStart &&
            ChatEventMapper.IsLifecycleStart(openedStart))
        {
            ApplyOpenedResetLifecycleStartLocked(
                threadId,
                openedStart);
        }
        else
        {
            openedLifecycle = ApplyBufferedLifecycleOpenLocked(
                threadId,
                resetGate.OpenedLifecycleStart,
                allowRemoteTurn: false);
        }
        if (resetGate.Drop)
        {
            return new(
                false,
                resetGate.ReloadHistory,
                null,
                openedLifecycle);
        }
        if (ShouldDropTerminalAgentEventLocked(
                evt,
                threadId,
                out var droppedReason))
        {
            return new(
                false,
                false,
                droppedReason,
                openedLifecycle);
        }
        return new(
            true,
            false,
            null,
            openedLifecycle);
    }

    private ChatRunTransition UpdateRunTrackingLocked(
        AgentEventInfo evt,
        string threadId,
        ChatProjectionContext context)
    {
        string? deferredAbortRunId = null;
        var deferredAbortCount = 0;
        ChatTerminalEventDropReason? droppedReason = null;
        string? completionPhase = null;
        var fetchRemoteUser = false;
        var allowRemoteTurn = false;
        var wasAborted = false;
        ChatDataSnapshot? snapshot = null;

        if (string.Equals(
                    evt.Stream,
                    "lifecycle",
                    StringComparison.OrdinalIgnoreCase) &&
                evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                evt.Data.TryGetProperty("phase", out var phaseProperty))
            {
                var phase = phaseProperty.GetString()?.ToLowerInvariant();
                if (phase == "start")
                {
                    allowRemoteTurn =
                        !_queue.IsLocallyInitiated(threadId) &&
                        !_lifecycle.IsThreadSuppressed(threadId) &&
                        !_lifecycle.HasPendingAbort(threadId);
                    if (!string.IsNullOrEmpty(evt.RunId))
                    {
                        _lifecycle.StartRun(threadId, evt.RunId);
                        fetchRemoteUser = !_queue.IsLocallyInitiated(threadId);
                        var pendingCount =
                            _lifecycle.TakePendingAbortCount(threadId);
                        if (pendingCount > 0)
                        {
                            _lifecycle.MarkDeferredAbort(
                                threadId,
                                evt.RunId);
                            deferredAbortRunId = evt.RunId;
                            deferredAbortCount = pendingCount;
                        }
                    }
                    if (TryPromoteQueuedMessageOnLocalTurnStartLocked(evt, threadId))
                        snapshot = BuildSnapshotLocked(context);
                }
                else if (phase is "end" or "error")
                {
                    completionPhase = phase;
                    wasAborted = _lifecycle.IsRunAborted(evt.RunId);
                    _reset.CompleteRun(threadId, evt.RunId);
                    _lifecycle.RemoveAbortedRun(evt.RunId);
                    _lifecycle.RemoveActiveRun(threadId);
                    _lifecycle.ClearThreadSuppression(threadId);
                    _queue.RemoveRunMappingByRunId(threadId, evt.RunId);
                    if (!_queue.HasPendingMessages(threadId))
                        _queue.ClearLocallyInitiated(threadId);
                    var pendingCount =
                        _lifecycle.TakePendingAbortCount(threadId);
                    if (pendingCount > 0)
                    {
                        deferredAbortRunId = evt.RunId;
                        deferredAbortCount = pendingCount;
                    }
                }
            }
            else if (string.Equals(
                         evt.Stream,
                         "job",
                         StringComparison.OrdinalIgnoreCase) &&
                     evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     evt.Data.TryGetProperty("state", out var stateProperty))
            {
                var phase = stateProperty.GetString()?.ToLowerInvariant();
                if (phase is "done" or "error")
                {
                    completionPhase = phase == "done" ? "end" : "error";
                    wasAborted = _lifecycle.IsRunAborted(evt.RunId);
                    _reset.CompleteRun(threadId, evt.RunId);
                    if (!string.IsNullOrWhiteSpace(evt.RunId))
                    {
                        _lifecycle.RemoveAbortedRun(evt.RunId);
                        _queue.RemoveRunMappingByRunId(threadId, evt.RunId);
                    }
                    _lifecycle.RemoveActiveRun(threadId);
                }
            }

        return new(
            deferredAbortRunId,
            deferredAbortCount,
            droppedReason,
            evt.RunId,
            completionPhase,
            fetchRemoteUser,
            allowRemoteTurn,
            wasAborted,
            snapshot);
    }

    private bool TryResolveTerminalApprovalLocked(
        AgentEventInfo evt,
        string threadId)
    {
        var terminal = ChatEventMapper.MapTerminalApproval(evt);
        if (terminal is null)
            return false;
        var timeline = GetOrCreateTimelineLocked(threadId);
        var pendingId = timeline.PendingPermission?.RequestId;
        if (pendingId is null ||
            !_approval.Matches(
                pendingId,
                terminal.ApprovalSlug,
                terminal.ApprovalId))
        {
            return false;
        }
        _timelines[threadId] = ChatTimelineReducer.ResolvePermission(
            timeline,
            pendingId,
            ChatEventMapper.MapTerminalApprovalDecision(
                terminal.Phase,
                terminal.Decision));
        return true;
    }

    internal void CompleteRemoteBackfill(string threadId)
    {
        lock (_gate)
            _reset.CompleteRemoteBackfill(threadId);
    }

    internal ChatRemoteUserBackfillTransition? ApplyRemoteUserBackfill(
        string threadId,
        ChatMessageInfo message,
        GatewayMediaMessageProjectionResult projection,
        IReadOnlyList<ChatAttachmentPresentation> attachments,
        long expectedResetGeneration,
        bool openResetGate,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (GetResetVersionLocked(threadId) != expectedResetGeneration ||
                _reset.IsPreResetTimestamp(threadId, message.Ts))
            {
                return null;
            }
            var openedLifecycle = openResetGate
                ? ApplyBufferedLifecycleOpenLocked(
                    threadId,
                    _reset.RecordRemoteUser(threadId),
                    allowRemoteTurn: true)
                : null;
            var metadata = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                attachments: attachments,
                gatewayDisplayItemId: message.OpenClawDisplayItemId);
            var snapshot = ApplyProjectedRemoteUserMessageLocked(
                threadId,
                ChatContentFormatting.TruncateForChatEntry(
                    projection.HasMediaEnvelope
                        ? projection.ReconciliationText
                        : ChatMetadataStore.EscapeUntrustedAttachmentMarkerLines(
                            message.Text)),
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(
                    attachments),
                metadata,
                context);
            if (snapshot is null && openedLifecycle is null)
                return null;
            return new(
                snapshot ?? BuildSnapshotLocked(context),
                openedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    private bool TryPromoteQueuedMessageOnLocalTurnStartLocked(
        AgentEventInfo evt,
        string threadId)
    {
        if (!_queue.IsLocallyInitiated(threadId))
            return false;
        if (!string.IsNullOrEmpty(evt.RunId) &&
            _queue.TryResolveMessageForRun(
                threadId,
                evt.RunId,
                out var messageId))
        {
            return PromoteQueuedMessageLocked(threadId, messageId);
        }
        return string.IsNullOrEmpty(evt.RunId) &&
               _queue.TryGetSingleSendingMessage(threadId, out var queued) &&
               PromoteQueuedMessageLocked(threadId, queued.Id);
    }

    private bool ShouldDropTerminalAgentEventLocked(
        AgentEventInfo evt,
        string threadId,
        out ChatTerminalEventDropReason? droppedReason)
    {
        droppedReason = null;
        if (!TryGetTerminalAgentRunId(evt, out var runId))
            return false;
        if (string.IsNullOrWhiteSpace(runId))
        {
            droppedReason = ChatTerminalEventDropReason.MissingRunId;
            return true;
        }
        return _lifecycle.ShouldDropTerminal(
            threadId,
            runId,
            _queue.RunIdsForThread(threadId),
            _timelines.TryGetValue(threadId, out var timeline) &&
            timeline.TurnActive,
            out droppedReason);
    }

    private static bool TryGetTerminalAgentRunId(
        AgentEventInfo evt,
        out string runId)
    {
        runId = evt.RunId ?? string.Empty;
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;
        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("phase", out var phase))
        {
            var value = phase.GetString();
            return string.Equals(value, "end", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("state", out var state))
        {
            var value = state.GetString();
            return string.Equals(value, "done", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    internal ChatDataSnapshot? SnapshotLatestAssistantUsage(
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var session = _presentation.ResolveSessionForThread(
                threadId,
                context.MainSessionKey);
            return session is not null &&
                   SnapshotLatestAssistantUsageLocked(session, threadId)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    internal ChatDataSnapshot? SnapshotAssistantUsageContribution(
        string threadId,
        ChatEntryMetadata metadata,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            return SnapshotAssistantUsageContributionLocked(threadId, metadata)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    private bool SnapshotAssistantUsageContributionLocked(
        string threadId,
        ChatEntryMetadata metadata)
    {
        var currentUsage = UsageValue(metadata);
        if (currentUsage is null || currentUsage <= 0 ||
            !_timelines.TryGetValue(threadId, out var timeline))
        {
            return false;
        }
        var contextTokens = metadata.ContextTokens;
        if (contextTokens is null || contextTokens <= 0)
            contextTokens = _presentation.ContextTokensForThread(threadId);
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;
            var threadMetadata = GetOrCreateThreadMetaLocked(threadId);
            threadMetadata.TryGetValue(entry.Id, out var existing);
            var previousUsage = LatestAssistantUsageBeforeLocked(
                timeline,
                threadMetadata,
                i);
            var cumulative = Math.Max(
                (previousUsage ?? 0) + currentUsage.Value,
                existing?.ResponseTokens ?? 0);
            if (existing?.ResponseTokens == cumulative &&
                existing.UsageContributionTokens == currentUsage &&
                existing.ContextTokens == contextTokens)
            {
                return false;
            }
            threadMetadata = threadMetadata.SetItem(entry.Id, (existing ?? BuildLiveMetaLocked(threadId)) with
            {
                InputTokens = metadata.InputTokens ?? existing?.InputTokens,
                OutputTokens = metadata.OutputTokens ?? existing?.OutputTokens,
                ResponseTokens = cumulative,
                ContextPercent = metadata.ContextPercent ?? existing?.ContextPercent,
                ContextTokens = contextTokens ?? existing?.ContextTokens,
                UsageContributionTokens = currentUsage,
            });
            _entryMeta[threadId] = threadMetadata;   // publish the new immutable snapshot (COW)
            BumpUsageRevisionLocked(threadId);   // metadata mutation, atomically with the write
            BumpRetainedVersionLocked(threadId); // metadata-only writes must invalidate the merge snapshot
            return true;
        }
        return false;
    }

    private static int? LatestAssistantUsageBeforeLocked(
        ChatTimelineState timeline,
        IReadOnlyDictionary<string, ChatEntryMetadata> metadata,
        int beforeIndex)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant ||
                !metadata.TryGetValue(entry.Id, out var entryMetadata))
            {
                continue;
            }
            var value = UsageValue(entryMetadata);
            if (value is > 0)
                return value;
        }
        return null;
    }

    private static int? UsageValue(ChatEntryMetadata metadata) =>
        metadata.ResponseTokens ??
        (metadata.InputTokens is { } input &&
         metadata.OutputTokens is { } output
            ? input + output
            : null);

    private bool TryReconcileExistingLocalQueuedUserEchoLocked(
        string threadId,
        string text,
        string attachmentCorrelationSignature,
        bool hasMediaEnvelope,
        ChatEntryMetadata confirmed)
    {
        if (!HasGatewayIdentity(confirmed) ||
            !_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }

        var candidates = new List<ChatTimelineItem>();
        var echoCandidates = new List<ChatPendingEchoCandidate>();
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.User ||
                !metadata.TryGetValue(entry.Id, out var existing) ||
                !existing.IsLocalQueuedSend ||
                !IsFreshLocalQueuedPromotion(existing, confirmed))
            {
                continue;
            }
            candidates.Add(entry);
            echoCandidates.Add(new ChatPendingEchoCandidate(
                entry.Id,
                entry.Text,
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(
                    existing.Attachments)));
        }

        var matchedMessageId = ChatAttachmentEchoCorrelation.SelectMatchingMessageId(
            echoCandidates,
            text,
            attachmentCorrelationSignature,
            hasMediaEnvelope);
        if (matchedMessageId is null)
            return false;

        var matched = candidates.First(candidate =>
            string.Equals(candidate.Id, matchedMessageId, StringComparison.Ordinal));
        var matchedMeta = metadata[matched.Id];
        _entryMeta[threadId] = metadata.SetItem(matched.Id, confirmed with
        {
            IsLocalQueuedSend = false,
            LocalQueuedMessageId = matchedMeta.LocalQueuedMessageId,
            Attachments = matchedMeta.Attachments,
        });
        return true;
    }

    private static bool IsFreshLocalQueuedPromotion(
        ChatEntryMetadata existing,
        ChatEntryMetadata confirmed)
    {
        if (existing.Timestamp is not { } existingTimestamp)
            return false;
        return confirmed.Timestamp is { } confirmedTimestamp
            ? (confirmedTimestamp - existingTimestamp).Duration() <=
              LocalEchoSuppressionWindow
            : DateTimeOffset.Now - existingTimestamp <= LocalEchoSuppressionWindow;
    }

    private bool ReconcileQueuedMessageEchoLocked(
        string threadId,
        string messageId,
        ChatEntryMetadata confirmed)
    {
        if (PromoteQueuedMessageLocked(threadId, messageId, confirmed))
            return true;
        if (!HasGatewayIdentity(confirmed) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }
        var match = metadata.FirstOrDefault(pair =>
            string.Equals(
                pair.Value.LocalQueuedMessageId,
                messageId,
                StringComparison.Ordinal));
        if (string.IsNullOrEmpty(match.Key))
            return false;
        _entryMeta[threadId] = metadata.SetItem(match.Key, confirmed with
        {
            IsLocalQueuedSend = false,
            LocalQueuedMessageId = messageId,
            Attachments = match.Value.Attachments,
        });
        return true;
    }

    private AssistantQueueFrameDisposition ClassifyAssistantQueueFrameLocked(
        string threadId,
        string assistantText,
        string? gatewayMessageId,
        int? openClawSeq)
    {
        if ((!string.IsNullOrEmpty(gatewayMessageId) || openClawSeq is not null) &&
            IsIdentifiedCompletedAssistantDuplicateLocked(
                threadId,
                assistantText,
                gatewayMessageId,
                openClawSeq))
        {
            return AssistantQueueFrameDisposition.Drop;
        }
        if (string.IsNullOrEmpty(gatewayMessageId) &&
            openClawSeq is null &&
            IsIdentitylessAssistantRetransmitAcrossLocalUserBoundaryLocked(
                threadId,
                assistantText))
        {
            return AssistantQueueFrameDisposition.Drop;
        }
        if (!_queue.IsLocallyInitiated(threadId) ||
            !_queue.TryGetSingleSendingMessage(threadId, out _) ||
            _lifecycle.HasActiveRun(threadId) ||
            _queue.IsAssistantFallbackPromoted(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline))
        {
            return AssistantQueueFrameDisposition.Render;
        }
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;
            if (entry.IsStreaming ||
                !string.Equals(entry.Text, assistantText, StringComparison.Ordinal))
            {
                return AssistantQueueFrameDisposition.Render;
            }
            if (string.IsNullOrEmpty(gatewayMessageId) && openClawSeq is null)
                return AssistantQueueFrameDisposition.Drop;
            if (!_entryMeta.TryGetValue(threadId, out var metadata) ||
                !metadata.TryGetValue(entry.Id, out var existing))
            {
                return AssistantQueueFrameDisposition.Render;
            }
            var sameIdentity =
                !string.IsNullOrEmpty(gatewayMessageId) &&
                string.Equals(
                    existing.GatewayMessageId,
                    gatewayMessageId,
                    StringComparison.Ordinal) ||
                openClawSeq is not null && existing.OpenClawSeq == openClawSeq;
            return sameIdentity
                ? AssistantQueueFrameDisposition.Drop
                : AssistantQueueFrameDisposition.Render;
        }
        return AssistantQueueFrameDisposition.Render;
    }

    private bool IsIdentitylessAssistantRetransmitAcrossLocalUserBoundaryLocked(
        string threadId,
        string assistantText)
    {
        if (!_queue.IsLocallyInitiated(threadId) ||
            _lifecycle.HasActiveRun(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }
        var sawBoundary = false;
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (!sawBoundary)
            {
                if (entry.Kind == ChatTimelineItemKind.Assistant)
                    return false;
                if (entry.Kind == ChatTimelineItemKind.User &&
                    metadata.TryGetValue(entry.Id, out var entryMetadata) &&
                    entryMetadata.IsLocalQueuedSend)
                {
                    sawBoundary = true;
                }
                continue;
            }
            if (entry.Kind == ChatTimelineItemKind.Assistant)
            {
                return !entry.IsStreaming &&
                       string.Equals(
                           entry.Text,
                           assistantText,
                           StringComparison.Ordinal);
            }
            if (entry.Kind == ChatTimelineItemKind.User)
                return false;
        }
        return false;
    }

    private bool IsIdentifiedCompletedAssistantDuplicateLocked(
        string threadId,
        string assistantText,
        string? gatewayMessageId,
        int? openClawSeq)
    {
        if (!_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant ||
                entry.IsStreaming ||
                !metadata.TryGetValue(entry.Id, out var existing))
            {
                continue;
            }
            var bothHaveIds = !string.IsNullOrEmpty(gatewayMessageId) &&
                              !string.IsNullOrEmpty(existing.GatewayMessageId);
            if (bothHaveIds &&
                string.Equals(
                    existing.GatewayMessageId,
                    gatewayMessageId,
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (!bothHaveIds &&
                openClawSeq is not null &&
                existing.OpenClawSeq == openClawSeq &&
                string.Equals(entry.Text, assistantText, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(gatewayMessageId) &&
                    string.IsNullOrEmpty(existing.GatewayMessageId))
                {
                    _entryMeta[threadId] = metadata.SetItem(entry.Id, existing with
                    {
                        GatewayMessageId = gatewayMessageId,
                    });
                }
                return true;
            }
        }
        return false;
    }

    private void ApplyEventLocked(
        string threadId,
        ChatEvent evt,
        ChatEntryMetadata? metadata)
    {
        // ANY ingest can change retained content/metadata: bump the exact merge version.
        BumpRetainedVersionLocked(threadId);
        var current = GetOrCreateTimelineLocked(threadId);
        var currentCount = current.Entries.Count;
        var next = ChatTimelineReducer.Apply(current, evt);
        _timelines[threadId] = next;
        if (metadata is null)
            return;
        var threadMetadata = GetOrCreateThreadMetaLocked(threadId);
        // BOUNDED ingest bookkeeping: only entries APPENDED by this event can be new, so no id HashSet and
        // no full retained scan under the lock.
        var assignedNewEntry = false;
        for (var i = currentCount; i < next.Entries.Count; i++)
        {
            var appended = next.Entries[i];
            if (!threadMetadata.ContainsKey(appended.Id))
            {
                threadMetadata = threadMetadata.SetItem(appended.Id, metadata);
                assignedNewEntry = true;
            }
        }
        // Selective invalidation: ONLY a newly ingested entry can change the usage topology. A streaming
        // reconcile of an existing entry with UNCHANGED metadata must not reset a completed usage scan.
        if (assignedNewEntry)
        {
            _entryMeta[threadId] = threadMetadata;   // publish the new immutable snapshot (COW)
            BumpUsageRevisionLocked(threadId);
        }

        // Streaming assistant frames reconcile into the SAME entry id
        // (see ChatTimelineReducer.UpsertAssistant), so the new-entry-only
        // assignment above never touches it again after creation. Assistant
        // structured media content can still refine across frames (e.g. a
        // legacy directive resolved to a structured reference on a later
        // frame), so merge it into the already-existing reconciled entry's
        // metadata explicitly.
        if (metadata.AssistantContent is not null)
        {
            for (var i = next.Entries.Count - 1; i >= 0; i--)
            {
                var entry = next.Entries[i];
                if (entry.Kind == ChatTimelineItemKind.User)
                    break;
                if (entry.Kind != ChatTimelineItemKind.Assistant)
                    continue;

                if (i < currentCount &&
                    threadMetadata.TryGetValue(entry.Id, out var existingEntryMeta))
                {
                    var mergedContent = ChatAssistantContentProjector.MergeLiveUpdate(
                        existingEntryMeta.AssistantContent,
                        metadata.AssistantContent);
                    if (!ReferenceEquals(mergedContent, existingEntryMeta.AssistantContent))
                    {
                        threadMetadata = threadMetadata.SetItem(
                            entry.Id,
                            existingEntryMeta with { AssistantContent = mergedContent });
                        _entryMeta[threadId] = threadMetadata;   // publish the new immutable snapshot (COW)
                    }
                }
                break;
            }
        }
    }

    private System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>
        GetOrCreateThreadMetaLocked(string threadId)
    {
        if (!_entryMeta.TryGetValue(threadId, out var metadata))
        {
            metadata = System.Collections.Immutable.ImmutableDictionary<string, ChatEntryMetadata>
                .Empty.WithComparers(StringComparer.Ordinal);
            _entryMeta[threadId] = metadata;
        }
        return metadata;
    }

    private ChatEntryMetadata BuildLiveMetaLocked(
        string threadId,
        long? tsMs = null,
        string? gatewayMessageId = null,
        int? openClawSeq = null,
        bool isLocalQueuedSend = false,
        string? localQueuedMessageId = null,
        string? openClawKind = null,
        long? compactionTokensBefore = null,
        long? compactionTokensAfter = null,
        IReadOnlyList<ChatAttachmentPresentation>? attachments = null,
        ChatAssistantContentPresentation? assistantContent = null,
        // Optional PROJECTED/DISPLAY item identity from the live gateway message, carried separately from
        // the raw GatewayMessageId. Appended LAST so existing positional callers are unaffected.
        string? gatewayDisplayItemId = null)
    {
        var timestamp = tsMs is { } value && value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime()
            : DateTimeOffset.Now;
        return new ChatEntryMetadata(
            timestamp,
            _presentation.ModelForThread(threadId),
            GatewayMessageId: gatewayMessageId,
            OpenClawSeq: openClawSeq,
            GatewayDisplayItemId: gatewayDisplayItemId,
            OpenClawKind: openClawKind,
            CompactionTokensBefore: compactionTokensBefore,
            CompactionTokensAfter: compactionTokensAfter,
            IsLocalQueuedSend: isLocalQueuedSend,
            LocalQueuedMessageId: localQueuedMessageId,
            Attachments: attachments,
            AssistantContent: assistantContent);
    }

    private ChatOpenedLifecycleTransition? AddResetAcceptedRunIdLocked(
        string threadId,
        string runId)
    {
        return ApplyBufferedLifecycleOpenLocked(
            threadId,
            _reset.AddAcceptedRun(threadId, runId),
            allowRemoteTurn: false);
    }

    private ChatOpenedLifecycleTransition? ApplyBufferedLifecycleOpenLocked(
        string threadId,
        AgentEventInfo? lifecycleStart,
        bool allowRemoteTurn)
    {
        if (string.IsNullOrEmpty(lifecycleStart?.RunId))
            return null;

        _lifecycle.StartRun(threadId, lifecycleStart.RunId);
        var deferredAbortCount =
            _lifecycle.TakePendingAbortCount(threadId);
        string? deferredAbortRunId = null;
        if (deferredAbortCount > 0)
        {
            deferredAbortRunId = lifecycleStart.RunId;
            _lifecycle.MarkDeferredAbort(
                threadId,
                deferredAbortRunId);
        }
        return new(
            lifecycleStart,
            allowRemoteTurn && deferredAbortCount == 0,
            deferredAbortRunId,
            deferredAbortCount);
    }

    private void ApplyOpenedResetLifecycleStartLocked(
        string threadId,
        AgentEventInfo? lifecycleStart)
    {
        if (!string.IsNullOrEmpty(lifecycleStart?.RunId))
            _lifecycle.StartRun(threadId, lifecycleStart.RunId);
    }

    private ChatDataSnapshot BuildSnapshotLocked(ChatProjectionContext context) =>
        ChatSnapshotProjector.Project(CaptureProjectionInputLocked(context));

    private ChatSnapshotProjectionInput CaptureProjectionInputLocked(
        ChatProjectionContext context) =>
        _presentation.CaptureProjectionInput(
            timelines: new Dictionary<string, ChatTimelineState>(_timelines),
            timelineGenerations: _reset.SnapshotVersions(),
            historyRevisions: _history.SnapshotRevisions(),
            queuedMessages: _queue.SnapshotMessages(),
            acceptedSessionIds: _history.SnapshotAcceptedTranscriptIds(),
            status: _status,
            context);

    private ChatTimelineState GetOrCreateTimelineLocked(string threadId)
    {
        if (!_timelines.TryGetValue(threadId, out var current))
        {
            current = ChatTimelineState.Initial();
            _timelines[threadId] = current;
        }
        return current;
    }

    private void EnsureTimelinesForSessionsLocked()
    {
        foreach (var session in _presentation.SessionSnapshot())
        {
            if (!string.IsNullOrEmpty(session.Key) &&
                !_timelines.ContainsKey(session.Key))
            {
                _timelines[session.Key] = ChatTimelineState.Initial();
            }
        }
    }

    private long GetResetVersionLocked(string threadId) =>
        _reset.GetVersion(threadId);

    internal bool IsRuntimeGenerationCurrent(
        string threadId,
        ChatRuntimeGeneration generation)
    {
        lock (_gate)
        {
            return !_disposed &&
                   _history.ConnectionGeneration == generation.ConnectionGeneration &&
                   GetResetVersionLocked(threadId) == generation.ResetGeneration;
        }
    }

    private ChatRuntimeGeneration CurrentRuntimeGenerationLocked(string threadId) =>
        new(
            _history.ConnectionGeneration,
            GetResetVersionLocked(threadId));
}
