using System.Text.Json;
using System.Xml.Linq;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceNavigationTests
{
    [Fact]
    public void Workspace_OnlyHomeAndFooterNotificationsHaveTypedDestinations()
    {
        Assert.Equal(new[] { WorkspacePageId.Home, WorkspacePageId.Notifications }, Enum.GetValues<WorkspacePageId>());
        Assert.Equal(new[] { "home", "notifications" }, WorkspaceNavigation.Routes.Keys);
        foreach (var (tag, page) in WorkspaceNavigation.Routes)
        {
            Assert.True(WorkspaceNavigation.TryResolveWorkspace($"workspace:{tag}", out var destination));
            Assert.Equal(page, destination.Page);
        }
    }

    public static IEnumerable<object[]> RemovedRoutes() =>
        new[] { "agents", "agent-detail", "writer-detail", "dashboards", "dashboard-detail", "canvas",
            "systems", "system-detail", "automations", "automation-detail", "plugins", "skills",
            "sessions", "usage", "activity", "tasks", "meetings", "apps", "portals", "more" }
        .Select(route => new object[] { route });

    [Theory]
    [MemberData(nameof(RemovedRoutes))]
    public void DeprecatedWorkspaceLinks_ReturnHomeAndCannotResurrectRemovedPages(string route)
    {
        Assert.True(WorkspaceNavigation.TryResolveWorkspace($"workspace:{route}", out var destination));
        Assert.Equal(WorkspacePageId.Home, destination.Page);
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Notifications));
        history.Navigate(destination);
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Notifications, history.Current.Page);
        Assert.True(history.GoForward());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void CompanionCatalog_RemainsSeparateAndComplete()
    {
        foreach (var page in Enum.GetValues<CompanionPageId>())
            Assert.False(WorkspaceNavigation.TryResolveWorkspace(WorkspaceNavigation.CompanionTag(page), out _));
        Assert.Equal("cron", WorkspaceNavigation.CompanionTag(CompanionPageId.Cron));
        Assert.Equal("agent:custom", WorkspaceNavigation.CompanionTag(CompanionPageId.Agents, "custom"));
        Assert.False(WorkspaceNavigation.TryResolveWorkspace("workspace:unknown", out _));
        Assert.False(WorkspaceNavigation.TryResolveWorkspace("workspace:agents:unknown", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hub")]
    [InlineData("home")]
    [InlineData("workspace")]
    [InlineData("chat")]
    public void LandingAndChat_OpenWorkspace(string? route)
    {
        Assert.True(WorkspaceNavigation.TryResolveWorkspace(route, out var destination));
        Assert.Equal(WorkspacePageId.Home, destination.Page);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("connection")]
    [InlineData("usage")]
    [InlineData("channels")]
    [InlineData("about")]
    [InlineData("agent:main:workspace")]
    public void CompanionRoutes_DoNotReplaceWorkspace(string route)
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Notifications));
        Assert.False(WorkspaceNavigation.TryResolveWorkspace(route, out _));
        Assert.Equal(new(WorkspacePageId.Notifications), history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
    }

    [Fact]
    public void RepeatedDestinations_DoNotGrowBackStack()
    {
        var history = new WorkspaceNavigationHistory();
        Assert.False(history.Navigate(new(WorkspacePageId.Home)));
        Assert.False(history.CanGoBack);
        history.Navigate(new(WorkspacePageId.Notifications));
        Assert.False(history.Navigate(new(WorkspacePageId.Notifications)));
        history.GoBack();
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void BackAndForward_PreserveIdentity_AndNewDestinationClearsForwardHistory()
    {
        var history = new WorkspaceNavigationHistory();
        Assert.False(history.GoBack());
        Assert.False(history.GoForward());
        var first = new WorkspaceDestination(WorkspacePageId.Notifications);
        var second = new WorkspaceDestination(WorkspacePageId.Home);
        history.Navigate(first);
        history.Navigate(second);
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.True(history.CanGoForward);
        Assert.False(history.Navigate(first));
        Assert.True(history.GoForward());
        Assert.Equal(second, history.Current);
        Assert.False(history.CanGoForward);
        Assert.True(history.GoBack());
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.False(history.CanGoBack);
        Assert.True(history.GoForward());
        Assert.Equal(first, history.Current);
        Assert.True(history.Navigate(new(WorkspacePageId.Home)));
        Assert.False(history.CanGoForward);
        Assert.False(history.GoForward());
    }

    [Fact]
    public void WorkspaceNavigation_UsesCompanionNativeControlAndExistingColourfulAssets()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var navigation = Assert.Single(document.Descendants(), element => element.Name.LocalName == "NavigationView");
        Assert.Equal("Left", (string?)navigation.Attribute("PaneDisplayMode"));
        Assert.Equal("False", (string?)navigation.Attribute("IsSettingsVisible"));
        var items = navigation.Descendants().Where(element => element.Name.LocalName == "NavigationViewItem" && element.Attribute("Tag") is not null).ToArray();
        Assert.Equal("home", (string?)Assert.Single(items).Attribute("Tag"));
        foreach (var item in items)
        {
            Assert.Contains(item.Descendants(), element => element.Name.LocalName == "ImageIcon");
            Assert.True(WorkspaceNavigation.Routes.ContainsKey((string)item.Attribute("Tag")!));
        }
        foreach (var image in navigation.Descendants().Where(element => element.Name.LocalName == "SvgImageSource"))
        {
            var asset = ((string)image.Attribute("UriSource")!)["ms-appx:///".Length..];
            Assert.True(File.Exists(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", asset)));
        }
        foreach (var (name, slot) in new[]
        {
            ("AssistantSelector", "NavigationView.PaneHeader"),
            ("SessionsHeader", "NavigationView.MenuItems"),
            ("OwnerButton", "NavigationView.PaneFooter"),
            ("NotificationsButton", "NavigationView.PaneFooter")
        })
        {
            var control = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Contains(control.Ancestors(), element => element.Name.LocalName == slot);
        }
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "PinnedList");
        Assert.Equal("False", (string?)navigation.Attribute("IsPaneToggleButtonVisible"));
        Assert.Contains("var open = !NavView.IsPaneOpen", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs")));
    }

    [Fact]
    public void HiddenPane_UsesZeroWidthAndReopenRowWithoutCoveringContent()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var navigation = document.Descendants().Single(element => element.Name.LocalName == "NavigationView");
        Assert.Equal("0", (string?)navigation.Attribute("CompactPaneLength"));
        var reopen = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ReopenPaneButton");
        Assert.Equal("Button", reopen.Name.LocalName);
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)reopen.Attribute("Style"));
        Assert.Equal("Collapsed", (string?)reopen.Attribute("Visibility"));
        var content = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ContentHost");
        var slot = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ReopenPaneSlot");
        Assert.Same(slot.Parent, content.Parent);
        Assert.Equal("56", (string?)slot.Attribute("Height"));
        var collapse = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CollapsePaneButton");
        Assert.Same(reopen.Parent, collapse.Parent);
        Assert.Same(reopen.Parent, navigation.Parent);
        Assert.Equal((string?)collapse.Attribute("Margin"), (string?)reopen.Attribute("Margin"));
        Assert.Equal("2", (string?)content.Attribute("Grid.Row"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "NavigationView.FooterMenuItems");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Equal("OnPaneClosed", (string?)navigation.Attribute("PaneClosed"));
        var duration = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == "SplitViewPaneAnimationOpenDuration");
        Assert.Equal("00:00:00.16", duration.Value);
        Assert.DoesNotContain("DispatcherQueueTimer", code);
        Assert.Contains("NavView.IsPaneVisible = false", code);
        Assert.DoesNotContain("NavigationViewPaneDisplayMode.LeftMinimal", code);
        Assert.DoesNotContain("NavView.CompactPaneLength =", code);
        Assert.Contains("CollapsePaneButton : ReopenPaneButton).Focus(FocusState.Programmatic)", code);
        var toggle = code[code.IndexOf("private void OnTogglePane", StringComparison.Ordinal)..
            code.IndexOf("private void UpdatePanePresentation", StringComparison.Ordinal)];
        Assert.DoesNotContain("IsPaneVisible = false", toggle);
        Assert.DoesNotContain("LeftMinimal", toggle);
    }

    [Fact]
    public void Sidebar_UsesNativePaneFillAndPreservesSessionSelection()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var background = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewDefaultPaneBackground");
        Assert.Equal("NavigationViewExpandedPaneBackground", (string?)background.Attribute("ResourceKey"));
        Assert.Equal("0", document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewBorderThickness").Value);
        var home = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "HomeItem");
        Assert.Equal("{StaticResource Chat_Icon}", (string?)Assert.Single(home.Descendants(),
            element => element.Name.LocalName == "ImageIcon").Attribute("Source"));
        Assert.DoesNotContain(home.Descendants(), element => element.Name.LocalName == "NavigationViewItem.Icon");
        Assert.Contains(home.Descendants(), element => (string?)element.Attribute(x + "Name") == "HomeLabel");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("session.Key == _selectedSessionKey", code);
        Assert.DoesNotContain("SelectsOnInvoked = false", code);
        Assert.DoesNotContain("OnNavigationInvoked", code);
    }

    [Fact]
    public void WorkspaceChrome_UsesNativeTitleBarAndTwoRowNavigationWithSubtleActions()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var title = document.Descendants().Single(element => element.Name.LocalName == "TitleBar");
        Assert.Equal("OpenClaw", (string?)title.Attribute("Title"));
        Assert.DoesNotContain(title.Descendants(), element => element.Name.LocalName == "Button");
        Assert.DoesNotContain(title.Descendants(), element => element.Name.LocalName == "TextBlock");
        foreach (var name in new[] { "BackButton", "ForwardButton" })
        {
            var button = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Contains(button.Ancestors(), element => (string?)element.Attribute(x + "Name") == "NavigationToolbar");
            Assert.Equal("Right", (string?)button.Parent!.Attribute("HorizontalAlignment"));
        }
        var toolbar = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "NavigationToolbar");
        Assert.Contains(toolbar.Ancestors(), element => element.Name.LocalName == "NavigationView.PaneHeader");
        var toggle = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CollapsePaneButton");
        Assert.Equal("Left", (string?)toggle.Attribute("HorizontalAlignment"));
        Assert.Contains(toggle.Descendants(), element => (string?)element.Attribute("Glyph") == "\uE90C");
        var assistant = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "AssistantSelector");
        Assert.Equal("ComboBox", assistant.Name.LocalName);
        Assert.Equal("Transparent", (string?)assistant.Attribute("Background"));
        Assert.Equal("0", (string?)assistant.Attribute("BorderThickness"));
        Assert.DoesNotContain(assistant.Descendants(), element => element.Name.LocalName == "ControlTemplate");
        Assert.Contains(assistant.Descendants(), element => (string?)element.Attribute("ResourceKey") == "SubtleFillColorSecondaryBrush");
        Assert.Contains(assistant.Descendants(), element => (string?)element.Attribute("ResourceKey") == "SubtleFillColorTertiaryBrush");
        var headers = document.Descendants().Where(element => element.Name.LocalName == "NavigationViewItemHeader").ToArray();
        Assert.DoesNotContain(headers, element => (string?)element.Attribute(x + "Name") == "PagesHeader");
        var sessions = headers.Single(element => (string?)element.Attribute(x + "Name") == "SessionsHeader");
        Assert.Equal("home", (string?)sessions.ElementsBeforeSelf().Last().Attribute("Tag"));
        Assert.Equal("True", (string?)sessions.Attribute("IsEnabled"));
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "ContentPresenter"
            && (string?)element.Attribute("Content") == "{TemplateBinding Content}");
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "TextBlock"
            && (string?)element.Attribute("Style") == "{StaticResource NavigationViewItemHeaderTextStyle}");
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Target") == "HeaderContent.Visibility"
            && (string?)element.Attribute("Value") == "Collapsed");
        var newConversation = assistant.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "NewConversationOption");
        Assert.Equal("ComboBoxItem", newConversation.Name.LocalName);
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "NewConversationButton");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("AssistantSelector.Items.Add(NewConversationOption)", code);
        Assert.Contains("RestoreAssistantSelection();", code);
        Assert.Contains("ReferenceEquals(AssistantSelector.SelectedItem, NewConversationOption)", code);
        foreach (var name in new[] { "NewSessionButton", "BackButton", "ForwardButton" })
        {
            var button = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)button.Attribute("Style"));
        }
    }

    [Fact]
    public void WorkspaceContentSurface_IsOwnedByNativeNavigationTemplate()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Single(doc.Descendants(), element => element.Name.LocalName == "MicaBackdrop");
        var host = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ContentHost");
        var layout = host.Parent!;
        Assert.Equal("Grid", layout.Name.LocalName);
        Assert.Equal("NavigationView", layout.Parent!.Name.LocalName);
        foreach (var element in host.AncestorsAndSelf())
            Assert.Null(element.Attribute("Background"));
        Assert.Null(layout.Attribute("CornerRadius"));
        Assert.Null(layout.Attribute("Margin"));
        var titleBar = doc.Descendants().Single(element => element.Name.LocalName == "TitleBar");
        Assert.Null(titleBar.Attribute("Background"));
        Assert.DoesNotContain(doc.Descendants(), element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewContentBackground");

        Assert.False(File.Exists(Source("Pages", "WorkspaceContentPage.xaml")));
        Assert.False(File.Exists(Source("Pages", "WorkspaceContentPage.xaml.cs")));
        Assert.False(File.Exists(Source("Controls", "WorkspacePageRenderer.cs")));
    }

    [Fact]
    public void NativeFooter_NotificationsAreIndependentAndRightOfOwner()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var owner = doc.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "OwnerButton");
        var bell = doc.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "NotificationsButton");
        Assert.Same(owner.Parent, bell.Parent);
        Assert.Equal("1", (string?)bell.Attribute("Grid.Column"));
        Assert.Equal("OnNotifications", (string?)bell.Attribute("Click"));
        Assert.Single(doc.Descendants(), node => (string?)node.Attribute(x + "Name") == "NotificationsButton");
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)owner.Attribute("Style"));
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)bell.Attribute("Style"));
        Assert.Single(owner.Descendants(), element => element.Name.LocalName == "PersonPicture");
        Assert.DoesNotContain(owner.Descendants(), element => element.Name.LocalName == "FontIcon");
        Assert.DoesNotContain(doc.Descendants(), element => (string?)element.Attribute(x + "Key") == "WorkspaceSubtleButton");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("OwnerButton.Flyout = menu", code);
        Assert.Contains("OpenCompanion(CompanionPageId.Usage)", code);
        Assert.Contains("OpenCompanion(CompanionPageId.Channels)", code);
        Assert.Contains("OpenCompanion(CompanionPageId.About)", code);
        Assert.Contains("Add(\"GetApps\", () => _ = OpenLinkAsync(\"https://docs.openclaw.ai/platforms\")", code);
        Assert.Contains("OpenLinkAsync(\"https://github.com/openclaw/openclaw-windows-node\")", code);
        Assert.Contains("help.Items.Add(github)", code);
        Assert.DoesNotContain("WebView", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml")));
    }

    [Fact]
    public void PaneToggle_TargetAndGlyphSizesMatchAcrossPaneStates()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        var expanded = doc.Descendants().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "WorkspaceTogglePane");
        var compact = doc.Descendants().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "WorkspaceReopenPane");
        foreach (var toggle in new[] { expanded, compact })
        {
            Assert.Equal("Button", toggle.Name.LocalName);
            Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)toggle.Attribute("Style"));
            Assert.Equal("40", (string?)toggle.Attribute("Width"));
            Assert.Equal("40", (string?)toggle.Attribute("Height"));
            var icon = Assert.Single(toggle.Descendants(), element => element.Name.LocalName == "FontIcon");
            Assert.Equal("16", (string?)icon.Attribute("FontSize"));
        }
    }

    [Fact]
    public void WindowManager_OwnsBothWindowsWithoutSharingCompanionNavigationScope()
    {
        var code = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("WorkspaceNavigation.TryResolveWorkspace(navigateTo", code);
        Assert.Contains("if (_workspaceWindow is null || _workspaceWindow.IsClosed)", code);
        Assert.Contains("if (_hubWindow is null || _hubWindow.IsClosed)", code);
        Assert.Contains("_hubWindow.NavigateTo(navigateTo)", code);
        Assert.Contains("_callbacks.ApplyTheme(_workspaceWindow)", code);
        Assert.Contains("TryClose(\"Workspace window\"", code);
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.DoesNotContain("PageActivator", workspace);
        Assert.DoesNotContain("GatewayRegistry", workspace);
        Assert.DoesNotContain("new OpenClawGatewayClient", workspace);
        var hubMarkup = File.ReadAllText(Source("Windows", "HubWindow.xaml"));
        Assert.DoesNotContain("Tag=\"chat\"", hubMarkup);
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        Assert.Contains("Initialize(Window? ownerWindow)", chat);
        Assert.Contains("GetWindowHandle(_ownerWindow)", chat);
        Assert.DoesNotContain("CurrentApp.ActiveHubWindow!.MountReactorChat", chat);
    }

    [Fact]
    public void WorkspaceRefocusAndPendingChat_PreserveUserIntent()
    {
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("preserveCurrent: navigateTo is null or \"hub\"", manager);
        Assert.Contains("if (!preserveCurrent)", manager);
        Assert.Contains("_workspaceWindow.Navigate(destination)", manager);
        Assert.Contains("_workspaceWindow.SelectSession(sessionKey)", manager);
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("if (!_navigation.Navigate(destination)", workspace);
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var legacy = chat[chat.IndexOf("private void ShowWebViewSurface", StringComparison.Ordinal)..];
        Assert.Contains("var pendingSessionKey = _pendingSessionKey ?? _hub?.PendingChatSessionKey", legacy);
        Assert.Contains("_pendingSessionKey = threadIdToMount", chat);
        Assert.Contains("_pendingVoice.Request(nativeSurface: !_webViewMode)", chat);
        Assert.Contains("_pendingVoice.Cancel()", chat);
        Assert.DoesNotContain("RetryTriggerVoice", chat);
    }

    [Fact]
    public void Projection_UsesRealIdentitiesAndNeverManufacturesAnAgent()
    {
        Assert.Empty(WorkspaceProjection.Agents(null, []));
        using var json = JsonDocument.Parse("""{"agents":[{"id":"custom","name":"Actual agent","workspace":"C:\\work"},null,{"id":9}]}""");
        var session = new SessionInfo { Key = "real-key", AgentId = "custom", DisplayName = "Actual conversation" };
        var agent = Assert.Single(WorkspaceProjection.Agents(json.RootElement, [session]));
        Assert.Equal("custom", agent.Id);
        Assert.Equal("Actual agent", agent.Name);
        Assert.Equal("real-key", agent.LatestSessionKey);
        var visible = Assert.Single(WorkspaceProjection.Sessions([session], "custom"));
        Assert.Equal("real-key", visible.Key);
        Assert.Empty(WorkspaceProjection.Sessions([session], "different"));
    }

    [Fact]
    public void Projection_DoesNotSelectBackgroundSessionsForAssistantChat()
    {
        using var json = JsonDocument.Parse("""{"agents":[{"id":"custom","name":"Actual agent"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "background", AgentId = "custom", IsBackground = true },
            new SessionInfo { Key = "visible", AgentId = "custom", DisplayName = "Conversation" }
        };
        var agent = Assert.Single(WorkspaceProjection.Agents(json.RootElement, sessions));
        Assert.Equal("visible", agent.LatestSessionKey);
        Assert.Equal("visible", Assert.Single(WorkspaceProjection.Sessions(sessions, "custom")).Key);
    }

    private static string Source(string folder, string file) =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", folder, file);
}
