using OpenClaw.Shared;
using System.Security.Cryptography;
using System.Text;

namespace OpenClawTray.Services.VoiceAssistant;

public readonly record struct VoiceAssistantConfiguration(
    bool Enabled,
    bool LocalPrerequisitesReady,
    string WakePhrase);

public sealed class VoiceAssistantCoordinator : IAsyncDisposable
{
    private const int ConsumedIdentityLimit = 32;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly IVoiceAssistantInput _input;
    private readonly IVoiceAssistantChatTurnClient _chat;
    private readonly IVoiceAssistantSpeaker _speaker;
    private readonly Func<VoiceAssistantConfiguration> _configuration;
    private readonly TimeSpan _replyTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<string> _consumedOrder = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly Queue<VoiceAssistantState> _pendingStateNotifications = new();
    private CancellationTokenSource? _turnCancellation;
    private CancellationTokenSource? _replyTimeoutCancellation;
    private VoiceAssistantTurnReceipt? _activeTurn;
    private VoiceAssistantState _state = VoiceAssistantState.Off;
    private bool _stateNotificationDrainActive;
    private bool _disposed;

    public VoiceAssistantCoordinator(
        IVoiceAssistantInput input,
        IVoiceAssistantChatTurnClient chat,
        IVoiceAssistantSpeaker speaker,
        Func<VoiceAssistantConfiguration> configuration,
        TimeSpan? replyTimeout = null)
    {
        _input = input;
        _chat = chat;
        _speaker = speaker;
        _configuration = configuration;
        _replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(120);
        _input.UtteranceCompleted += OnUtteranceCompleted;
        _input.CaptureAvailable += OnCaptureAvailable;
        _chat.ReadinessChanged += OnChatReadinessChanged;
        _chat.TurnInvalidated += OnTurnInvalidated;
    }

    public VoiceAssistantState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public event Action<VoiceAssistantState>? StateChanged;

    public Task ReconcileAsync() => ReconcileCoreAsync();

    public bool TryClaimResponse(OpenClawNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        string? identity = GetIdentity(notification);
        var fallbackSignature = GetFallbackSignature(notification);
        VoiceAssistantTurnReceipt? receipt;
        CancellationToken turnCancellation;
        bool drainStateNotifications;

        lock (_gate)
        {
            if (identity is not null && _consumed.Contains(identity))
                return true;

            receipt = _activeTurn;
            var turnScopedFallbackIdentity = receipt is null
                ? null
                : GetFallbackIdentity(
                    fallbackSignature,
                    notification.SessionKey,
                    receipt.LocalMessageId);
            if (turnScopedFallbackIdentity is not null &&
                _consumed.Contains(turnScopedFallbackIdentity))
                return true;

            if (_state != VoiceAssistantState.WaitingForReply ||
                receipt is null ||
                string.IsNullOrWhiteSpace(notification.SessionKey) ||
                !string.Equals(notification.SessionKey, receipt.SessionKey, StringComparison.Ordinal) ||
                notification.OpenClawSeq is { } sequence &&
                    receipt.PreSendSequence is { } baseline &&
                    sequence <= baseline ||
                !_chat.IsResponseForTurn(receipt, notification))
            {
                return false;
            }

            identity ??= turnScopedFallbackIdentity;
            if (identity is null)
                return false;

            AddConsumedLocked(identity);
            drainStateNotifications = SetStateLocked(VoiceAssistantState.Speaking);
            turnCancellation = _turnCancellation?.Token ?? _lifetime.Token;
            _replyTimeoutCancellation?.Cancel();
        }

        if (drainStateNotifications)
            DrainStateNotifications();
        _ = SpeakAndResumeAsync(
            notification.FullMessage ?? notification.Message,
            receipt,
            turnCancellation);
        return true;
    }

    private void OnUtteranceCompleted(string transcript)
    {
        string request;
        CancellationTokenSource turnCancellation;
        bool drainStateNotifications;

        lock (_gate)
        {
            if (_disposed ||
                _state != VoiceAssistantState.WakeListening ||
                !VoiceWakeGate.TryExtractRequest(
                    transcript,
                    _configuration().WakePhrase,
                    out request))
            {
                return;
            }

            drainStateNotifications = SetStateLocked(VoiceAssistantState.Dispatching);
            _turnCancellation?.Cancel();
            _turnCancellation?.Dispose();
            turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _turnCancellation = turnCancellation;
        }

        if (drainStateNotifications)
            DrainStateNotifications();
        _ = DispatchAsync(request, turnCancellation.Token);
    }

    private async Task DispatchAsync(string request, CancellationToken cancellationToken)
    {
        try
        {
            await _input.StopAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var sessionKey = _chat.GetReadySessionKey();
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                await RecoverAsync(VoiceAssistantState.Unavailable).ConfigureAwait(false);
                return;
            }

            var receipt = await _chat.SendAsync(sessionKey, request, cancellationToken).ConfigureAwait(false);
            if (receipt.Disposition == VoiceAssistantSendDisposition.Terminated)
            {
                await RecoverAsync(VoiceAssistantState.WakeListening).ConfigureAwait(false);
                return;
            }

            if (receipt.Disposition != VoiceAssistantSendDisposition.Direct)
            {
                await _chat.CancelAsync(receipt, cancellationToken).ConfigureAwait(false);
                await RecoverAsync(
                    receipt.Disposition == VoiceAssistantSendDisposition.Untrackable
                        ? VoiceAssistantState.Error
                        : VoiceAssistantState.Unavailable).ConfigureAwait(false);
                return;
            }

            CancellationTokenSource? timeoutCancellation = null;
            var invalidatedBeforeRegistration = false;
            var drainStateNotifications = false;
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    return;

                invalidatedBeforeRegistration = _chat.IsTurnInvalidated(receipt);
                if (!invalidatedBeforeRegistration)
                {
                    _activeTurn = receipt;
                    drainStateNotifications = SetStateLocked(VoiceAssistantState.WaitingForReply);
                    _replyTimeoutCancellation?.Cancel();
                    _replyTimeoutCancellation?.Dispose();
                    timeoutCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _replyTimeoutCancellation = timeoutCancellation;
                }
            }

            if (invalidatedBeforeRegistration)
            {
                await RecoverAsync(VoiceAssistantState.WakeListening).ConfigureAwait(false);
                return;
            }

            var registeredTimeout = timeoutCancellation
                ?? throw new InvalidOperationException("Voice Assistant reply timeout was not registered.");
            if (drainStateNotifications)
                DrainStateNotifications();

            if (_chat.TryTakeBufferedResponse(receipt, out var bufferedResponse))
            {
                var speakBufferedResponse = false;
                var drainSpeaking = false;
                CancellationToken bufferedTurnCancellation;
                lock (_gate)
                {
                    bufferedTurnCancellation = _turnCancellation?.Token ?? _lifetime.Token;
                    speakBufferedResponse = ReferenceEquals(_activeTurn, receipt) &&
                        _state == VoiceAssistantState.WaitingForReply &&
                        !bufferedTurnCancellation.IsCancellationRequested;
                    if (speakBufferedResponse)
                    {
                        drainSpeaking = SetStateLocked(VoiceAssistantState.Speaking);
                        registeredTimeout.Cancel();
                    }
                }

                if (drainSpeaking)
                    DrainStateNotifications();
                if (speakBufferedResponse)
                    _ = SpeakAndResumeAsync(bufferedResponse, receipt, bufferedTurnCancellation);
                return;
            }

            try
            {
                await Task.Delay(_replyTimeout, registeredTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (registeredTimeout.IsCancellationRequested)
            {
                return;
            }
            await RecoverAsync(VoiceAssistantState.Error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await RecoverAsync(VoiceAssistantState.Error).ConfigureAwait(false);
        }
    }

    private async Task SpeakAndResumeAsync(
        string text,
        VoiceAssistantTurnReceipt receipt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _speaker.SpeakAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            await RecoverAsync(VoiceAssistantState.Error).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_activeTurn, receipt))
                return;
        }

        await RecoverAsync(VoiceAssistantState.WakeListening).ConfigureAwait(false);
    }

    private async Task RecoverAsync(VoiceAssistantState transientState)
    {
        VoiceAssistantTurnReceipt? receipt;
        var drainStateNotifications = false;
        lock (_gate)
        {
            receipt = _activeTurn;
            _activeTurn = null;
            _turnCancellation?.Cancel();
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            _replyTimeoutCancellation?.Cancel();
            _replyTimeoutCancellation?.Dispose();
            _replyTimeoutCancellation = null;
            if (!_disposed)
                drainStateNotifications = SetStateLocked(transientState);
        }

        if (drainStateNotifications)
            DrainStateNotifications();

        if (receipt is not null && transientState != VoiceAssistantState.WakeListening)
        {
            try
            {
                await _chat.CancelAsync(receipt, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        await ReconcileCoreAsync().ConfigureAwait(false);
    }

    private async Task ReconcileCoreAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            VoiceAssistantConfiguration configuration;
            VoiceAssistantAvailability availability;
            VoiceAssistantState target;
            VoiceAssistantTurnReceipt? canceledTurn = null;
            bool drainStateNotifications;
            lock (_gate)
            {
                if (_disposed)
                    return;

                configuration = _configuration();
                availability = _chat.GetAvailability();
                target = !configuration.Enabled
                    ? VoiceAssistantState.Off
                    : !configuration.LocalPrerequisitesReady ||
                      !availability.IsUsable ||
                      !availability.CanSendDirectly
                        ? VoiceAssistantState.Unavailable
                        : VoiceAssistantState.Starting;

                if (_state is VoiceAssistantState.Dispatching or
                    VoiceAssistantState.WaitingForReply or
                    VoiceAssistantState.Speaking)
                {
                    var ownedRunStillCurrent = _activeTurn?.GatewayRunId is { Length: > 0 } ownedRunId &&
                        (string.IsNullOrWhiteSpace(availability.ActiveRunId) ||
                         string.Equals(availability.ActiveRunId, ownedRunId, StringComparison.Ordinal));
                    if (configuration.Enabled &&
                        configuration.LocalPrerequisitesReady &&
                        availability.IsUsable &&
                        (_state == VoiceAssistantState.Dispatching || ownedRunStillCurrent))
                    {
                        return;
                    }

                    _turnCancellation?.Cancel();
                    _turnCancellation?.Dispose();
                    _turnCancellation = null;
                    _replyTimeoutCancellation?.Cancel();
                    _replyTimeoutCancellation?.Dispose();
                    _replyTimeoutCancellation = null;
                    canceledTurn = _activeTurn;
                    _activeTurn = null;
                }

                drainStateNotifications = SetStateLocked(target);
            }

            if (drainStateNotifications)
                DrainStateNotifications();
            await _input.StopAsync().ConfigureAwait(false);
            if (canceledTurn is not null)
                await _chat.CancelAsync(canceledTurn, _lifetime.Token).ConfigureAwait(false);

            if (target != VoiceAssistantState.Starting)
                return;

            try
            {
                await _input.StartAsync(_lifetime.Token).ConfigureAwait(false);
                bool drainWakeListening;
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    drainWakeListening = SetStateLocked(VoiceAssistantState.WakeListening);
                }
                if (drainWakeListening)
                    DrainStateNotifications();
            }
            catch (VoiceCaptureBusyException)
            {
                bool drainUnavailable;
                lock (_gate)
                    drainUnavailable = SetStateLocked(VoiceAssistantState.Unavailable);
                if (drainUnavailable)
                    DrainStateNotifications();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch
            {
                bool drainError;
                lock (_gate)
                    drainError = SetStateLocked(VoiceAssistantState.Error);
                if (drainError)
                    DrainStateNotifications();
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void OnCaptureAvailable() => _ = ReconcileCoreAsync();

    private void OnChatReadinessChanged() => _ = ReconcileCoreAsync();

    private void OnTurnInvalidated(VoiceAssistantTurnInvalidation invalidation)
    {
        var changed = false;
        var drainStateNotifications = false;
        lock (_gate)
        {
            if (_disposed ||
                _activeTurn is not { } activeTurn ||
                !string.Equals(activeTurn.SessionKey, invalidation.SessionKey, StringComparison.Ordinal) ||
                !string.Equals(activeTurn.GatewayRunId, invalidation.GatewayRunId, StringComparison.Ordinal) ||
                !string.Equals(activeTurn.LocalMessageId, invalidation.LocalMessageId, StringComparison.Ordinal))
            {
                return;
            }

            _activeTurn = null;
            _turnCancellation?.Cancel();
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            _replyTimeoutCancellation?.Cancel();
            _replyTimeoutCancellation?.Dispose();
            _replyTimeoutCancellation = null;
            drainStateNotifications = SetStateLocked(VoiceAssistantState.Unavailable);
            changed = true;
        }

        if (changed)
        {
            if (drainStateNotifications)
                DrainStateNotifications();
            _ = ReconcileCoreAsync();
        }
    }

    private bool SetStateLocked(VoiceAssistantState state)
    {
        _state = state;
        _pendingStateNotifications.Enqueue(state);
        if (_stateNotificationDrainActive)
            return false;

        _stateNotificationDrainActive = true;
        return true;
    }

    private void DrainStateNotifications()
    {
        while (true)
        {
            VoiceAssistantState state;
            lock (_gate)
            {
                if (_pendingStateNotifications.Count == 0)
                {
                    _stateNotificationDrainActive = false;
                    return;
                }

                state = _pendingStateNotifications.Dequeue();
            }

            StateChanged?.Invoke(state);
        }
    }

    private static string? GetIdentity(OpenClawNotification notification) =>
        !string.IsNullOrWhiteSpace(notification.SessionKey) &&
        !string.IsNullOrWhiteSpace(notification.OpenClawId) &&
        notification.OpenClawSeq is { } sequence
            ? $"{notification.SessionKey}\n{notification.OpenClawId}\n{sequence}"
            : null;

    private static string? GetFallbackSignature(OpenClawNotification notification)
    {
        var text = notification.FullMessage ?? notification.Message;
        if (string.IsNullOrWhiteSpace(notification.SessionKey) ||
            string.IsNullOrEmpty(text))
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return hash;
    }

    private static string? GetFallbackIdentity(
        string? fallbackSignature,
        string? sessionKey,
        string localMessageId) =>
        fallbackSignature is null || string.IsNullOrWhiteSpace(sessionKey)
            ? null
            : $"fallback:{sessionKey}\n{localMessageId}\n{fallbackSignature}";

    private void AddConsumedLocked(string identity)
    {
        if (!_consumed.Add(identity))
            return;

        _consumedOrder.Enqueue(identity);
        while (_consumedOrder.Count > ConsumedIdentityLimit)
            _consumed.Remove(_consumedOrder.Dequeue());
    }

    public async ValueTask DisposeAsync()
    {
        VoiceAssistantTurnReceipt? activeTurn;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _state = VoiceAssistantState.Off;
            _turnCancellation?.Cancel();
            _replyTimeoutCancellation?.Cancel();
            activeTurn = _activeTurn;
            _activeTurn = null;
        }

        _input.UtteranceCompleted -= OnUtteranceCompleted;
        _input.CaptureAvailable -= OnCaptureAvailable;
        _chat.ReadinessChanged -= OnChatReadinessChanged;
        _chat.TurnInvalidated -= OnTurnInvalidated;
        _lifetime.Cancel();
        await _input.StopAsync().ConfigureAwait(false);
        if (activeTurn is not null)
            await _chat.CancelAsync(activeTurn, CancellationToken.None).ConfigureAwait(false);
        _replyTimeoutCancellation?.Dispose();
        _turnCancellation?.Dispose();
        _lifetime.Dispose();
    }
}
