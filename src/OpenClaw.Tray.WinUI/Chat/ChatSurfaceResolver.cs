using OpenClawTray.Helpers;

namespace OpenClawTray.Chat;

public static class ChatSurfaceResolver
{
    public static bool UseLegacyWebChat(bool useLegacyWebChatSetting) =>
        DebugChatSurfaceOverrides.ResolveUseLegacy(DebugChatSurfaceOverrides.WorkspaceChat, useLegacyWebChatSetting);

    public static bool ShouldUseLegacyWebChat(
        bool legacySurfaceRequested,
        bool hasPendingDraft,
        bool hasUnsentComposerContent = false) =>
        legacySurfaceRequested && !hasPendingDraft && !hasUnsentComposerContent;

    public static string BuildChatUrlForSession(string chatUrl, string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var baseUrl = System.Text.RegularExpressions.Regex.Replace(chatUrl, @"[&?]session=[^&]*", "");
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{separator}session={Uri.EscapeDataString(sessionKey)}";
    }

    public static string? BuildChatUrl(string? gatewayUrl, string? token)
    {
        gatewayUrl ??= string.Empty;
        token ??= string.Empty;

        return GatewayChatUrlBuilder.TryBuildChatUrl(gatewayUrl, token, out var url, out _)
            ? url
            : null;
    }

}
