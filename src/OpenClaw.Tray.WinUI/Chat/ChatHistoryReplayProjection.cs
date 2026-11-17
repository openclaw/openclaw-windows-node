using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ChatHistoryReplayPart(
    ChatMessageInfo Message,
    string Text,
    IReadOnlyList<ChatToolContentInfo> ToolContent,
    IReadOnlyList<ChatMessageContentPartInfo> AssistantContentParts,
    bool IsFirstPart,
    ChatMessagePresentation? Presentation = null,
    bool IsReasoning = false);

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
        IEnumerable<ChatMessageInfo> messages)
    {
        foreach (var message in messages)
        {
            var resultWithTextParts = message.ToolContent.Count > 0 &&
                message.Role.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() is "toolresult" or "tool" or "function" &&
                message.ContentParts.All(part => part.Kind == ChatMessageContentPartKind.Text);
            if (message.ContentParts.Count == 0 || resultWithTextParts)
            {
                yield return new ChatHistoryReplayPart(
                    message,
                    message.Text ?? string.Empty,
                    message.ToolContent,
                    Array.Empty<ChatMessageContentPartInfo>(),
                    IsFirstPart: true,
                    Presentation: message.Presentation);
                continue;
            }

            var isFirstPart = true;
            var contentIndex = 0;
            var hasPhasedParts = message.ContentParts.Any(part => part.Phase is not null);
            for (var index = 0; index < message.ContentParts.Count; index++)
            {
                var part = message.ContentParts[index];
                var presentation = message.Presentation with
                {
                    Phase = hasPhasedParts ? part.Phase : part.Phase ?? message.Presentation.Phase,
                    BlockId = part.TextSignatureId ?? (message.Presentation.MessageId is null ? null :
                        part.Kind == ChatMessageContentPartKind.Reasoning ? "content:thinking" : $"content:{contentIndex}"),
                    TurnBoundary = isFirstPart && message.Presentation.TurnBoundary,
                };
                if (part.Kind != ChatMessageContentPartKind.Reasoning)
                    contentIndex++;
                if (part.Kind == ChatMessageContentPartKind.Reasoning)
                {
                    yield return new ChatHistoryReplayPart(
                        message, part.Text ?? "", [], [], isFirstPart,
                        presentation with { Phase = null }, IsReasoning: true);
                    isFirstPart = false;
                    continue;
                }
                if (part.Kind == ChatMessageContentPartKind.Text)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        part.Text ?? string.Empty,
                        Array.Empty<ChatToolContentInfo>(),
                        new[] { part },
                        isFirstPart,
                        presentation);
                    isFirstPart = false;
                }
                else if (part.Tool is { } tool)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        string.Empty,
                        new[] { tool },
                        Array.Empty<ChatMessageContentPartInfo>(),
                        isFirstPart,
                        presentation);
                    isFirstPart = false;
                }
                else if (part.Kind == ChatMessageContentPartKind.Media && part.Media is not null)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        string.Empty,
                        Array.Empty<ChatToolContentInfo>(),
                        new[] { part },
                        isFirstPart,
                        presentation);
                    isFirstPart = false;
                }
            }
        }
    }
}
