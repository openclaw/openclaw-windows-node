using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ChatHistoryReplayPart(
    ChatMessageInfo Message,
    string Text,
    IReadOnlyList<ChatToolContentInfo> ToolContent,
    bool IsFirstPart);

internal static class ChatHistoryReplayProjection
{
    internal static JsonObject? ProjectToolArgs(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } args
            ? NativeToolProjector.ExtractSafeToolDisplayArgs(args)
            : null;

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
        IEnumerable<ChatMessageInfo> messages)
    {
        foreach (var message in messages)
        {
            if (message.ContentParts.Count == 0)
            {
                yield return new ChatHistoryReplayPart(
                    message,
                    message.Text ?? string.Empty,
                    message.ToolContent,
                    IsFirstPart: true);
                continue;
            }

            var isFirstPart = true;
            foreach (var part in message.ContentParts)
            {
                if (part.Kind == ChatMessageContentPartKind.Text)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        part.Text ?? string.Empty,
                        Array.Empty<ChatToolContentInfo>(),
                        isFirstPart);
                    isFirstPart = false;
                }
                else if (part.Tool is { } tool)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        string.Empty,
                        new[] { tool },
                        isFirstPart);
                    isFirstPart = false;
                }
            }
        }
    }
}
