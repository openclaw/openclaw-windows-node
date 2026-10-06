using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class ChatVisibilityPolicyTests
{
    [Theory]
    [InlineData(false, true, false, true, false, false, true)] // Visible Workspace chat
    [InlineData(false, true, true, true, false, false, false)] // Minimized Workspace
    [InlineData(false, false, false, true, false, false, false)] // Hidden Workspace
    [InlineData(true, true, false, true, false, false, false)] // Closed Workspace
    [InlineData(false, true, false, false, false, false, false)] // Notifications, not chat
    [InlineData(true, false, false, false, true, false, true)] // Compact chat only
    [InlineData(false, true, true, true, true, false, true)] // Compact chat still visible
    [InlineData(false, true, false, true, true, true, false)] // Shutdown
    [InlineData(true, false, false, false, false, false, false)] // Settings companion only
    public void OnlyVisibleChatSurfacesSuppressNotifications(
        bool closed, bool visible, bool minimized, bool home,
        bool compactVisible, bool shuttingDown, bool expected)
    {
        var workspaceVisible = ChatVisibilityPolicy.IsWorkspaceChatVisible(
            closed, visible, minimized, home ? WorkspacePageId.Home : WorkspacePageId.Notifications);
        Assert.Equal(expected, ChatVisibilityPolicy.IsChatVisible(shuttingDown, workspaceVisible, compactVisible));
    }

    [Fact]
    public void NativeAdapters_UseCurrentWindowVisibilityAndKeepNotificationToggles()
    {
        // retirement_condition: replace with native notification tests when WinUI can run in Tray.Tests.
        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var workspace = File.ReadAllText(Path.Combine(root, "Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("ChatVisibilityPolicy.IsWorkspaceChatVisible(", workspace);
        Assert.Contains("AppWindow.IsVisible", workspace);
        Assert.Contains("Microsoft.UI.Windowing.OverlappedPresenterState.Minimized", workspace);
        Assert.Contains("Destination.Page", workspace);
        var manager = File.ReadAllText(Path.Combine(root, "Services", "WindowManager.cs"));
        Assert.Contains("_workspaceWindow is { IsChatVisible: true }", manager);
        Assert.Contains("_chatWindow is { IsClosed: false, Visible: true }", manager);
        var app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        var method = app[app.IndexOf("private bool ShouldShowNotification(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("#endregion", StringComparison.Ordinal)];
        Assert.DoesNotContain("IsHubOpen", method);
        Assert.Contains("notification.IsChat && !_settings.NotifyChatResponses", method);
        Assert.Contains("if (notification.IsChat)", method);
        Assert.Contains("_windowManager?.IsChatVisible == true", method);
        Assert.Contains("s_notifTypeMap.TryGetValue", method);
    }
}
