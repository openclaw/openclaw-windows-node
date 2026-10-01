namespace OpenClawTray.Presentation;

internal static class ChatVisibilityPolicy
{
    public static bool IsChatVisible(bool isShuttingDown, bool workspaceChatVisible, bool compactChatVisible) =>
        !isShuttingDown && (workspaceChatVisible || compactChatVisible);

    public static bool IsWorkspaceChatVisible(
        bool isClosed, bool isVisible, bool isMinimized, WorkspacePageId page) =>
        !isClosed && isVisible && !isMinimized && page == WorkspacePageId.Home;
}
