using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>A lease supplied by the actual originating conversation owner, not by action content.</summary>
public interface IChatInputOrigin
{
    IOperatorGatewayClient Client { get; }
    long ConnectionEpoch { get; }
    string SessionKey { get; }
    string? SessionId { get; }
    string? AgentId { get; }
    string? RunId { get; }
    CancellationToken CancellationToken { get; }
    /// <summary>
    /// Bounded synchronous read, also invoked under the socket send semaphore.
    /// Never send, wait for a send, or acquire a lock held while blocking on a send.
    /// </summary>
    bool IsCurrent { get; }
    /// <summary>Only the actual conversation producer may qualify an owned materialization transition.</summary>
    Task<IChatInputOrigin?> ContinueMaterializationAsync(
        ChatPlacementMaterializationReceipt receipt, CancellationToken cancellationToken) =>
        Task.FromResult<IChatInputOrigin?>(null);
}

public enum ChatInputAction
{
    Send,
    Queue,
    Steer,
    Followup,
    Collect,
    Interrupt,
}

public enum ChatInputSubmissionState
{
    Accepted,
    Queued,
    Unconfirmed,
    Rejected,
    Stale,
}

public sealed record ChatInputSubmissionResult(
    ChatInputSubmissionState State,
    string? MessageId = null,
    string? Error = null)
{
    public bool Accepted => State is ChatInputSubmissionState.Accepted or ChatInputSubmissionState.Queued;
    public bool OwnsPayload => Accepted || State == ChatInputSubmissionState.Unconfirmed;
}

/// <summary>Connection-bound options for the additive input-custody contract.</summary>
public sealed record ChatSendOptions(
    bool UseInputCustody,
    string? QueueMode = null,
    string? AgentId = null,
    IChatInputOrigin? Origin = null,
    Func<bool>? IsCurrent = null);

public sealed class ChatSendDeliveryException(
    string message,
    bool deliveryUncertain,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public bool DeliveryUncertain { get; } = deliveryUncertain;
}

public sealed class ChatSendRejectedException(
    string message,
    string? code = null) : InvalidOperationException(message)
{
    public string? Code { get; } = code;
}

public sealed class ChatMutationDeliveryException(
    string message, bool mayHaveApplied, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public bool MayHaveApplied { get; } = mayHaveApplied;
}

public sealed record ChatInputReceipt(
    string RunId,
    string State,
    bool Queued = false,
    bool Cancelled = false,
    string? ConsumedByEventId = null);

public sealed record ChatPendingInput(
    string Id,
    string? RunId,
    JsonElement Message,
    string State,
    double? AcceptedAt,
    bool Queued = false);

public sealed record ChatPendingInputPage(
    IReadOnlyList<ChatPendingInput> Items, int Total, int? QueuedCount, int? NextBefore);

public static class ChatInputCustody
{
    public const string Capability = "chat-input-custody-v1";
    public const int MaxReceiptRunIds = 50;
    public const int MaxRunIdCharacters = 256;

    public static bool IsQueueMode(string value) =>
        value is "steer" or "followup" or "collect" or "interrupt";

    public static string? WireMode(ChatInputAction action) => action switch
    {
        ChatInputAction.Steer => "steer",
        ChatInputAction.Followup => "followup",
        ChatInputAction.Collect => "collect",
        ChatInputAction.Interrupt => "interrupt",
        ChatInputAction.Send or ChatInputAction.Queue => null,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    internal static void ReadHistory(JsonElement payload, ChatHistoryInfo history)
    {
        if (payload.TryGetProperty("inputReceipts", out var receipts))
        {
            if (receipts.ValueKind != JsonValueKind.Array || receipts.GetArrayLength() > MaxReceiptRunIds)
                Diagnose(history);
            else
            {
                var parsed = new List<ChatInputReceipt>();
                foreach (var receipt in receipts.EnumerateArray())
                {
                    var runId = String(receipt, "runId");
                    var state = String(receipt, "state");
                    var consumedBy = String(receipt, "consumedByEventId");
                    if (string.IsNullOrWhiteSpace(runId) || runId.Length > MaxRunIdCharacters ||
                        state is not ("pending" or "consumed") ||
                        state == "consumed" && string.IsNullOrWhiteSpace(consumedBy))
                    {
                        Diagnose(history);
                        continue;
                    }
                    parsed.Add(new(runId, state, Flag(receipt, "queued"), Flag(receipt, "cancelled"), consumedBy));
                }
                history.InputReceipts = parsed;
            }
        }
        if (payload.TryGetProperty("pendingInputs", out var pending))
        {
            var total = pending.ValueKind == JsonValueKind.Object ? Integer(pending, "total") : null;
            if (pending.ValueKind != JsonValueKind.Object ||
                !pending.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() > 20 || total is null or < 0)
            {
                Diagnose(history);
                return;
            }
            var inputs = new List<ChatPendingInput>();
            foreach (var item in items.EnumerateArray())
            {
                var id = String(item, "id");
                var state = String(item, "state");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(state) ||
                    state is not ("queued" or "cancelled" or "interrupted") ||
                    !item.TryGetProperty("message", out var message) ||
                    !item.TryGetProperty("acceptedAt", out var at) || at.ValueKind != JsonValueKind.Number ||
                    !at.TryGetDouble(out var acceptedAt))
                {
                    Diagnose(history);
                    continue;
                }
                inputs.Add(new(id, String(item, "runId"), message.Clone(), state, acceptedAt, Flag(item, "queued")));
            }
            history.PendingInputs = new(inputs, total.Value,
                Integer(pending, "queuedCount"), Integer(pending, "nextBefore"));
        }
    }

    private static void Diagnose(ChatHistoryInfo history) =>
        history.InputCustodyError = "The Gateway returned malformed input-custody data. The transcript remains available.";

    private static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    private static bool Flag(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.True;

    private static int? Integer(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : null;
}
