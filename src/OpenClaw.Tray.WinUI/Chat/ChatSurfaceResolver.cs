using OpenClawTray.Helpers;

namespace OpenClawTray.Chat;

public static class ChatSurfaceResolver
{
    public static bool UseLegacyWebChat(bool useLegacyWebChatSetting) =>
        DebugChatSurfaceOverrides.ResolveUseLegacy(DebugChatSurfaceOverrides.WorkspaceChat, useLegacyWebChatSetting);

    public static string? BuildChatUrl(string? gatewayUrl, string? token)
    {
        gatewayUrl ??= string.Empty;
        token ??= string.Empty;

        return GatewayChatUrlBuilder.TryBuildChatUrl(gatewayUrl, token, out var url, out _)
            ? url
            : null;
    }

}
