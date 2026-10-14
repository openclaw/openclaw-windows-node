using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ChatHistoryReplayPart(
    ChatMessageInfo Message,
    string Text,
    IReadOnlyList<ChatToolContentInfo> ToolContent,
    IReadOnlyList<ChatMessageContentPartInfo> AssistantContentParts,
    bool IsFirstPart);

internal static class ChatHistoryReplayProjection
{
    internal static JsonObject? ProjectToolArgs(JsonElement? value) =>
        NativeToolProjector.ExtractSafePersistedToolDisplayArgs(value);

    internal static string ToolLabel(string toolName, JsonObject? args)
    {
        var label = NativeToolProjector.FirstToolDisplayValue(args);
        if (string.IsNullOrWhiteSpace(label))
            return toolName;
        if (label.Length <= 80)
            return label;
        var length = 77;
        if (char.IsHighSurrogate(label[length - 1]))
            length--;
        return label[..length] + "\u2026";
    }

    public static IEnumerable<ChatHistoryReplayPart> Project(
        IEnumerable<ChatMessageInfo> messages,
        CancellationToken cancellationToken = default)
    {
        // Finite in-enumeration checks counted by ACTUAL yielded parts (not outer messages only),
        // checked while yielding rather than only around the caller's ToArray.
        var interval = OpenClaw.Shared.HistoryPagingBudget.DefaultItemsPerTick;
        var sincePartCheck = 0;
        void TickPart()
        {
            if (++sincePartCheck >= interval)
            {
                sincePartCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        foreach (var message in messages)
        {
            TickPart();
            if (message.ContentParts.Count == 0)
            {
                yield return new ChatHistoryReplayPart(
                    message,
                    message.Text ?? string.Empty,
                    message.ToolContent,
                    Array.Empty<ChatMessageContentPartInfo>(),
                    IsFirstPart: true);
                continue;
            }

            var isFirstPart = true;
            foreach (var part in message.ContentParts)
            {
                TickPart(); // count ACTUAL parts, not outer messages
                if (part.Kind == ChatMessageContentPartKind.Text)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        part.Text ?? string.Empty,
                        Array.Empty<ChatToolContentInfo>(),
                        new[] { part },
                        isFirstPart);
                    isFirstPart = false;
                }
                else if (part.Tool is { } tool)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        string.Empty,
                        new[] { tool },
                        Array.Empty<ChatMessageContentPartInfo>(),
                        isFirstPart);
                    isFirstPart = false;
                }
                else if (part.Kind == ChatMessageContentPartKind.Media && part.Media is not null)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        string.Empty,
                        Array.Empty<ChatToolContentInfo>(),
                        new[] { part },
                        isFirstPart);
                    isFirstPart = false;
                }
            }
        }
    }
}
