namespace OpenClawTray.Helpers;

/// <summary>
/// Maps HubWindow nav tags to localised page labels for cross-page
/// "Back to {origin}" affordances. Reuses the sidebar
/// <c>HubWindow_NavigationViewItem_*.Content</c> resw entries so page names
/// are translated once and stay in sync with the sidebar.
/// </summary>
internal static class NavOriginLabels
{
    public static string DisplayLabel(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var resourceKey = tag switch
        {
            "chat" => "HubWindow_NavigationViewItem_82.Content",
            "connection" => "HubWindow_NavigationViewItem_88.Content",
            "sessions" => "HubWindow_NavigationViewItem_91.Content",
            "skills" => "HubWindow_NavigationViewItem_97.Content",
            "channels" => "HubWindow_NavigationViewItem_109.Content",
            "instances" or "nodes" => "HubWindow_NavigationViewItem_112.Content",
            "agentevents" => "HubWindow_NavigationViewItem_94.Content",
            "bindings" => "HubWindow_NavigationViewItem_115.Content",
            "config" => "HubWindow_NavigationViewItem_118.Content",
            "usage" => "HubWindow_NavigationViewItem_121.Content",
            "cron" => "HubWindow_NavigationViewItem_124.Content",
            "voice" => "HubWindow_NavigationViewItem_Voice.Content",
            "settings" => "HubWindow_NavigationViewItem_133.Content",
            "permissions" => "HubWindow_NavigationViewItem_136.Content",
            "sandbox" => "HubWindow_NavigationViewItem_Sandbox.Content",
            "debug" => "HubWindow_NavigationViewItem_145.Content",
            "info" or "about" => "HubWindow_NavigationViewItem_148.Content",
            _ => null,
        };
        return resourceKey == null
            ? char.ToUpperInvariant(tag![0]) + tag.Substring(1)
            : LocalizationHelper.GetString(resourceKey);
    }

    public static string BackToLabel(string? tag) =>
        LocalizationHelper.Format("BackToOriginFormat", DisplayLabel(tag));
}



