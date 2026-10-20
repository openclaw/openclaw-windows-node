using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;

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

    internal static WorkspaceAgent AgentIdentity(ChatPlacementCatalog? catalog, string? agentId)
    {
        var id = agentId ?? "main";
        if (catalog?.AgentId != id)
            return new(id, id, null);
        return new(id, string.IsNullOrWhiteSpace(catalog.AgentName) ? id : catalog.AgentName, null,
            catalog.AgentEmoji, catalog.AgentAvatarUrl);
    }

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
