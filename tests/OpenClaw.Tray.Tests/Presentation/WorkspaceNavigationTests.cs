using System.Text.Json;
using System.Xml.Linq;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceNavigationTests
{
    [Fact]
    public void ApprovedScene_AllContentRoutesHaveTypedDestinations()
    {
        using var scene = ReadScene();
        var routes = Objects(scene.RootElement)
            .Where(node => node.TryGetProperty("dashboardPage", out var value) && value.ValueKind == JsonValueKind.String)
            .Select(node => node.GetProperty("dashboardPage").GetString()!)
            .Where(value => !value.Contains('{'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(22, WorkspaceNavigation.Routes.Count);
        Assert.Subset(WorkspaceNavigation.Routes.Keys.ToHashSet(), routes);
        foreach (var (tag, page) in WorkspaceNavigation.Routes)
        {
            Assert.True(WorkspaceNavigation.TryResolveWorkspace($"workspace:{tag}", out var destination));
            Assert.Equal(page, destination.Page);
        }
    }

    [Fact]
    public void ApprovedScene_All21CompanionLinksKeepExactDestination()
    {
        using var scene = ReadScene();
        var expected = new Dictionary<string, string>
        {
            ["colleague-agent-settings"] = "agents",
            ["refresh-button-71"] = "permissions", ["refresh-button-116"] = "permissions",
            ["colleague-system-connection"] = "connection", ["colleague-this-computer"] = "permissions",
            ["colleague-manage-gateway"] = "connection", ["colleague-system-permissions"] = "permissions",
            ["colleague-system-devices"] = "instances", ["refresh-button-199"] = "permissions",
            ["refresh-button-315"] = "permissions", ["refresh-button-483"] = "settings",
            ["footer-open-settings"] = "settings", ["footer-open-usage"] = "usage",
            ["footer-pair-device"] = "channels", ["footer-build-hash"] = "about",
            ["refresh-button-912"] = "settings", ["refresh-button-983"] = "connection",
            ["refresh-button-988"] = "permissions", ["refresh-button-993"] = "debug",
            ["refresh-button-1019"] = "connection", ["refresh-button-1029"] = "settings"
        };
        var links = Objects(scene.RootElement).Where(node =>
            node.TryGetProperty("on", out var on) && on.TryGetProperty("tap", out var tap) &&
            tap.ValueKind == JsonValueKind.Object &&
            tap.TryGetProperty("navigate", out var navigate) && navigate.GetString() == "settings").ToArray();
        Assert.Equal(21, links.Length);
        foreach (var link in links)
        {
            var id = link.GetProperty("id").GetString()!;
            var tab = link.GetProperty("on").GetProperty("tap").GetProperty("setState").GetProperty("settingsTab").GetString();
            Assert.Equal(expected[id], tab);
            Assert.True(Enum.TryParse<CompanionPageId>(tab, true, out var page));
            Assert.False(WorkspaceNavigation.TryResolveWorkspace(WorkspaceNavigation.CompanionTag(page), out _));
        }
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
        history.Navigate(new(WorkspacePageId.Agents));
        history.Navigate(new(WorkspacePageId.AgentDetail, "real-agent"));
        Assert.False(WorkspaceNavigation.TryResolveWorkspace(route, out _));
        Assert.Equal(new(WorkspacePageId.AgentDetail, "real-agent"), history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Agents, history.Current.Page);
    }

    [Fact]
    public void RepeatedDestinations_DoNotGrowBackStack_AndItemIdentityIsPreserved()
    {
        var history = new WorkspaceNavigationHistory();
        Assert.False(history.Navigate(new(WorkspacePageId.Home)));
        Assert.False(history.CanGoBack);
        history.Navigate(new(WorkspacePageId.AgentDetail, "first"));
        Assert.False(history.Navigate(new(WorkspacePageId.AgentDetail, "first")));
        Assert.True(history.Navigate(new(WorkspacePageId.AgentDetail, "second")));
        history.GoBack();
        Assert.Equal("first", history.Current.ItemId);
        history.GoBack();
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void BackAndForward_PreserveIdentity_AndNewDestinationClearsForwardHistory()
    {
        var history = new WorkspaceNavigationHistory();
        Assert.False(history.GoBack());
        Assert.False(history.GoForward());
        var first = new WorkspaceDestination(WorkspacePageId.AgentDetail, "first");
        var second = new WorkspaceDestination(WorkspacePageId.AgentDetail, "second");
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
        Assert.True(history.Navigate(new(WorkspacePageId.Systems)));
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
        Assert.Equal(WorkspaceNavigation.PinnedPages.Count + 1, items.Length);
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
        Assert.Contains("NavView.IsPaneOpen = !NavView.IsPaneOpen", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs")));
    }

    [Fact]
    public void CompactPaneActions_AreNativeMenuItems_NotFloatingOverlays()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var reopen = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CompactPaneItem");
        Assert.Equal("NavigationViewItem", reopen.Name.LocalName);
        Assert.Equal("NavigationView.MenuItems", reopen.Parent!.Name.LocalName);
        Assert.Same(reopen, reopen.Parent.Elements().First());
        Assert.Equal("{StaticResource WorkspaceCompactAction}", (string?)reopen.Attribute("Style"));
        var notifications = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CompactNotificationsItem");
        Assert.Equal("NavigationViewItem", notifications.Name.LocalName);
        Assert.Equal("NavigationView.FooterMenuItems", notifications.Parent!.Name.LocalName);
        Assert.Equal("{StaticResource WorkspaceCompactAction}", (string?)notifications.Attribute("Style"));
        foreach (var item in new[] { reopen, notifications })
        {
            var button = Assert.Single(item.Elements());
            Assert.Equal("Button", button.Name.LocalName);
            Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)button.Attribute("Style"));
        }
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "ReopenPaneButton");
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
        var toggle = toolbar.Descendants().Single(element => (string?)element.Attribute("Click") == "OnTogglePane");
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
        Assert.Contains(headers, element => (string?)element.Attribute(x + "Name") == "PagesHeader");
        var sessions = headers.Single(element => (string?)element.Attribute(x + "Name") == "SessionsHeader");
        Assert.Equal("more", (string?)sessions.ElementsBeforeSelf().Last().Attribute("Tag"));
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
        Assert.Equal(1, agent.SessionCount);
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
        Assert.Equal(1, agent.SessionCount);
        Assert.Equal("visible", agent.LatestSessionKey);
        Assert.Equal("visible", Assert.Single(WorkspaceProjection.Sessions(sessions, "custom")).Key);
    }

    private static JsonDocument ReadScene() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), ".agents", "design", "prototypes.jsonc")),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    private static IEnumerable<JsonElement> Objects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
                foreach (var child in Objects(property.Value)) yield return child;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var child in Objects(item)) yield return child;
    }

    private static string Source(string folder, string file) =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", folder, file);
}
