using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using System.Text.Json;

namespace OpenClawTray.Chat;

internal static class ChatWelcomePresentation
{
    internal const int RecentChatLimit = 5;

    internal static string LastUsed(DateTimeOffset? updated, DateTimeOffset now)
    {
        if (updated is not { } value) return "";
        var age = now - value;
        if (age.TotalMinutes < 1) return LocalizationHelper.GetString("TimeAgo_JustNow");
        if (age.TotalHours < 1) return string.Format(LocalizationHelper.GetString("TimeAgo_MinutesFormat"), (int)age.TotalMinutes);
        if (age.TotalHours < 48) return string.Format(LocalizationHelper.GetString("TimeAgo_HoursFormat"), (int)age.TotalHours);
        if (age.TotalDays < 30) return string.Format(LocalizationHelper.GetString("TimeAgo_DaysFormat"), (int)age.TotalDays);
        return value.ToLocalTime().ToString("d");
    }

    internal static WorkspaceAgent AgentIdentity(ChatPlacementCatalog? catalog, string? agentId,
        ChatAgentIdentity? hydrated = null, ChatAgentPresentation? currentRoster = null)
    {
        var id = agentId ?? "main";
        if (currentRoster is { } current &&
            string.Equals(current.AgentId, id, StringComparison.Ordinal))
        {
            return AgentAvatarPresentation.Compose(
                id, null, current.Name, null, current.Emoji, null, current.AvatarUrl, null);
        }

        var roster = catalog?.AgentId == id ? catalog : null;
        return AgentAvatarPresentation.Compose(id, null, roster?.AgentName, null, roster?.AgentEmoji,
            roster?.AgentAvatar, roster?.AgentAvatarUrl, hydrated);
    }

    internal static ChatAgentIdentity ParseAgentIdentity(string requestedAgentId, JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            ReadString(response, "agentId") is not { } responseAgentId ||
            !string.Equals(responseAgentId, requestedAgentId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Gateway returned an identity for a different agent.");
        }

        var avatar = ReadString(response, "avatar");
        return new ChatAgentIdentity(
            requestedAgentId,
            ReadString(response, "name"),
            AgentAvatarPresentation.ImageUrl(avatar),
            AgentAvatarPresentation.TextAvatar(ReadString(response, "emoji")) ??
                AgentAvatarPresentation.TextAvatar(avatar));
    }

    private static string? ReadString(JsonElement response, string name) =>
        response.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    internal static IReadOnlyList<ChatThread> RecentChats(
        IEnumerable<ChatThread> threads,
        string? currentThreadId,
        string? agentId) =>
        threads.Where(thread => (thread.Activity != ChatActivity.Idle
                    || thread.InputTokens > 0 || thread.OutputTokens > 0 || thread.TotalTokens > 0)
                && !string.Equals(thread.Id, currentThreadId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(thread.Id)
                && !string.IsNullOrWhiteSpace(thread.Title)
                && !thread.IsBackground
                && !thread.IsArchived
                && !thread.IsChannelSession
                && string.Equals(thread.AgentId ?? "main", agentId ?? "main", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(thread => thread.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(thread => thread.Id, StringComparer.Ordinal)
            .Take(RecentChatLimit)
            .ToArray();
}
