namespace OpenClawTray.Presentation;

/// <summary>
/// Where the shared Workspace footer (account, Dashboard, bell) is reachable. Full Settings shows
/// it in its pane or compact rail; an embedded pinned page hides that pane and relies on Chat's.
/// Chat hides its whole pane, so Dashboard and the bell move to the title bar only while no pane
/// footer is visible, and never appear twice.
/// </summary>
internal static class WorkspaceFooterPlacement
{
    public static bool ShowTitleBarActions(bool settingsFooterVisible, bool chatPaneOpen) =>
        !settingsFooterVisible && !chatPaneOpen;
}
