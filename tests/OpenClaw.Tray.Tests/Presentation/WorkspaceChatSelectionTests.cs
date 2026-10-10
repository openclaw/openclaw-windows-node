using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceChatSelectionTests
{
    [Fact]
    public void ComposerNewSession_UpdatesCurrentConversationAndAddsOneHistoryEntry()
    {
        var history = new WorkspaceNavigationHistory();
        var original = new WorkspaceDestination(WorkspacePageId.Home, "agent:main:original");
        history.Navigate(original);
        history.Navigate(new(WorkspacePageId.Notifications));
        history.GoBack();

        Assert.True(history.SelectChatSession("agent:research:new"));
        Assert.Equal(new(WorkspacePageId.Home, "agent:research:new"), history.Current);
        Assert.Equal(history.Current, history.ChatDestination);
        Assert.False(history.CanGoForward);
        Assert.False(history.SelectChatSession("agent:research:new"));
        Assert.True(history.GoBack());
        Assert.Equal(original, history.Current);
        Assert.True(history.GoForward());
        Assert.Equal("agent:research:new", history.Current.SessionKey);
    }

    [Fact]
    public void LateComposerSelection_PreservesNotificationsAndChangesRetainedConversation()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "agent:main:original"));
        var notifications = new WorkspaceDestination(WorkspacePageId.Notifications);
        history.Navigate(notifications);

        Assert.True(history.SelectChatSession("agent:main:new"));
        Assert.Equal(notifications, history.Current);
        Assert.Equal(new(WorkspacePageId.Home, "agent:main:new"), history.ChatDestination);
        Assert.False(history.SelectChatSession("agent:main:new"));
        history.Navigate(history.ChatDestination);
        Assert.Equal("agent:main:new", history.Current.SessionKey);
        Assert.True(history.GoBack());
        Assert.Equal(notifications, history.Current);
    }

    [Fact]
    public void RemovingComposerCreatedSession_ClearsRetainedAndCurrentDestinations()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "agent:main:original"));
        history.SelectChatSession("agent:main:new");

        Assert.True(history.RemoveSession("agent:main:new", "agent:main:replacement"));
        Assert.Equal("agent:main:replacement", history.Current.SessionKey);
        Assert.Equal(history.Current, history.ChatDestination);
        Assert.True(history.GoBack());
        Assert.Equal("agent:main:original", history.Current.SessionKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ComposerSelection_RejectsEmptyKeysWithoutChangingNavigation(string key)
    {
        var history = new WorkspaceNavigationHistory();
        var original = history.Current;

        Assert.Throws<ArgumentException>(() => history.SelectChatSession(key));
        Assert.Equal(original, history.Current);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void SelectionNotification_DoesNotQueueOrRemountTheAlreadySelectedChat()
    {
        // Retire when the native Workspace event adapter can be exercised in Tray.Tests.
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        var handler = source[source.IndexOf("private void OnChatSessionSelected(", StringComparison.Ordinal)..
            source.IndexOf("internal void NavigateNativeSetup(", StringComparison.Ordinal)];
        Assert.Contains("_navigation.SelectChatSession(sessionKey)", handler);
        Assert.Contains("SessionDisplayResolver.Resolve(session).AgentId", handler);
        Assert.Contains("RefreshSidebar()", handler);
        Assert.Contains("UpdateNavigationSelection()", handler);
        Assert.DoesNotContain("RenderDestination()", handler);
        Assert.DoesNotContain("QueueSession(", handler);
        Assert.DoesNotContain("Initialize(", handler);
        Assert.Contains("_chat.SessionSelected += OnChatSessionSelected;", source);
        Assert.Contains("_chat.SessionSelected -= OnChatSessionSelected;", source);
    }
}
