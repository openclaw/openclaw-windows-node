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
    internal static JsonObject? ProjectToolArgs(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.Object } args)
            return NativeToolProjector.ExtractSafeToolDisplayArgs(args);

        if (value is not { ValueKind: JsonValueKind.String } encoded)
            return null;

        var json = encoded.GetString();
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? NativeToolProjector.ExtractSafeToolDisplayArgs(document.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
                    Array.Empty<ChatMessageContentPartInfo>(),
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
