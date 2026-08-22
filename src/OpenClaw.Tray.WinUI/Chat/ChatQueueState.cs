using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal readonly record struct ChatLocalSentText(
    string Text,
    DateTimeOffset SentAt,
    string QueuedMessageId,
    string AttachmentCorrelationSignature = "");

internal sealed record ChatQueueRetryResult(
    bool Requeued,
    TimeSpan Delay,
    bool ShouldEndTurn);

/// <summary>
/// Owns queued-message/request collections, local echo correlation, run
/// mappings, drain scheduling, and queue retry mechanics. The root owns the
/// only lock and coordinates timeline/run/reset commits around these methods.
/// </summary>
internal sealed class ChatQueueState
{
    private const int MaxLocalEchoes = 20;
    private static readonly TimeSpan LocalEchoWindow = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Queue<ChatLocalSentText>> _localSentTexts =
        new();
    private readonly Dictionary<string, List<ChatQueuedMessage>> _messages = new();
    private readonly Dictionary<string, List<ChatQueuedSendRequest>> _requests = new();
    private readonly Dictionary<string, Dictionary<string, string>> _messageIdsByRunId =
        new();
    private readonly HashSet<string> _drainScheduledThreads =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _assistantFallbackPromotedThreads =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _locallyInitiatedThreads = new();

    private long _messageSequence;

    internal string NextMessageId() => $"q{++_messageSequence}";

    internal IReadOnlyDictionary<string, IReadOnlyList<ChatQueuedMessage>>
        SnapshotMessages() =>
        _messages.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ChatQueuedMessage>)pair.Value.ToArray());

    internal string[] ThreadsWithMessages() => _messages.Keys.ToArray();

    internal void AddMessage(string threadId, ChatQueuedMessage message)
    {
        if (!_messages.TryGetValue(threadId, out var messages))
        {
            messages = [];
            _messages[threadId] = messages;
        }
        messages.RemoveAll(existing => existing.Id == message.Id);
        messages.Add(message);
    }

    internal void AddRequest(ChatQueuedSendRequest request)
    {
        if (!_requests.TryGetValue(request.ThreadId, out var requests))
        {
            requests = [];
            _requests[request.ThreadId] = requests;
        }
        requests.RemoveAll(existing => existing.Id == request.Id);
        requests.Add(request);
    }

    internal ChatQueuedSendRequest? FindRequest(
        string threadId,
        string messageId) =>
        _requests.TryGetValue(threadId, out var requests)
            ? requests.FirstOrDefault(request =>
                string.Equals(request.Id, messageId, StringComparison.Ordinal))
            : null;

    internal void RemoveRequest(string threadId, string messageId)
    {
        if (!_requests.TryGetValue(threadId, out var requests))
            return;
        requests.RemoveAll(request => request.Id == messageId);
        if (requests.Count == 0)
            _requests.Remove(threadId);
    }

    internal bool CanSendDirectly(
        string threadId,
        bool hasActiveRun,
        bool turnActive) =>
        ChatSendQueuePolicy.CanSendDirectly(
            hasActiveRun,
            turnActive,
            HasPendingMessages(threadId));

    internal bool CanClearAssistantFallback(
        string threadId,
        bool hasActiveRun,
        bool turnActive) =>
        !HasSendingMessages(threadId) &&
        !hasActiveRun &&
        !turnActive;

    internal ChatQueuedSendDispatch StartDirect(
        ChatQueuedSendRequest request,
        string? sessionId,
        long connectionGeneration,
        long resetVersion,
        long resetLifecycleSequence,
        long lifecycleStartSequence)
    {
        EnqueueLocalEcho(
            request.ThreadId,
            request.EffectiveTimelineText,
            request.AttachmentCorrelationSignature,
            request.Id);
        _locallyInitiatedThreads.Add(request.ThreadId);
        _assistantFallbackPromotedThreads.Add(request.ThreadId);
        return new ChatQueuedSendDispatch(
            request,
            sessionId,
            connectionGeneration,
            resetVersion,
            resetLifecycleSequence,
            lifecycleStartSequence,
            StartedDirectly: true);
    }

    internal ChatQueuedSendDispatch? TryStartNext(
        string threadId,
        bool requireConnected,
        ConnectionStatus status,
        bool hasActiveRun,
        bool turnActive,
        string? sessionId,
        long connectionGeneration,
        long resetVersion,
        long resetLifecycleSequence,
        long lifecycleStartSequence,
        out TimeSpan? delayedRetry)
    {
        delayedRetry = null;
        if (!ChatSendQueuePolicy.CanStartNext(
                requireConnected,
                status,
                hasActiveRun,
                turnActive,
                HasSendingMessages(threadId)) ||
            !_messages.TryGetValue(threadId, out var messages))
        {
            return null;
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].SendState != ChatQueuedMessageSendState.Queued)
                continue;
            var request = FindRequest(threadId, messages[index].Id);
            if (request is null)
                continue;

            var now = DateTimeOffset.UtcNow;
            if (request.DeferredAdmissionRetryAfter is { } retryAfter)
            {
                if (retryAfter > now)
                {
                    delayedRetry = retryAfter - now;
                    return null;
                }
                request = request with { DeferredAdmissionRetryAfter = null };
                AddRequest(request);
            }

            _assistantFallbackPromotedThreads.Remove(threadId);
            messages[index] = messages[index] with
            {
                SendState = ChatQueuedMessageSendState.Sending,
                ErrorText = null,
            };
            if (request.LifecycleCommand is null)
            {
                EnqueueLocalEcho(
                    threadId,
                    request.EffectiveTimelineText,
                    request.AttachmentCorrelationSignature,
                    request.Id);
                _locallyInitiatedThreads.Add(threadId);
            }

            return new ChatQueuedSendDispatch(
                request,
                sessionId,
                connectionGeneration,
                resetVersion,
                resetLifecycleSequence,
                lifecycleStartSequence,
                StartedDirectly: false);
        }

        return null;
    }

    internal bool TryScheduleDrain(string threadId) =>
        _messages.ContainsKey(threadId) &&
        _drainScheduledThreads.Add(threadId);

    internal void CompleteDrainSchedule(string threadId) =>
        _drainScheduledThreads.Remove(threadId);

    internal void TrackRun(string threadId, string runId, string messageId)
    {
        if (!_messageIdsByRunId.TryGetValue(threadId, out var byRunId))
        {
            byRunId = new Dictionary<string, string>(StringComparer.Ordinal);
            _messageIdsByRunId[threadId] = byRunId;
        }
        byRunId[runId] = messageId;
    }

    internal bool TryResolveMessageForRun(
        string threadId,
        string runId,
        out string messageId)
    {
        messageId = string.Empty;
        if (!_messageIdsByRunId.TryGetValue(threadId, out var byRunId) ||
            !byRunId.TryGetValue(runId, out var resolved))
        {
            return false;
        }
        messageId = resolved;
        return true;
    }

    internal string[] RunIdsForThread(string threadId) =>
        _messageIdsByRunId.TryGetValue(threadId, out var byRunId)
            ? byRunId.Keys.ToArray()
            : [];

    internal void RemoveRunMappingByMessageId(
        string threadId,
        string messageId)
    {
        if (!_messageIdsByRunId.TryGetValue(threadId, out var byRunId))
            return;
        foreach (var runId in byRunId
                     .Where(pair => pair.Value == messageId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            byRunId.Remove(runId);
        }
        if (byRunId.Count == 0)
            _messageIdsByRunId.Remove(threadId);
    }

    internal void RemoveRunMappingByRunId(string threadId, string runId)
    {
        if (!_messageIdsByRunId.TryGetValue(threadId, out var byRunId))
            return;
        if (byRunId.TryGetValue(runId, out var messageId))
        {
            foreach (var alias in byRunId
                         .Where(pair => pair.Value == messageId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                byRunId.Remove(alias);
            }
        }
        else
        {
            byRunId.Remove(runId);
        }
        if (byRunId.Count == 0)
            _messageIdsByRunId.Remove(threadId);
    }

    internal bool RemoveMessage(string threadId, string messageId)
    {
        if (!_messages.TryGetValue(threadId, out var messages))
            return false;
        var removed = messages.RemoveAll(message => message.Id == messageId) > 0;
        if (removed)
        {
            RemoveRunMappingByMessageId(threadId, messageId);
            RemoveRequest(threadId, messageId);
        }
        RemoveEmptyThread(threadId, messages);
        return removed;
    }

    internal bool CancelMessage(string threadId, string messageId)
    {
        if (!_messages.TryGetValue(threadId, out var messages))
            return false;
        var index = messages.FindIndex(message => message.Id == messageId);
        if (index < 0 ||
            messages[index].SendState == ChatQueuedMessageSendState.Sending)
        {
            return false;
        }
        messages.RemoveAt(index);
        RemovePendingLocalEcho(threadId, messageId);
        RemoveRunMappingByMessageId(threadId, messageId);
        RemoveRequest(threadId, messageId);
        RemoveEmptyThread(threadId, messages);
        return true;
    }

    internal bool TryTakeForPromotion(
        string threadId,
        string messageId,
        out ChatQueuedMessage message)
    {
        message = default!;
        if (!_messages.TryGetValue(threadId, out var messages))
            return false;
        var index = messages.FindIndex(candidate => candidate.Id == messageId);
        if (index < 0)
            return false;

        message = messages[index];
        messages.RemoveAt(index);
        _assistantFallbackPromotedThreads.Add(threadId);
        RemoveRequest(threadId, messageId);
        RemoveEmptyThread(threadId, messages);
        return true;
    }

    internal void MarkFailed(string threadId, string messageId, string error)
    {
        if (!_messages.TryGetValue(threadId, out var messages))
            return;
        var index = messages.FindIndex(message => message.Id == messageId);
        if (index >= 0)
        {
            messages[index] = messages[index] with
            {
                SendState = ChatQueuedMessageSendState.Failed,
                ErrorText = error,
            };
        }
    }

    internal ChatQueueRetryResult RequeueDeferredAdmission(
        string threadId,
        string messageId,
        bool hasActiveRun)
    {
        if (!_messages.TryGetValue(threadId, out var messages))
            return new(false, ChatSendQueuePolicy.DrainDelay, false);
        var index = messages.FindIndex(message =>
            message.Id == messageId &&
            message.SendState == ChatQueuedMessageSendState.Sending);
        if (index < 0)
            return new(false, ChatSendQueuePolicy.DrainDelay, false);

        var retryCount = IncrementDeferredAdmissionRetryCount(
            threadId,
            messageId);
        if (retryCount > ChatSendQueuePolicy.MaxDeferredAdmissionRetries)
        {
            throw new InvalidOperationException(
                $"Gateway kept chat.send status in_flight after {ChatSendQueuePolicy.MaxDeferredAdmissionRetries} retries.");
        }

        messages[index] = messages[index] with
        {
            SendState = ChatQueuedMessageSendState.Queued,
            ErrorText = null,
        };
        var delay = ChatSendQueuePolicy.DeferredAdmissionRetryDelay(retryCount);
        SetDeferredAdmissionRetryAfter(
            threadId,
            messageId,
            DateTimeOffset.UtcNow + delay);
        _assistantFallbackPromotedThreads.Remove(threadId);
        return new(true, delay, ShouldEndTurn: !hasActiveRun);
    }

    internal bool HasSendingMessages(string threadId) =>
        _messages.TryGetValue(threadId, out var messages) &&
        messages.Any(message =>
            message.SendState == ChatQueuedMessageSendState.Sending);

    internal bool HasPendingMessages(string threadId) =>
        _messages.TryGetValue(threadId, out var messages) &&
        messages.Any(message =>
            message.SendState is ChatQueuedMessageSendState.Queued or
                ChatQueuedMessageSendState.Sending);

    internal bool TryGetSingleSendingMessage(
        string threadId,
        out ChatQueuedMessage message)
    {
        message = default!;
        if (!_messages.TryGetValue(threadId, out var messages))
            return false;
        ChatQueuedMessage? found = null;
        foreach (var candidate in messages)
        {
            if (candidate.SendState != ChatQueuedMessageSendState.Sending ||
                FindRequest(threadId, candidate.Id)?.LifecycleCommand is not null)
            {
                continue;
            }
            if (found is not null)
                return false;
            found = candidate;
        }
        if (found is null)
            return false;
        message = found;
        return true;
    }

    internal bool IsLocallyInitiated(string threadId) =>
        _locallyInitiatedThreads.Contains(threadId);

    internal void ClearLocallyInitiatedIfIdle(
        string threadId,
        bool hasActiveRun,
        bool turnActive)
    {
        if (!hasActiveRun && !turnActive && !HasPendingMessages(threadId))
            _locallyInitiatedThreads.Remove(threadId);
    }

    internal void ClearLocallyInitiated(string threadId) =>
        _locallyInitiatedThreads.Remove(threadId);

    internal bool IsAssistantFallbackPromoted(string threadId) =>
        _assistantFallbackPromotedThreads.Contains(threadId);

    internal void ClearAssistantFallbackPromotion(string threadId) =>
        _assistantFallbackPromotedThreads.Remove(threadId);

    internal ChatLocalSentText[] SnapshotLocalEchoes(string threadId) =>
        _localSentTexts.TryGetValue(threadId, out var queue)
            ? queue.ToArray()
            : [];

    internal bool HasPendingLocalEchoText(
        string threadId,
        string text,
        string attachmentCorrelationSignature = "",
        bool hasMediaEnvelope = false)
    {
        var normalizedText =
            GatewayMediaMessageProjection.NormalizeEchoCorrelationText(text);
        if ((normalizedText.Length == 0 &&
             string.IsNullOrEmpty(attachmentCorrelationSignature)) ||
            !_localSentTexts.TryGetValue(threadId, out var queue))
        {
            return false;
        }
        var candidates = queue
            .Select(pending => new ChatPendingEchoCandidate(
                pending.QueuedMessageId,
                pending.Text,
                pending.AttachmentCorrelationSignature))
            .ToArray();
        return ChatAttachmentEchoCorrelation.SelectMatchingMessageId(
            candidates,
            normalizedText,
            attachmentCorrelationSignature,
            hasMediaEnvelope) is not null;
    }

    internal bool TryConsumeLocalEcho(
        string threadId,
        string echoText,
        string attachmentCorrelationSignature,
        bool hasMediaEnvelope,
        out string queuedMessageId)
    {
        queuedMessageId = string.Empty;
        if (!_localSentTexts.TryGetValue(threadId, out var queue))
            return false;
        var normalizedEchoText =
            GatewayMediaMessageProjection.NormalizeEchoCorrelationText(echoText);

        var now = DateTimeOffset.Now;
        while (queue.Count > 0 && now - queue.Peek().SentAt > LocalEchoWindow)
            queue.Dequeue();
        if (queue.Count == 0)
        {
            _localSentTexts.Remove(threadId);
            return false;
        }

        var pending = queue.ToArray();
        var candidates = pending
            .Select(candidate => new ChatPendingEchoCandidate(
                candidate.QueuedMessageId,
                candidate.Text,
                candidate.AttachmentCorrelationSignature))
            .ToArray();
        var matchedMessageId = ChatAttachmentEchoCorrelation.SelectMatchingMessageId(
            candidates,
            normalizedEchoText,
            attachmentCorrelationSignature,
            hasMediaEnvelope);
        if (matchedMessageId is null)
            return false;

        var retained = new Queue<ChatLocalSentText>(pending.Length);
        foreach (var candidate in pending)
        {
            if (!string.Equals(
                    candidate.QueuedMessageId,
                    matchedMessageId,
                    StringComparison.Ordinal))
            {
                retained.Enqueue(candidate);
            }
        }
        queuedMessageId = matchedMessageId;
        StoreLocalEchoQueue(threadId, retained);
        return true;
    }

    // Plain-text overload retained for call sites (e.g. reset-gate dropped
    // messages) that never carry a media envelope.
    internal bool TryConsumeLocalEcho(
        string threadId,
        string echoText,
        out string queuedMessageId) =>
        TryConsumeLocalEcho(
            threadId,
            echoText,
            attachmentCorrelationSignature: "",
            hasMediaEnvelope: false,
            out queuedMessageId);

    internal void RemovePendingLocalEcho(string threadId, string messageId)
    {
        if (!_localSentTexts.TryGetValue(threadId, out var queue))
            return;
        var retained = new Queue<ChatLocalSentText>(
            queue.Where(local => local.QueuedMessageId != messageId));
        StoreLocalEchoQueue(threadId, retained);
    }

    internal void ClearForReconnect()
    {
        _locallyInitiatedThreads.Clear();
        _localSentTexts.Clear();
        _messages.Clear();
        _requests.Clear();
        _drainScheduledThreads.Clear();
        _assistantFallbackPromotedThreads.Clear();
        _messageIdsByRunId.Clear();
    }

    internal void ClearForDispose()
    {
        _messages.Clear();
        _requests.Clear();
        _drainScheduledThreads.Clear();
        _messageIdsByRunId.Clear();
        _localSentTexts.Clear();
        _locallyInitiatedThreads.Clear();
    }

    internal void ClearThreadForReset(string threadId)
    {
        _locallyInitiatedThreads.Remove(threadId);
        _localSentTexts.Remove(threadId);
        _messages.Remove(threadId);
        _requests.Remove(threadId);
        _drainScheduledThreads.Remove(threadId);
        _messageIdsByRunId.Remove(threadId);
        _assistantFallbackPromotedThreads.Remove(threadId);
    }

    private void EnqueueLocalEcho(
        string threadId,
        string text,
        string attachmentCorrelationSignature,
        string messageId)
    {
        RemovePendingLocalEcho(threadId, messageId);
        if (!_localSentTexts.TryGetValue(threadId, out var queue))
        {
            queue = new Queue<ChatLocalSentText>();
            _localSentTexts[threadId] = queue;
        }
        queue.Enqueue(new ChatLocalSentText(
            text,
            DateTimeOffset.UtcNow,
            messageId,
            attachmentCorrelationSignature));
        while (queue.Count > MaxLocalEchoes)
            queue.Dequeue();
    }

    private void RemoveEmptyThread(
        string threadId,
        List<ChatQueuedMessage> messages)
    {
        if (messages.Count != 0)
            return;
        _messages.Remove(threadId);
        _drainScheduledThreads.Remove(threadId);
    }

    private void StoreLocalEchoQueue(
        string threadId,
        Queue<ChatLocalSentText> queue)
    {
        if (queue.Count == 0)
            _localSentTexts.Remove(threadId);
        else
            _localSentTexts[threadId] = queue;
    }

    private void SetDeferredAdmissionRetryAfter(
        string threadId,
        string messageId,
        DateTimeOffset retryAfter)
    {
        if (!_requests.TryGetValue(threadId, out var requests))
            return;
        var index = requests.FindIndex(request => request.Id == messageId);
        if (index >= 0)
        {
            requests[index] = requests[index] with
            {
                DeferredAdmissionRetryAfter = retryAfter,
            };
        }
    }

    private int IncrementDeferredAdmissionRetryCount(
        string threadId,
        string messageId)
    {
        if (!_requests.TryGetValue(threadId, out var requests))
            return ChatSendQueuePolicy.MaxDeferredAdmissionRetries + 1;
        var index = requests.FindIndex(request => request.Id == messageId);
        if (index < 0)
            return ChatSendQueuePolicy.MaxDeferredAdmissionRetries + 1;
        var count = requests[index].DeferredAdmissionRetryCount + 1;
        requests[index] = requests[index] with
        {
            DeferredAdmissionRetryCount = count,
        };
        return count;
    }
}
