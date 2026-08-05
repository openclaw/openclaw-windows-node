using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ChatHistoryReplayPart(
    ChatMessageInfo Message,
    string Text,
    IReadOnlyList<ChatToolContentInfo> ToolContent,
    bool IsFirstTextPart);

internal static class ChatHistoryReplayProjection
{
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
                    IsFirstTextPart: true);
                continue;
            }

            var isFirstTextPart = true;
            foreach (var part in message.ContentParts)
            {
                if (part.Kind == ChatMessageContentPartKind.Text)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        part.Text ?? string.Empty,
                        Array.Empty<ChatToolContentInfo>(),
                        isFirstTextPart);
                    isFirstTextPart = false;
                }
                else if (part.Tool is { } tool)
                {
                    yield return new ChatHistoryReplayPart(
                        message,
                        string.Empty,
                        new[] { tool },
                        IsFirstTextPart: false);
                }
            }
        }
    }
}
