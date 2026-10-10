using OpenClaw.Chat;
using OpenClaw.Shared;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed record ChatHistoryLoadResult(
    ChatHistoryCommitToken Token,
    bool PublishSnapshot,
    ChatProviderNotification? Notification = null,
    // The FULL ORIGINAL window lease this result was produced against (captured when the request began).
    // Required to fence an exhausted/error notification at delivery time: a same-cursor refresh keeps the
    // generation token but replaces the window revision, so the token alone cannot fence the notification.
    ChatHistoryWindowLease? Lease = null);

/// <summary>
/// Owns history request lifetime, in-flight coalescing, cancellation, retries,
/// and immutable transcript rebuild plans. Conversation state alone accepts
/// or rejects plans against its authoritative generation/reset token.
/// </summary>
internal sealed class ChatHistoryLoader : IDisposable
{
    private readonly record struct PendingReload(
        ChatHistoryCommitToken Token,
        bool Replacement);

    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    // ONE bounded initial page: explicit limit + maxBytes + offset 0 (the tail). The initial bounded
    // tail is never treated as full authoritative complete history; completeness rides on the window
    // committed atomically with the accepted timeline, and a later LoadOlder walks older pages.
    // Never fetch all pages upfront.
    internal const int InitialHistoryPageLimit = 200;
    internal const int InitialHistoryPageMaxBytes = 512 * 1024;

    private readonly object _gate = new();
    private readonly IChatGatewayBridge _bridge;
    private readonly ChatConversationState _state;
    private readonly ChatMetadataStore _metadata;
    private readonly ChatStatePersistence _persistence;
    private readonly ChatTelemetryTracker _telemetry;
    private readonly Func<TimeSpan, CancellationToken, Func<Task>, Task> _retryScheduler;
    private readonly Action? _failureReservedForTesting;
    private readonly Dictionary<string, long> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatHistoryCommitToken> _authoritativePending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatHistoryCommitToken> _replacementPending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<ChatHistoryCommitToken, int> _retryCounts = new();
    private CancellationTokenSource _generationCancellation = new();
    private long _appliedStateGeneration;
    private long _requestSequence;
    private bool _disposed;
    private readonly HashSet<string> _olderInFlight = new(StringComparer.Ordinal);

    internal ChatHistoryLoader(
        IChatGatewayBridge bridge,
        ChatConversationState state,
        ChatMetadataStore metadata,
        ChatStatePersistence persistence,
        ChatTelemetryTracker telemetry,
        Func<TimeSpan, CancellationToken, Func<Task>, Task>? retryScheduler = null,
        Action? failureReservedForTesting = null)
    {
        _bridge = bridge;
        _state = state;
        _metadata = metadata;
        _persistence = persistence;
        _telemetry = telemetry;
        _retryScheduler = retryScheduler ?? (static async (delay, token, retry) =>
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            await retry().ConfigureAwait(false);
        });
        _failureReservedForTesting = failureReservedForTesting;
    }

    internal event EventHandler<ChatHistoryLoadResult>? Completed;

    /// <summary>
    /// Narrowly scoped test seam: awaited at the start of the reconstruction worker so a test can
    /// deterministically hold actual reconstruction, advance/reset the generation, then release.
    /// Null in production.
    /// </summary>
    internal Func<Task>? TestReconstructionBarrier { get; set; }

    /// <summary>
    /// Production seam invoked AFTER a tentative older-page reconstruction and immediately BEFORE the
    /// compare-and-commit, so tests can deterministically introduce a conflict at that exact point.
    /// Null in production.
    /// </summary>
    internal Action? BeforeOlderMergeCommitForTests { get; set; }

    internal Task LoadAsync(
        string threadId,
        bool force = false,
        CancellationToken cancellationToken = default,
        bool authoritative = false,
        ChatHistoryCommitToken? expectedToken = null) =>
        LoadCoreAsync(
            threadId,
            force,
            cancellationToken,
            authoritative,
            expectedToken,
            replacement: false,
            supersedeReplacement: false);

    internal Task LoadReplacementAsync(
        string threadId,
        ChatHistoryCommitToken token,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;
            _authoritativePending.Remove(threadId);
            _replacementPending.Remove(threadId);
            foreach (var retryToken in _retryCounts.Keys
                         .Where(candidate => string.Equals(
                             candidate.ThreadId,
                             threadId,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                _retryCounts.Remove(retryToken);
            }
        }
        return LoadCoreAsync(
            threadId,
            force: true,
            cancellationToken,
            authoritative: false,
            expectedToken: token,
            replacement: true,
            supersedeReplacement: true);
    }

    /// <summary>
    /// Loads ONE older bounded page for a thread using the accepted window's server-supplied
    /// nextOffset (not rows.Count arithmetic). No eager loop and no unbounded fallback: a missing or
    /// exhausted window is a no-op. The accepted window is committed atomically by the state.
    /// </summary>
    internal async Task LoadOlderAsync(string threadId, CancellationToken cancellationToken = default)
    {
        CancellationToken generationToken;
        lock (_gate)
        {
            if (_disposed || !_olderInFlight.Add(threadId))
                return;   // single-flight per thread
            generationToken = _generationCancellation.Token;
        }

        try
        {
            if (!_state.TryCaptureOlderMergeLease(threadId, out var lease, out var retainedLeaseVersion))
                return;
            if (!lease.Window.HasMore || lease.Window.NextOffset is not int nextOffset)
                return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, generationToken);
            // Entry check: a pre-cancelled request must not run ANY reconstruction stage (not even the
            // fetch), so cancellation is observed before real work begins.
            linked.Token.ThrowIfCancellationRequested();
            var model = _state.ModelForThread(threadId);
            var page = await _bridge.RequestChatHistoryPageAsync(
                    threadId,
                    new ChatHistoryPageOptions(
                        Limit: InitialHistoryPageLimit,
                        MaxBytes: InitialHistoryPageMaxBytes,
                        Offset: nextOffset),
                    linked.Token)
                .WaitAsync(linked.Token)
                .ConfigureAwait(false);

            if (_disposed || linked.IsCancellationRequested ||
                !_state.IsHistoryRequestCurrent(lease.Token))
                return;

            var history = new ChatHistoryInfo
            {
                SessionKey = page.SessionKey,
                SessionId = page.SessionId,
                Messages = page.Messages,
            };
            var windowState = new ChatHistoryWindowState(
                page.SessionKey, page.SessionId, page.HasMore, page.NextOffset, page.Total, page.ResponseOffset,
                page.Completeness, page.OmittedCount, page.NormalizedBytes);

            // BOUNDED content-root REBASE: the network page is fetched ONCE and the page plan is built once
            // per attempt; each attempt re-captures only the CHEAP content roots (timeline + immutable
            // metadata references) and reconstructs off-lock, and the commit rejects when a streaming/
            // metadata/permission edit landed in the meantime. Up to 3 attempts; the ORIGINAL network lease
            // stays authoritative throughout.
            // The page plan is INVARIANT across rebase attempts: build it ONCE (outside the loop).
            linked.Token.ThrowIfCancellationRequested();
            var plan = await Task.Run(
                    () => BuildPlanAsync(
                        history,
                        threadId,
                        model,
                        lease.Token.ResetGeneration,
                        linked.Token),
                    linked.Token)
                .ConfigureAwait(false);

            // Bounded content-root rebase: up to 3 reconstructions on the newest cheap roots.
            const int maxAttempts = 3;
            var committed = false;
            var commitToken = lease.Token;
            for (var attempt = 0; attempt < maxAttempts && !committed; attempt++)
            {
                linked.Token.ThrowIfCancellationRequested();   // entry check: a pre-cancelled pass never runs
                if (!_state.TryPrepareOlderMergeRoots(threadId, nextOffset, windowState, lease, out var roots))
                    return;   // original lease no longer authoritative: reject silently (no stale notification)
                commitToken = roots.Lease.Token;

                if (_disposed || linked.IsCancellationRequested)
                    return;

                var tentative = await Task.Run(
                        () =>
                        {
                            var merged = ChatHistoryState.MergeOlderPage(
                                plan, roots.Timeline, roots.Metadata, linked.Token);
                            // Bounded/cancellable immutable map conversion OFF the gate: the commit stays an
                            // O(1) assignment and the final map build is itself cancellable every 64 entries
                            // (no full scan swapped onto the UI thread).
                            return (
                                Timeline: merged.Timeline,
                                Metadata: ChatHistoryState.ToImmutableMetadataCancellable(
                                    merged.Metadata, linked.Token));
                        },
                        linked.Token)
                    .ConfigureAwait(false);

                if (_disposed || linked.IsCancellationRequested)
                    return;

                // Production seam AFTER a completed tentative reconstruction and BEFORE the commit, so a
                // test can deterministically introduce a conflict at exactly this point.
                BeforeOlderMergeCommitForTests?.Invoke();
                linked.Token.ThrowIfCancellationRequested();
                committed = _state.TryCommitPreparedOlderMerge(
                    roots,
                    plan,
                    tentative.Timeline,
                    tentative.Metadata,
                    linked.Token);
            }

            if (!committed)
            {
                // Sustained content churn exhausted the bounded rebase: surface a VISIBLE retry/pending state
                // through the existing notification mechanism instead of silently dropping the page.
                Completed?.Invoke(
                    this,
                    new ChatHistoryLoadResult(
                        commitToken,
                        PublishSnapshot: false,
                        new ChatProviderNotification(
                            ChatProviderNotificationKind.Error,
                            threadId,
                            LocalizationHelper.GetString("Chat_Notification_LoadHistoryFailed"),
                            LocalizationHelper.GetString("Chat_Notification_LoadHistoryFailed")),
                        // FULL LEASE AUTHORITY: the ORIGINAL network lease, not the (possibly refreshed)
                        // per-attempt roots lease. Exhaustion is only deliverable while that exact window
                        // revision is still accepted.
                        Lease: lease));
                return;
            }

            Completed?.Invoke(
                this,
                new ChatHistoryLoadResult(commitToken, PublishSnapshot: true));
        }
        catch (OperationCanceledException)
        {
            // Cancellation/disposal/generation change: held timeline and accepted window untouched.
        }
        finally
        {
            lock (_gate)
                _olderInFlight.Remove(threadId);
        }
    }

    internal ChatStatusTransition ApplyStatusAndAdvanceGeneration(
        ConnectionStatus status,
        ChatProjectionContext context)
    {
        CancellationTokenSource? previous = null;
        ChatStatusTransition transition;
        lock (_gate)
        {
            transition = _state.ApplyStatus(status, context);
            if ((transition.Reconnected || transition.Disconnected) &&
                transition.HistoryGeneration > _appliedStateGeneration)
            {
                _appliedStateGeneration = transition.HistoryGeneration;
                previous = _generationCancellation;
                _generationCancellation = new CancellationTokenSource();
                _inFlight.Clear();
                _authoritativePending.Clear();
                _replacementPending.Clear();
                _retryCounts.Clear();
                _state.ActivateHistoryGeneration(transition.HistoryGeneration);
            }
        }
        previous?.Cancel();
        previous?.Dispose();
        return transition;
    }

    internal void ApplyReset(string threadId, long resetGeneration)
    {
        lock (_gate)
        {
            RemoveOlderPendingReload(
                _authoritativePending,
                threadId,
                resetGeneration);
            RemoveOlderPendingReload(
                _replacementPending,
                threadId,
                resetGeneration);
            foreach (var token in _retryCounts.Keys
                         .Where(candidate => string.Equals(
                             candidate.ThreadId,
                             threadId,
                             StringComparison.Ordinal) &&
                             candidate.ResetGeneration < resetGeneration)
                         .ToArray())
            {
                _retryCounts.Remove(token);
            }
        }
    }

    private static void RemoveOlderPendingReload(
        Dictionary<string, ChatHistoryCommitToken> pending,
        string threadId,
        long resetGeneration)
    {
        if (pending.TryGetValue(threadId, out var token) &&
            token.ResetGeneration < resetGeneration)
        {
            pending.Remove(threadId);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            cancellation = _generationCancellation;
            _inFlight.Clear();
            _authoritativePending.Clear();
            _replacementPending.Clear();
            _retryCounts.Clear();
        }
        cancellation.Cancel();
        cancellation.Dispose();
        Completed = null;
    }

    private async Task LoadCoreAsync(
        string threadId,
        bool force,
        CancellationToken cancellationToken,
        bool authoritative,
        ChatHistoryCommitToken? expectedToken,
        bool replacement,
        bool supersedeReplacement)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId))
            return;

        CancellationToken generationToken;
        long requestId;
        ChatHistoryCommitToken commitToken;
        string? model;
        Task? generationActivation;
        bool canBegin;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (expectedToken is { } expected &&
                !_state.IsHistoryRequestCurrent(expected))
            {
                return;
            }
            if (_inFlight.ContainsKey(threadId))
            {
                if (replacement)
                {
                    var replacementToken = expectedToken ??
                        _state.CaptureHistoryToken(threadId);
                    if (supersedeReplacement)
                        _replacementPending[threadId] = replacementToken;
                    else
                        _replacementPending.TryAdd(threadId, replacementToken);
                }
                else if (authoritative)
                {
                    if (expectedToken is { } retryToken)
                    {
                        _authoritativePending.TryAdd(threadId, retryToken);
                    }
                    else
                    {
                        _authoritativePending[threadId] =
                            _state.CaptureHistoryToken(threadId);
                    }
                }
                return;
            }
            requestId = ++_requestSequence;
            _inFlight[threadId] = requestId;
            generationToken = _generationCancellation.Token;
            canBegin = _state.TryBeginHistory(
                threadId,
                force,
                expectedToken,
                out commitToken,
                out model,
                out generationActivation);
        }

        if (!canBegin)
        {
            CompleteInFlight(
                threadId,
                requestId,
                out var pendingReload);
            if (pendingReload is { } pending)
            {
                _ = ObserveRetryAsync(RerunAfterActivationAsync(
                    threadId,
                    generationActivation,
                    generationToken,
                    pending));
                return;
            }
            if (generationActivation is not null)
            {
                using var activationCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        generationToken);
                await generationActivation
                    .WaitAsync(activationCancellation.Token)
                    .ConfigureAwait(false);
                await LoadCoreAsync(
                        threadId,
                        force,
                        cancellationToken,
                        authoritative,
                        commitToken,
                        replacement,
                        supersedeReplacement: false)
                    .ConfigureAwait(false);
            }
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generationToken);
        var requestStartedAt = DateTimeOffset.Now;
        var operation = _telemetry.StartHistoryLoad(
            force ? ChatHistoryTelemetrySource.Forced : ChatHistoryTelemetrySource.Initial);
        var outcome = ChatTelemetryOutcome.Success;
        Exception? failure = null;
        Task<GatewayChatHistoryPage>? request = null;
        try
        {
            // ONE bounded initial page through the real production page path (no legacy unbounded
            // fallback). The window is stored atomically with the accepted commit below, so a stale or
            // failed reconstruction can never publish it.
            request = _bridge.RequestChatHistoryPageAsync(
                threadId,
                new ChatHistoryPageOptions(
                    Limit: InitialHistoryPageLimit,
                    MaxBytes: InitialHistoryPageMaxBytes,
                    Offset: 0),
                linkedCancellation.Token);
            var page = await request
                .WaitAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            var history = new ChatHistoryInfo
            {
                SessionKey = page.SessionKey,
                SessionId = page.SessionId,
                Messages = page.Messages,
            };
            if (!_state.IsHistoryRequestCurrent(commitToken))
            {
                outcome = ChatTelemetryOutcome.Canceled;
                return;
            }

            // Explicit WHOLE-reconstruction worker: the entire plan build (ordering, projection,
            // reserved scan, fold) runs on the pool, so it does not continue inline on the caller
            // even when the history task or an earlier precompute await already completed.
            var plan = await Task.Run(
                    () => BuildPlanAsync(
                        history,
                        threadId,
                        model,
                        commitToken.ResetGeneration,
                        linkedCancellation.Token),
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            // Pass the actual page so its window metadata is stored atomically with the accepted
            // timeline commit (never before the current-token check; no stale write possible).
            var committed = _state.CommitHistory(
                commitToken,
                plan,
                requestStartedAt,
                authoritative,
                new ChatHistoryWindowState(
                    page.SessionKey, page.SessionId, page.HasMore, page.NextOffset, page.Total, page.ResponseOffset,
                    page.Completeness, page.OmittedCount, page.NormalizedBytes));
            if (!committed)
            {
                outcome = ChatTelemetryOutcome.Canceled;
                return;
            }
            lock (_gate)
                _retryCounts.Remove(commitToken);
            // ACTUAL reported omission from the source: the received content is committed (valid rows preserved
            // incrementally) and a VISIBLE partial state is surfaced so the retained transcript is never presented
            // as a complete snapshot. The reported loss is never turned into invented rows.
            Completed?.Invoke(
                this,
                new ChatHistoryLoadResult(
                    commitToken,
                    PublishSnapshot: true,
                    page.ContentLossReported
                        ? new ChatProviderNotification(
                            ChatProviderNotificationKind.Error,
                            threadId,
                            LocalizationHelper.GetString("Chat_Notification_LoadHistoryFailed"),
                            "chat.history reported omitted content; the retained transcript shows only the received rows.")
                        : null));
        }
        catch (OperationCanceledException)
        {
            outcome = ChatTelemetryOutcome.Canceled;
            if (request is not null)
                _ = ObserveCanceledRequestAsync(request);
        }
        catch (Exception ex)
        {
            _failureReservedForTesting?.Invoke();
            if (!_state.IsHistoryRequestCurrent(commitToken))
            {
                outcome = ChatTelemetryOutcome.Canceled;
                failure = null;
                return;
            }
            outcome = ChatTelemetryOutcome.Failure;
            failure = ex;
            var shouldRetry = false;
            lock (_gate)
            {
                if (!_disposed &&
                    _state.CanRetryHistory(commitToken, authoritative))
                {
                    _retryCounts.TryGetValue(commitToken, out var retryCount);
                    shouldRetry = retryCount < MaxRetries;
                    if (shouldRetry)
                        _retryCounts[commitToken] = retryCount + 1;
                }
            }
            if (_state.IsHistoryRequestCurrent(commitToken))
            {
                Completed?.Invoke(
                    this,
                    new ChatHistoryLoadResult(
                        commitToken,
                        PublishSnapshot: false,
                        new ChatProviderNotification(
                            ChatProviderNotificationKind.Error,
                            threadId,
                            LocalizationHelper.GetString(
                                "Chat_Notification_LoadHistoryFailed"),
                            ex.Message)));
            }
            if (!_state.IsHistoryRequestCurrent(commitToken))
            {
                outcome = ChatTelemetryOutcome.Canceled;
                failure = null;
                shouldRetry = false;
            }
            if (shouldRetry)
            {
                _ = ObserveRetryAsync(_retryScheduler(
                    RetryDelay,
                    generationToken,
                    () => LoadCoreAsync(
                        threadId,
                        force: true,
                        CancellationToken.None,
                        authoritative,
                        commitToken,
                        replacement,
                        supersedeReplacement: false)));
            }
        }
        finally
        {
            _telemetry.FinishHistoryLoad(operation, outcome, failure);
            CompleteInFlight(threadId, requestId, out var pendingReload);
            if (pendingReload is { } pending)
            {
                _ = LoadCoreAsync(
                    threadId,
                    force: true,
                    CancellationToken.None,
                    authoritative: !pending.Replacement,
                    expectedToken: pending.Token,
                    replacement: pending.Replacement,
                    supersedeReplacement: false);
            }
        }
    }

    private async Task RerunAfterActivationAsync(
        string threadId,
        Task? generationActivation,
        CancellationToken generationToken,
        PendingReload pending)
    {
        if (generationActivation is not null)
        {
            await generationActivation
                .WaitAsync(generationToken)
                .ConfigureAwait(false);
        }
        generationToken.ThrowIfCancellationRequested();
        await LoadCoreAsync(
                threadId,
                force: true,
                CancellationToken.None,
                authoritative: !pending.Replacement,
                expectedToken: pending.Token,
                replacement: pending.Replacement,
                supersedeReplacement: false)
            .ConfigureAwait(false);
    }

    private async Task<ChatHistoryRebuildPlan> BuildPlanAsync(
        ChatHistoryInfo history,
        string threadId,
        string? model,
        long resetGeneration,
        CancellationToken cancellationToken)
    {
        // Test-only barrier: runs inside the reconstruction worker (see TestReconstructionBarrier).
        if (TestReconstructionBarrier is { } barrier)
            await barrier().ConfigureAwait(false);

        // GetToolMetadata / CreateAttachmentMatcher are independent, lock-guarded copies; obtained
        // here inside the worker (no invented UI-affinity restriction).
        var timeline = ChatTimelineState.Initial() with { HistoryLoaded = true };
        var metadata = new Dictionary<string, ChatEntryMetadata>(StringComparer.Ordinal);
        var cachedTools = _metadata.GetToolMetadata(
            history.SessionId,
            threadId,
            resetGeneration);
        var positionalToolMetadataTrusted = true;
        var attachmentMatcher = _metadata.CreateAttachmentMatcher(
            history.SessionId,
            threadId,
            resetGeneration);
        var nextAssistantIsAborted = false;
        var pendingUnkeyedToolCalls = new Queue<string>();
        var pendingVerifiedCallLookups =
            new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingVerifiedResultLookups =
            new Dictionary<string, int>(StringComparer.Ordinal);
        var seenVerifiedResults =
            new HashSet<string>(StringComparer.Ordinal);
        var syntheticToolCallSequence = 0;
        ChatMessageInfo? suppressedAbortedAssistant = null;

        ChatTimelineState Apply(
            ChatTimelineState current,
            ChatEvent evt,
            ChatEntryMetadata? entryMetadata)
        {
            var before = current.Entries
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.Ordinal);
            var next = ChatTimelineReducer.Apply(current, evt);
            if (entryMetadata is not null)
            {
                foreach (var entry in next.Entries)
                {
                    if (!before.Contains(entry.Id) && !metadata.ContainsKey(entry.Id))
                        metadata[entry.Id] = entryMetadata;
                }
                if (entryMetadata.AssistantContent is not null &&
                    next.ActiveAssistantId is { } assistantId &&
                    metadata.TryGetValue(assistantId, out var existingMetadata))
                {
                    metadata[assistantId] = existingMetadata with
                    {
                        AssistantContent =
                            ChatAssistantContentProjector.MergeLiveUpdate(
                                existingMetadata.AssistantContent,
                                entryMetadata.AssistantContent),
                    };
                }
            }
            return next;
        }

        // The pre-yield ordering/projection/reserved-ID scan is synchronous work that Task.Yield
        // cannot bound; run it on a worker with a linked cancellation token so a superseded or
        // cancelled build stops promptly instead of monopolising the caller (UI) thread.
        cancellationToken.ThrowIfCancellationRequested();
        (ChatHistoryReplayPart[] Parts, HashSet<string> Reserved) precomputed;
        try
        {
        precomputed = await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var orderedMessages = OrderHistoryMessages(history.Messages, cancellationToken);
                var parts = ChatHistoryReplayProjection.Project(orderedMessages, cancellationToken).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                // Check WHILE enumerating the reserved tool IDs, not only around the ToHashSet.
                var reserved = new HashSet<string>(StringComparer.Ordinal);
                var sinceToolCheck = 0;
                foreach (var part in parts)
                {
                    // Count ACTUAL tool entries (inner loop), not outer parts only.
                    foreach (var tool in part.ToolContent)
                    {
                        if (++sinceToolCheck >= OpenClaw.Shared.HistoryPagingBudget.DefaultItemsPerTick)
                        {
                            sinceToolCheck = 0;
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (!string.IsNullOrWhiteSpace(tool.CallId))
                            reserved.Add(tool.CallId);
                    }
                }
                return (parts, reserved);
            },
            cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
        {
            // LINQ/List.Sort can wrap a comparer-thrown OperationCanceledException; surface the real
            // cancellation so it is not reported as a load failure/retry.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        var (replayParts, reservedToolCallIds) = precomputed;

        // Post-worker / pre-fold check: a build superseded while the worker ran must not start the fold.
        cancellationToken.ThrowIfCancellationRequested();

        string AllocateSyntheticToolCallId()
        {
            while (true)
            {
                var candidate =
                    $"history-tool-{syntheticToolCallSequence++}";
                if (reservedToolCallIds.Add(candidate))
                    return candidate;
            }
        }

        ChatMetadataStore.CachedToolMeta? MatchPositionalToolMetadata(
            long historyTsMs) =>
            positionalToolMetadataTrusted
                ? ChatMetadataStore.TryMatchCachedTool(
                    cachedTools,
                    historyTsMs)
                : null;

        ChatMetadataStore.CachedToolMeta? MatchVerifiedToolMetadata(
            string toolCallId,
            long historyTsMs,
            bool isCall)
        {
            var counterpartLookups = isCall
                ? pendingVerifiedResultLookups
                : pendingVerifiedCallLookups;
            if (counterpartLookups.TryGetValue(
                    toolCallId,
                    out var counterpartCount))
            {
                if (counterpartCount == 1)
                    counterpartLookups.Remove(toolCallId);
                else
                    counterpartLookups[toolCallId] = counterpartCount - 1;
                if (!isCall)
                    seenVerifiedResults.Add(toolCallId);
                return null;
            }

            if (!isCall && !seenVerifiedResults.Add(toolCallId))
                return null;

            var lookup = ChatMetadataStore.TryMatchCachedToolByCallId(
                cachedTools,
                toolCallId,
                historyTsMs);
            var ownLookups = isCall
                ? pendingVerifiedCallLookups
                : pendingVerifiedResultLookups;
            ownLookups.TryGetValue(toolCallId, out var ownCount);
            ownLookups[toolCallId] = ownCount + 1;
            if (lookup.Outcome ==
                ChatMetadataStore.CachedToolLookupOutcome.Unmatched)
            {
                positionalToolMetadataTrusted = false;
            }
            return lookup.Match;
        }

        var partsSinceYield = 0;
        foreach (var replayPart in replayParts)
        {
            if (++partsSinceYield >= OpenClaw.Shared.HistoryPagingBudget.DefaultItemsPerTick)
            {
                partsSinceYield = 0;
                // Finite interval: a superseded/cancelled build stops at the next interval instead
                // of running the whole fold to completion.
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            var message = replayPart.Message;
            if (suppressedAbortedAssistant is not null)
            {
                if (ReferenceEquals(suppressedAbortedAssistant, message))
                    continue;
                suppressedAbortedAssistant = null;
            }

            var role = message.Role?.ToLowerInvariant() ?? string.Empty;
            var rawText = replayPart.Text;
            var userProjection = role == "user"
                ? GatewayMediaMessageProjection.Project(rawText)
                : null;
            var entryMetadata = new ChatEntryMetadata(
                message.Ts > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(message.Ts).ToLocalTime()
                    : null,
                model,
                message.InputTokens,
                message.OutputTokens,
                message.ResponseTokens,
                message.ContextPercent,
                GatewayMessageId: message.OpenClawId,
                OpenClawSeq: message.OpenClawSeq,
                GatewayDisplayItemId: message.OpenClawDisplayItemId,
                OpenClawKind: message.OpenClawKind,
                CompactionTokensBefore: message.CompactionTokensBefore,
                CompactionTokensAfter: message.CompactionTokensAfter,
                AssistantContent: role == "assistant"
                    ? ChatAssistantContentProjector.Project(
                        replayPart.AssistantContentParts)
                    : null);
            var text = ChatContentFormatting.TruncateForChatEntry(
                ChatMetadataStore.EscapeUntrustedAttachmentMarkerLines(
                    userProjection?.HasMediaEnvelope == true
                        ? userProjection.ReconciliationText
                        : rawText));
            if (userProjection is not null)
            {
                var cachedAttachment = attachmentMatcher.TryMatch(
                    userProjection.ReconciliationText,
                    userProjection.AttachmentCorrelationSignature,
                    message.Ts);
                var attachmentPresentations = cachedAttachment is not null
                    ? ChatMetadataStore.CreatePersistedLocalPresentations(
                        cachedAttachment.Attachments)
                    : userProjection.Attachments;
                entryMetadata = entryMetadata with
                {
                    Attachments = attachmentPresentations,
                };
            }
            var hasStructuredToolContent =
                replayPart.ToolContent.Count > 0;
            var hasUserAttachments =
                entryMetadata.Attachments is { Count: > 0 };
            var hasAssistantMedia =
                entryMetadata.AssistantContent is { Media.Count: > 0 };

            if (role == "user" &&
                _persistence.IsMessageAborted(
                    threadId,
                    message.OpenClawId,
                    resetGeneration))
            {
                nextAssistantIsAborted = true;
            }
            var gatewayAborted = role == "assistant" &&
                !string.IsNullOrEmpty(message.StopReason) &&
                !string.Equals(message.StopReason, "stop", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(message.StopReason, "toolUse", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(message.StopReason, "end_turn", StringComparison.OrdinalIgnoreCase);
            var isFirstAssistantPart =
                role == "assistant" && replayPart.IsFirstPart;
            var markAborted = isFirstAssistantPart &&
                (nextAssistantIsAborted || gatewayAborted);
            if (isFirstAssistantPart)
                nextAssistantIsAborted = false;
            if (markAborted)
            {
                timeline = Apply(
                    timeline,
                    new ChatStatusEvent(
                        "Response was stopped",
                        ChatTone.Warning),
                    entryMetadata);
                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatTurnEndEvent());
                suppressedAbortedAssistant = message;
                continue;
            }

            if (string.IsNullOrEmpty(text) &&
                !hasStructuredToolContent &&
                !hasUserAttachments &&
                !hasAssistantMedia)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(text) ||
                (role == "user" && hasUserAttachments) ||
                (role == "assistant" && hasAssistantMedia))
            {
                switch (role)
                {
                    case "user":
                        if (ChatContentFormatting.LooksLikeApprovalSlashCommand(text) ||
                            NativeToolProjector.LooksLikeSystemControlNote(text))
                        {
                            timeline = Apply(
                                timeline,
                                new ChatStatusEvent(text, ChatTone.Dim),
                                entryMetadata);
                        }
                        else
                        {
                            timeline = timeline with
                            {
                                ActiveAssistantId = null,
                                ActiveReasoningId = null,
                            };
                            timeline = Apply(
                                timeline,
                                new ChatUserMessageEvent(text),
                                entryMetadata);
                        }
                        break;
                    case "assistant":
                        if (ChatMessageInfo.IsSilentAssistantDirective(
                                role,
                                text))
                        {
                            break;
                        }
                        if (NativeToolProjector.LooksLikeSystemControlNote(text))
                        {
                            timeline = Apply(
                                timeline,
                                new ChatStatusEvent(text, ChatTone.Dim),
                                entryMetadata);
                        }
                        else if (NativeToolProjector.LooksLikeFlattenedToolOutput(text))
                        {
                            var cached =
                                MatchPositionalToolMetadata(message.Ts);
                            var assistantHistoryCallId =
                                AllocateSyntheticToolCallId();
                            var kind = cached?.ToolName ??
                                NativeToolProjector.ClassifyFlattenedToolOutput(text);
                            var label = cached?.Label ??
                                NativeToolProjector.ExtractFlattenedToolSummary(text);
                            timeline = Apply(
                                timeline,
                                new ChatToolStartEvent(
                                    label,
                                    kind,
                                    ToolArgs: cached?.ToolArgs,
                                    ToolCallId: assistantHistoryCallId,
                                    IdentityStrength: cached?.IdentityStrength ??
                                        NativeToolProjector.ClassifyHistoryIdentityStrength(
                                            kind)),
                                entryMetadata);
                            timeline = Apply(
                                timeline,
                                new ChatToolOutputEvent(
                                    text,
                                    ToolCallId: assistantHistoryCallId),
                                entryMetadata);
                        }
                        else
                        {
                            timeline = Apply(
                                timeline,
                                new ChatMessageEvent(
                                    ChatContentFormatting.RepairContentBlockSeams(
                                        text)),
                                entryMetadata);
                            if (timeline.ActiveToolCalls.Count > 0 ||
                                timeline.ActiveToolCallId is not null)
                            {
                                timeline = timeline with
                                {
                                    ActiveAssistantId = null,
                                    ActiveReasoningId = null,
                                };
                            }
                            else
                            {
                                timeline = ChatTimelineReducer.Apply(
                                    timeline,
                                    new ChatTurnEndEvent());
                            }
                        }
                        break;
                    case "toolresult":
                    case "tool_result":
                        if (hasStructuredToolContent)
                            break;
                        var cachedTool =
                            MatchPositionalToolMetadata(message.Ts);
                        var toolResultHistoryCallId =
                            AllocateSyntheticToolCallId();
                        var toolKind = cachedTool?.ToolName ??
                            NativeToolProjector.ClassifyFlattenedToolOutput(text);
                        var toolLabel = cachedTool?.Label ??
                            NativeToolProjector.ExtractFlattenedToolSummary(text);
                        timeline = Apply(
                            timeline,
                            new ChatToolStartEvent(
                                toolLabel,
                                toolKind,
                                ToolArgs: cachedTool?.ToolArgs,
                                ToolCallId: toolResultHistoryCallId,
                                IdentityStrength: cachedTool?.IdentityStrength ??
                                    NativeToolProjector.ClassifyHistoryIdentityStrength(
                                        toolKind)),
                            entryMetadata);
                        timeline = Apply(
                            timeline,
                            new ChatToolOutputEvent(
                                text,
                                ToolCallId: toolResultHistoryCallId),
                            entryMetadata);
                        break;
                    case "system":
                    case "tool":
                        timeline = Apply(
                            timeline,
                            new ChatStatusEvent(text, ChatTone.Dim),
                            entryMetadata);
                        break;
                    default:
                        timeline = Apply(
                            timeline,
                            new ChatMessageEvent(
                                ChatContentFormatting.RepairContentBlockSeams(
                                    text)),
                            entryMetadata);
                        timeline = ChatTimelineReducer.Apply(
                            timeline,
                            new ChatTurnEndEvent());
                        break;
                }
            }

            foreach (var toolBlock in replayPart.ToolContent)
            {
                if (toolBlock.Kind == ChatToolContentKind.Call)
                {
                    var args =
                        ChatHistoryReplayProjection.ProjectToolArgs(
                            toolBlock.Args);
                    var callId = toolBlock.CallId;
                    if (string.IsNullOrWhiteSpace(callId))
                    {
                        _ = MatchPositionalToolMetadata(message.Ts);
                        callId = AllocateSyntheticToolCallId();
                        pendingUnkeyedToolCalls.Enqueue(callId);
                    }
                    else
                    {
                        _ = MatchVerifiedToolMetadata(
                            callId,
                            message.Ts,
                            isCall: true);
                    }
                    timeline = Apply(
                        timeline,
                        new ChatToolStartEvent(
                            ChatHistoryReplayProjection.ToolLabel(
                                toolBlock.ToolName,
                                args),
                            toolBlock.ToolName,
                            args,
                            callId),
                        entryMetadata);
                    continue;
                }

                var resultCallId = toolBlock.CallId;
                var hasVerifiedCallId =
                    !string.IsNullOrWhiteSpace(resultCallId);
                if (!hasVerifiedCallId)
                {
                    resultCallId =
                        pendingUnkeyedToolCalls.Count > 0
                            ? pendingUnkeyedToolCalls.Dequeue()
                            : AllocateSyntheticToolCallId();
                }
                var resolvedCallId = resultCallId!;
                var correlationKey = new ChatToolCorrelationKey(
                    RunId: null,
                    LegacyTurn: timeline.ToolLegacyTurn,
                    ToolCallId: resolvedCallId);
                var verifiedCached = hasVerifiedCallId
                    ? MatchVerifiedToolMetadata(
                        resolvedCallId,
                        message.Ts,
                        isCall: false)
                    : null;
                if (!timeline.ActiveToolCalls.ContainsKey(correlationKey))
                {
                    var cached = hasVerifiedCallId
                        ? verifiedCached
                        : MatchPositionalToolMetadata(message.Ts);
                    var toolName =
                        cached?.ToolName ?? toolBlock.ToolName;
                    timeline = Apply(
                        timeline,
                        new ChatToolStartEvent(
                            cached?.Label ?? toolName,
                            toolName,
                            ToolArgs: cached?.ToolArgs,
                            ToolCallId: resolvedCallId,
                            IdentityStrength:
                                cached?.IdentityStrength ??
                                NativeToolProjector.ClassifyHistoryIdentityStrength(
                                    toolName)),
                        entryMetadata);
                }
                var output = NativeToolProjector.TruncateToolOutput(
                    toolBlock.Text ?? string.Empty);
                timeline = Apply(
                    timeline,
                    toolBlock.IsError
                        ? new ChatToolErrorEvent(
                            output,
                            resolvedCallId)
                        : new ChatToolOutputEvent(
                            output,
                            resolvedCallId),
                    entryMetadata);
            }
        }

        if (nextAssistantIsAborted)
        {
            timeline = Apply(
                timeline,
                new ChatStatusEvent("Response was stopped", ChatTone.Warning),
                null);
            timeline = ChatTimelineReducer.Apply(timeline, new ChatTurnEndEvent());
        }
        timeline = ChatTimelineReducer.Apply(
            timeline,
            new ChatTurnEndEvent());
        timeline = timeline with
        {
            TurnActive = false,
            ActiveAssistantId = null,
            ActiveReasoningId = null,
        };
        var maxSequence = history.Messages
            .Where(message => message.OpenClawSeq is not null)
            .Select(message => message.OpenClawSeq!.Value)
            .DefaultIfEmpty(int.MinValue)
            .Max();
        // Final pre-return check: a build cancelled during the fold must not publish a plan.
        cancellationToken.ThrowIfCancellationRequested();
        return new(history.SessionId, timeline, metadata, maxSequence);
    }

    private void CompleteInFlight(
        string threadId,
        long requestId,
        out PendingReload? pendingReload)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(threadId, out var current) ||
                current != requestId)
            {
                pendingReload = null;
                return;
            }
            _inFlight.Remove(threadId);
            pendingReload = null;
            if (_disposed)
                return;
            if (_replacementPending.Remove(threadId, out var replacementToken))
            {
                pendingReload = new(replacementToken, Replacement: true);
                return;
            }
            if (_authoritativePending.Remove(threadId, out var authoritativeToken))
                pendingReload = new(authoritativeToken, Replacement: false);
        }
    }

    internal static List<ChatMessageInfo> OrderHistoryMessages(
        IReadOnlyList<ChatMessageInfo> messages,
        CancellationToken cancellationToken,
        Action<int>? comparisonObserver = null,
        int? comparisonInterval = null)
    {
        // Finite, cheap checks only (no per-comparison synchronization). Ordering semantics are
        // unchanged: seq when all sequenced, timestamp when none sequenced, insertion order when
        // mixed, with the original index as the tiebreaker.
        cancellationToken.ThrowIfCancellationRequested();
        var interval = OpenClaw.Shared.HistoryPagingBudget.DefaultItemsPerTick;
        var sinceCheck = 0;
        void Tick()
        {
            if (++sinceCheck >= interval)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var indexed = new List<(ChatMessageInfo Message, int Index)>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            Tick();
            indexed.Add((messages[i], i));
        }

        // Sequenced-count scan: finite checks while scanning, not only around it.
        var sequencedCount = 0;
        for (var i = 0; i < indexed.Count; i++)
        {
            Tick();
            if (indexed[i].Message.OpenClawSeq is not null) sequencedCount++;
        }

        // Explicit comparer (not LINQ): preserves the original-index tiebreaker and checks the linked
        // token at a FINITE COMPARISON interval. No per-comparison locking/synchronization.
        var mode = sequencedCount == indexed.Count ? 0 : sequencedCount == 0 ? 1 : 2;
        var compareInterval = comparisonInterval is > 0 ? comparisonInterval.Value : interval;
        var comparisons = 0;
        int Compare((ChatMessageInfo Message, int Index) a, (ChatMessageInfo Message, int Index) b)
        {
            comparisons++;
            comparisonObserver?.Invoke(comparisons);
            if (comparisons % compareInterval == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var primary = mode switch
            {
                0 => System.Nullable.Compare(a.Message.OpenClawSeq, b.Message.OpenClawSeq),
                1 => a.Message.Ts.CompareTo(b.Message.Ts),
                _ => 0,
            };
            return primary != 0 ? primary : a.Index.CompareTo(b.Index);
        }

        // List.Sort wraps a comparer-thrown exception in InvalidOperationException; surface the real
        // cancellation here (and again at the loader boundary).
        try
        {
            indexed.Sort(Compare);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = new List<ChatMessageInfo>(indexed.Count);
        for (var i = 0; i < indexed.Count; i++)
            ordered.Add(indexed[i].Message);
        return ordered;
    }

    private static async Task ObserveRetryAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ChatHistory] Retry scheduler failed: {ex.GetType().Name}");
        }
    }

    private static async Task ObserveCanceledRequestAsync(Task request)
    {
        try
        {
            await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Debug(
                $"[ChatHistory] Canceled request completed with {ex.GetType().Name}");
        }
    }
}
