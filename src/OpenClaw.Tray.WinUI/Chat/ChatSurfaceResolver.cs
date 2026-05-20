using OpenClawTray.Helpers;

namespace OpenClawTray.Chat;

public enum ChatSurfaceTarget
{
    HubChat,
    TrayChat,
}

public readonly record struct ChatSurfaceDecision(
    bool UseLegacyWebChat,
    string? ChatUrl,
    bool ChatUrlChanged);

public static class ChatSurfaceResolver
{
    public static ChatSurfaceDecision Resolve(
        ChatSurfaceTarget target,
        bool useLegacyWebChatSetting,
        string? currentChatUrl,
        string? resolvedChatUrl)
    {
        var useLegacy = DebugChatSurfaceOverrides.ResolveUseLegacy(
            GetOverride(target),
            useLegacyWebChatSetting);

        var urlChanged = !string.Equals(resolvedChatUrl, currentChatUrl, StringComparison.Ordinal);
        return new ChatSurfaceDecision(useLegacy, resolvedChatUrl, urlChanged);
    }

    public static string? BuildChatUrl(string? gatewayUrl, string? token)
    {
        gatewayUrl ??= string.Empty;
        token ??= string.Empty;

        return GatewayChatUrlBuilder.TryBuildChatUrl(gatewayUrl, token, out var url, out _)
            ? url
            : null;
    }

    private static ChatSurfaceOverride GetOverride(ChatSurfaceTarget target) => target switch
    {
        ChatSurfaceTarget.HubChat => DebugChatSurfaceOverrides.HubChat,
        ChatSurfaceTarget.TrayChat => DebugChatSurfaceOverrides.TrayChat,
        _ => ChatSurfaceOverride.NoOverride,
    };
}
