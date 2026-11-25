// Host-local new-chat intent. Queue a draft, then materialize it on first send.
using OpenClaw.Chat;
using OpenClaw.Shared;
using System.Collections.Generic;

namespace OpenClawTray.Chat;

internal sealed record ChatPlacementIntentSnapshot(
    ChatPlacementSelection Selection,
    bool RequiresPreparation,
    bool OutcomeUncertain,
    bool Preparing,
    ChatPlacementError? Error);

/// <summary>A fresh conversation identity and options that have not yet been sent to the Gateway.</summary>
internal sealed class ChatDraft(string key, string? agentId, bool useGatewayDefaultAgent = false)
{
    private sealed record SendIdempotencyAttempt(
        string Text,
        ChatAttachment[] Attachments,
        string Key,
        string? SessionId);

    private readonly object _placementGate = new();
    private readonly object _creationGate = new();
    private readonly List<SendIdempotencyAttempt> _uncertainSendAttempts = [];
    private ChatPlacementIntentSnapshot? _placementIntent;
    private Task<SessionCreateResult>? _creationTask;
    private string? _gatewaySessionKey;
    private bool _creationOutcomeUncertain;
    private string? _lastSendText;
    private ChatAttachment[]? _lastSendAttachments;
    private string? _lastSendIdempotencyKey;
    private string? _lastSendSessionId;

    internal string Key { get; } = key;
    internal string? AgentId { get; } = agentId;
    internal bool UsesGatewayDefaultAgent { get; } = useGatewayDefaultAgent;
    internal string? GatewaySessionKey
    {
        get { lock (_creationGate) return _gatewaySessionKey; }
    }
    internal string TargetKey => GatewaySessionKey ?? Key;
    internal void RememberGatewaySessionKey(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        lock (_creationGate)
        {
            if (_gatewaySessionKey is not null &&
                !string.Equals(_gatewaySessionKey, sessionKey, StringComparison.Ordinal))
                throw new InvalidOperationException("The draft is already bound to another Gateway session.");
            _gatewaySessionKey = sessionKey;
        }
    }

    internal string GetSendIdempotencyKey(
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(attachments);
        var trimmedMessage = message.Trim();
        lock (_creationGate)
        {
            for (var index = _uncertainSendAttempts.Count - 1; index >= 0; index--)
            {
                var attempt = _uncertainSendAttempts[index];
                if (string.Equals(attempt.Text, trimmedMessage, StringComparison.Ordinal) &&
                    HasSameAttachments(attempt.Attachments, attachments) &&
                    SessionIdentitiesMayMatch(attempt.SessionId, sessionId))
                {
                    if (attempt.SessionId is null && sessionId is not null)
                        _uncertainSendAttempts[index] = attempt with { SessionId = sessionId };
                    return attempt.Key;
                }
            }

            if (_lastSendIdempotencyKey is not null &&
                string.Equals(_lastSendText, trimmedMessage, StringComparison.Ordinal) &&
                HasSameAttachments(_lastSendAttachments, attachments) &&
                SessionIdentitiesMayMatch(_lastSendSessionId, sessionId))
            {
                if (_lastSendSessionId is null && sessionId is not null)
                    _lastSendSessionId = sessionId;
                return _lastSendIdempotencyKey;
            }

            var snapshot = new ChatAttachment[attachments.Count];
            for (var index = 0; index < attachments.Count; index++)
                snapshot[index] = attachments[index];
            _lastSendText = trimmedMessage;
            _lastSendAttachments = snapshot;
            _lastSendIdempotencyKey = Guid.NewGuid().ToString("N");
            _lastSendSessionId = sessionId;
            return _lastSendIdempotencyKey;
        }
    }

    internal void MarkSendOutcomeUncertain(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        lock (_creationGate)
        {
            if (_lastSendIdempotencyKey is not { } currentKey ||
                !string.Equals(currentKey, idempotencyKey, StringComparison.Ordinal) ||
                _lastSendText is not { } currentText ||
                _lastSendAttachments is not { } currentAttachments)
            {
                return;
            }

            _uncertainSendAttempts.Add(new(
                currentText,
                currentAttachments,
                currentKey,
                _lastSendSessionId));
            _lastSendText = null;
            _lastSendAttachments = null;
            _lastSendIdempotencyKey = null;
            _lastSendSessionId = null;
        }
    }

    internal void ForgetSendIdempotencyKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        lock (_creationGate)
        {
            if (string.Equals(_lastSendIdempotencyKey, idempotencyKey, StringComparison.Ordinal))
            {
                _lastSendText = null;
                _lastSendAttachments = null;
                _lastSendIdempotencyKey = null;
                _lastSendSessionId = null;
            }
            _uncertainSendAttempts.RemoveAll(attempt =>
                string.Equals(attempt.Key, idempotencyKey, StringComparison.Ordinal));
        }
    }

    private static bool SessionIdentitiesMayMatch(string? previous, string? current) =>
        previous is null ||
        current is null ||
        string.Equals(previous, current, StringComparison.Ordinal);

    private static bool HasSameAttachments(
        ChatAttachment[]? previous,
        IReadOnlyList<ChatAttachment> current)
    {
        if (previous is null || previous.Length != current.Count)
            return false;
        for (var index = 0; index < current.Count; index++)
        {
            if (!ReferenceEquals(previous[index], current[index]))
                return false;
        }
        return true;
    }

    internal bool CreationOutcomeUncertain
    {
        get { lock (_creationGate) return _creationOutcomeUncertain; }
    }
    internal bool IsCompleted { get; private set; }
    internal ChatPlacementIntentSnapshot? PlacementIntent
    {
        get { lock (_placementGate) return _placementIntent; }
    }

    internal ChatPlacementSelection? PlacementSelection => PlacementIntent?.Selection;
    internal long Revision { get; private set; }
    internal bool ModelChanged { get; private set; }
    internal string? Model { get; private set; }
    internal bool ThinkingChanged { get; private set; }
    internal string? ThinkingLevel { get; private set; }

    /// <summary>Allocates only a local key, not a Gateway session.</summary>
    internal static ChatDraft Create(string? agentId, bool useGatewayDefaultAgent = false)
    {
        if (agentId is null && !useGatewayDefaultAgent)
            throw new ArgumentException("A draft requires an agent or an explicit Gateway-default target.", nameof(agentId));
        if (agentId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return new(agentId is null ? $"draft:{Guid.NewGuid():N}" : $"agent:{agentId}:{Guid.NewGuid():N}",
            agentId, useGatewayDefaultAgent);
    }

    internal void RememberPlacementIntent(ChatPlacementIntentSnapshot? intent)
    {
        lock (_placementGate)
            _placementIntent = intent;
    }

    internal Task<SessionCreateResult> CreateGatewaySessionOnceAsync(Func<Task<SessionCreateResult>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        TaskCompletionSource<SessionCreateResult> completion;
        lock (_creationGate)
        {
            if (_gatewaySessionKey is not null)
                return Task.FromResult(new SessionCreateResult { Ok = true, Key = _gatewaySessionKey });
            if (_creationOutcomeUncertain)
                return Task.FromResult(new SessionCreateResult { OutcomeUncertain = true });
            if (_creationTask is not null)
                return _creationTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _creationTask = completion.Task;
        }

        _ = CompleteGatewaySessionCreationAsync(completion, create);
        return completion.Task;
    }

    private async Task CompleteGatewaySessionCreationAsync(
        TaskCompletionSource<SessionCreateResult> completion,
        Func<Task<SessionCreateResult>> create)
    {
        try
        {
            var result = await create().ConfigureAwait(false);
            lock (_creationGate)
            {
                if (result.OutcomeUncertain)
                    _creationOutcomeUncertain = true;
                else if (result.IsSupported && result.Ok && !string.IsNullOrWhiteSpace(result.Key))
                    _gatewaySessionKey = result.Key;
                _creationTask = null;
            }
            completion.TrySetResult(result);
        }
        catch (GatewayRequestNotSentException ex)
        {
            lock (_creationGate)
                _creationTask = null;
            completion.TrySetException(ex);
        }
        catch (Exception ex)
        {
            lock (_creationGate)
            {
                _creationOutcomeUncertain = true;
                _creationTask = null;
            }
            completion.TrySetException(ex);
        }
    }

    internal void MarkCompleted() => IsCompleted = true;

    internal void SetModel(string? model)
    {
        Revision++;
        ModelChanged = true;
        Model = model;
    }

    internal void SetThinkingLevel(string? level)
    {
        Revision++;
        ThinkingChanged = true;
        ThinkingLevel = level;
    }

    /// <summary>Projects locally chosen options without patching an uncreated session.</summary>
    internal ChatThread Project(ChatThread thread) => thread with
    {
        AgentId = AgentId ?? thread.AgentId,
        Model = ModelChanged ? Model : null,
        ModelProvider = null,
        ThinkingLevel = ThinkingChanged ? ThinkingLevel : null
    };

    internal async Task ApplyOptionsAsync(
        IChatComposerRuntimePort port, CancellationToken cancellationToken, Func<bool> isCurrent)
    {
        void CheckCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isCurrent()) throw new OperationCanceledException(cancellationToken);
        }
        if (!ModelChanged && !ThinkingChanged)
            return;
        CheckCurrent();
        var patch = new SessionPatch();
        if (ModelChanged)
            patch.Model = Model is { } model ? model : SessionPatch.Clear;
        if (ThinkingChanged)
            patch.ThinkingLevel = ThinkingLevel is { } level ? level : SessionPatch.Clear;
        await port.PatchDraftOptionsConfirmedAsync(TargetKey, patch, cancellationToken);
    }
}
