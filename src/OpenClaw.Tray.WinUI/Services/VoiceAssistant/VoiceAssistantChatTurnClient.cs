using System.Collections.Concurrent;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClawTray.Services.VoiceAssistant;

public sealed class VoiceAssistantChatTurnClient : IVoiceAssistantChatTurnClient, IDisposable
{
    private const int CanceledIdentityLimit = 32;
    private readonly OpenClawChatDataProvider _provider;
    private readonly ConcurrentDictionary<string, ResponseIdentity> _responses = new(StringComparer.Ordinal);
    private readonly object _canceledGate = new();
    private readonly object _readinessGate = new();
    private readonly Queue<string> _canceledOrder = new();
    private readonly HashSet<string> _canceled = new(StringComparer.Ordinal);
    private VoiceAssistantAvailability _lastAvailability;

    public VoiceAssistantChatTurnClient(OpenClawChatDataProvider provider)
    {
        _provider = provider;
        _provider.Changed += OnProviderChanged;
        _lastAvailability = _provider.GetVoiceAssistantAvailability();
        _provider.VoiceAssistantResponseObserved += OnResponseObserved;
        _provider.VoiceAssistantTurnInvalidated += OnTurnInvalidated;
    }

    public event Action? ReadinessChanged;
    public event Action<VoiceAssistantTurnInvalidation>? TurnInvalidated;

    public string? GetReadySessionKey() => _provider.GetVoiceAssistantReadySessionKey();
    public VoiceAssistantAvailability GetAvailability() => _provider.GetVoiceAssistantAvailability();

    public Task<VoiceAssistantTurnReceipt> SendAsync(
        string sessionKey,
        string request,
        CancellationToken cancellationToken) =>
        _provider.SendVoiceAssistantMessageAsync(sessionKey, request, cancellationToken);

    public async Task CancelAsync(
        VoiceAssistantTurnReceipt receipt,
        CancellationToken cancellationToken)
    {
        RememberCanceled(receipt.LocalMessageId);
        _responses.TryRemove(receipt.LocalMessageId, out _);
        await _provider.CancelVoiceAssistantTurnAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    public bool IsTurnInvalidated(VoiceAssistantTurnReceipt receipt)
    {
        lock (_canceledGate)
            return _canceled.Contains(receipt.LocalMessageId);
    }

    public bool TryTakeBufferedResponse(
        VoiceAssistantTurnReceipt receipt,
        out string responseText)
    {
        responseText = string.Empty;
        if (!_responses.TryGetValue(receipt.LocalMessageId, out var response) ||
            !string.Equals(response.SessionKey, receipt.SessionKey, StringComparison.Ordinal) ||
            !string.Equals(response.GatewayRunId, receipt.GatewayRunId, StringComparison.Ordinal))
        {
            return false;
        }

        if (response.GatewaySequence is { } sequence &&
            receipt.PreSendSequence is { } baseline &&
            sequence <= baseline)
        {
            _responses.TryRemove(
                new KeyValuePair<string, ResponseIdentity>(receipt.LocalMessageId, response));
            return false;
        }

        if (!_responses.TryRemove(
            new KeyValuePair<string, ResponseIdentity>(receipt.LocalMessageId, response)))
        {
            return false;
        }

        responseText = response.ResponseText;
        return true;
    }

    public bool IsResponseForTurn(
        VoiceAssistantTurnReceipt receipt,
        OpenClawNotification notification)
    {
        if (!_responses.TryGetValue(receipt.LocalMessageId, out var response) ||
            !string.Equals(response.SessionKey, notification.SessionKey, StringComparison.Ordinal) ||
            !string.Equals(response.GatewayRunId, receipt.GatewayRunId, StringComparison.Ordinal))
        {
            return false;
        }

        bool matches;
        if (!string.IsNullOrWhiteSpace(response.GatewayMessageId) &&
            response.GatewaySequence is { } gatewaySequence)
        {
            matches = string.Equals(
                    response.GatewayMessageId,
                    notification.OpenClawId,
                    StringComparison.Ordinal) &&
                gatewaySequence == notification.OpenClawSeq;
        }
        else if (!string.IsNullOrWhiteSpace(notification.OpenClawId) ||
                 notification.OpenClawSeq is not null)
        {
            matches = false;
        }
        else
        {
            matches = string.Equals(
                response.ResponseText,
                notification.FullMessage ?? notification.Message,
                StringComparison.Ordinal);
        }

        if (matches)
            _responses.TryRemove(receipt.LocalMessageId, out _);
        return matches;
    }

    private void OnResponseObserved(OpenClawChatDataProvider.VoiceAssistantResponseIdentity response)
    {
        lock (_canceledGate)
        {
            if (_canceled.Contains(response.LocalMessageId))
                return;
        }

        _responses[response.LocalMessageId] = new ResponseIdentity(
            response.SessionKey,
            response.GatewayRunId,
            response.GatewayMessageId,
            response.GatewaySequence,
            response.ResponseText);
    }

    private void OnTurnInvalidated(OpenClawChatDataProvider.VoiceAssistantTurnInvalidation invalidation)
    {
        RememberCanceled(invalidation.LocalMessageId);
        _responses.TryRemove(invalidation.LocalMessageId, out _);
        TurnInvalidated?.Invoke(new VoiceAssistantTurnInvalidation(
            invalidation.SessionKey,
            invalidation.GatewayRunId,
            invalidation.LocalMessageId));
    }

    private void OnProviderChanged(object? sender, ChatDataChangedEventArgs args)
    {
        var availability = _provider.GetVoiceAssistantAvailability();
        lock (_readinessGate)
        {
            if (HasSameReadiness(_lastAvailability, availability))
            {
                _lastAvailability = availability;
                return;
            }

            _lastAvailability = availability;
        }

        ReadinessChanged?.Invoke();
    }

    private static bool HasSameReadiness(
        VoiceAssistantAvailability left,
        VoiceAssistantAvailability right) =>
        left.IsUsable == right.IsUsable &&
        left.CanSendDirectly == right.CanSendDirectly &&
        string.Equals(left.ActiveRunId, right.ActiveRunId, StringComparison.Ordinal);

    private void RememberCanceled(string localMessageId)
    {
        lock (_canceledGate)
        {
            if (!_canceled.Add(localMessageId))
                return;

            _canceledOrder.Enqueue(localMessageId);
            while (_canceledOrder.Count > CanceledIdentityLimit)
                _canceled.Remove(_canceledOrder.Dequeue());
        }
    }

    public void Dispose()
    {
        _provider.Changed -= OnProviderChanged;
        _provider.VoiceAssistantResponseObserved -= OnResponseObserved;
        _provider.VoiceAssistantTurnInvalidated -= OnTurnInvalidated;
        _responses.Clear();
        lock (_canceledGate)
        {
            _canceled.Clear();
            _canceledOrder.Clear();
        }
    }

    private sealed record ResponseIdentity(
        string SessionKey,
        string GatewayRunId,
        string? GatewayMessageId,
        int? GatewaySequence,
        string ResponseText);
}
