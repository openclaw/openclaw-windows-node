using System.Text.Json;
using System.Xml.Linq;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisconnectAndReturningRoster_PreserveExplicitAgentInsteadOfGatewayDefault(
        bool sessionsArriveFirst)
    {
        using var roster = JsonDocument.Parse(
            """{"defaultId":"main","selectionRequired":true,"agents":[{"id":"main"},{"id":"coordinator"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:main", AgentId = "main" },
            new SessionInfo { Key = "agent:coordinator:dashboard:proof", AgentId = "coordinator" },
        };
        var state = new AppState
        {
            Status = ConnectionStatus.Connected,
            AgentsList = roster.RootElement,
            Sessions = sessions,
        };
        var selection = new WorkspaceAgentSelection();
        selection.Select("coordinator");
        selection.Refresh("gateway", state.AgentsList, WorkspaceProjection.Agents(state.AgentsList, state.Sessions), true);
        state.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(AppState.AgentsList) or nameof(AppState.Sessions) or nameof(AppState.Status))
                selection.Refresh("gateway", state.AgentsList,
                    WorkspaceProjection.Agents(state.AgentsList, state.Sessions),
                    rosterUpdated: change.PropertyName == nameof(AppState.AgentsList));
        };

        state.Status = ConnectionStatus.Disconnected;
        state.ClearCachedData();
        Assert.Equal("coordinator", selection.AgentId);
        Assert.False(selection.IsReady);
        Assert.False(selection.HasCurrentRoster);
        state.Status = ConnectionStatus.Connected;
        if (sessionsArriveFirst)
        {
            state.Sessions = sessions;
            state.AgentsList = roster.RootElement;
        }
        else
        {
            state.AgentsList = roster.RootElement;
            state.Sessions = sessions;
        }

        Assert.Equal("coordinator", selection.AgentId);
        Assert.True(selection.IsReady);
        Assert.Equal("agent:coordinator:dashboard:proof",
            Assert.Single(WorkspaceProjection.Sessions(state.Sessions, selection.AgentId, new WorkspaceSessionOrder())).Key);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "main")]
    public void AuthoritativeRosterRemoval_UsesItsSelectionPolicy(bool required, string? expectedAgent)
    {
        using var initial = JsonDocument.Parse("""{"agents":[{"id":"main"},{"id":"coordinator"}]}""");
        using var removed = JsonDocument.Parse(
            $$"""{"defaultId":"main","selectionRequired":{{required.ToString().ToLowerInvariant()}},"agents":[{"id":"main"}]}""");
        var selection = new WorkspaceAgentSelection();
        selection.Select("coordinator");
        selection.Refresh("gateway", initial.RootElement, WorkspaceProjection.Agents(initial.RootElement, []), true);
        selection.Refresh("gateway", null, []);
        Assert.Equal("coordinator", selection.AgentId);
        Assert.False(selection.IsReady);

        selection.Refresh("gateway", removed.RootElement, WorkspaceProjection.Agents(removed.RootElement, []), true);

        Assert.Equal(expectedAgent, selection.AgentId);
        Assert.True(selection.HasCurrentRoster);
        Assert.Equal(!required, selection.IsReady);
    }

    [Fact]
    public void ExplicitlyUnsupportedRoster_PreservesLegacySessionsAndDefaultCreation()
    {
        using var previousRoster = JsonDocument.Parse(
            """{"defaultId":"main","agents":[{"id":"main"},{"id":"research"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:main", AgentId = "main" },
            new SessionInfo { Key = "agent:research:existing", AgentId = "research" },
        };
        var selection = new WorkspaceAgentSelection();
        selection.Select("research");
        selection.Refresh("legacy", null, []);
        Assert.False(selection.HasSessionAccess);
        Assert.False(selection.IsReady);

        var agents = WorkspaceProjection.Agents(previousRoster.RootElement, sessions);
        selection.Refresh("legacy", previousRoster.RootElement, agents, rosterUpdated: true);
        Assert.True(selection.HasCurrentRoster);
        selection.Refresh("legacy", previousRoster.RootElement, agents,
            rosterUpdated: true, rosterUnsupported: true);

        Assert.False(selection.HasCurrentRoster);
        Assert.True(selection.HasSessionAccess);
        Assert.True(selection.IsReady);
        Assert.Null(selection.AgentId);
        Assert.Equal(new[] { "agent:main:main", "agent:research:existing" },
            WorkspaceProjection.Sessions(sessions, selection.AgentId, new WorkspaceSessionOrder())
                .Select(session => session.Key).OrderBy(key => key, StringComparer.Ordinal));

        selection.Refresh("different-gateway", null, [], rosterUnsupported: true);
        Assert.False(selection.HasSessionAccess);
        Assert.False(selection.IsReady);
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected, true)]
    [InlineData(ConnectionStatus.Error, true)]
    [InlineData(ConnectionStatus.Connecting, false)]
    [InlineData(ConnectionStatus.Connected, false)]
    public void AssistantMenuRetainsOfflineConnectionGuidanceWithoutAuthorizingSessionCreation(
        ConnectionStatus status, bool menuAvailable)
    {
        var selection = new WorkspaceAgentSelection();
        selection.Refresh("gateway", null, []);

        Assert.Equal(menuAvailable, selection.CanOpenAssistantMenu(status));
        Assert.False(selection.HasCurrentRoster);
        Assert.False(selection.HasSessionAccess);
        Assert.False(selection.IsReady);
        Assert.Null(selection.AgentId);
    }

    [Fact]
    public void GatewaySwitch_DoesNotAdmitThePreviousGatewayRosterOrSelection()
    {
        using var previous = JsonDocument.Parse("""{"defaultId":"main","agents":[{"id":"main"},{"id":"coordinator"}]}""");
        using var current = JsonDocument.Parse(
            """{"defaultId":"main","selectionRequired":true,"agents":[{"id":"main"},{"id":"coordinator"}]}""");
        var selection = new WorkspaceAgentSelection();
        selection.Refresh("previous", previous.RootElement, WorkspaceProjection.Agents(previous.RootElement, []), true);
        selection.Select("coordinator");

        selection.Refresh("current", previous.RootElement, WorkspaceProjection.Agents(previous.RootElement, []));
        Assert.Null(selection.AgentId);
        Assert.False(selection.HasCurrentRoster);
        Assert.False(selection.IsReady);
        selection.Refresh("current", null, [], true);
        selection.Refresh("current", current.RootElement, WorkspaceProjection.Agents(current.RootElement, []), true);
        Assert.Null(selection.AgentId);
        Assert.True(selection.HasCurrentRoster);
        Assert.False(selection.IsReady);

        selection.Select("coordinator");
        selection.Refresh("current", current.RootElement, WorkspaceProjection.Agents(current.RootElement, []));
        Assert.Equal("coordinator", selection.AgentId);
        Assert.True(selection.IsReady);
    }

    [Fact]
    public void InitialCachedRoster_WaitsForCurrentResponseWithoutLosingRequestedAgent()
    {
        using var roster = JsonDocument.Parse(
            """{"defaultId":"main","selectionRequired":true,"agents":[{"id":"main"},{"id":"coordinator"}]}""");
        var agents = WorkspaceProjection.Agents(roster.RootElement, []);
        var selection = new WorkspaceAgentSelection();
        selection.Select("coordinator");

        selection.Refresh("gateway", roster.RootElement, agents);

        Assert.Equal("coordinator", selection.AgentId);
        Assert.False(selection.HasCurrentRoster);
        Assert.False(selection.IsReady);
        selection.Refresh("gateway", roster.RootElement, agents, rosterUpdated: true);
        Assert.Equal("coordinator", selection.AgentId);
        Assert.True(selection.HasCurrentRoster);
        Assert.True(selection.IsReady);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("replacement")]
    public void RemovedSession_CannotReturnThroughHistory(string? replacement)
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "kept"));
        history.Navigate(new(WorkspacePageId.Home, "removed"));
        history.Navigate(new(WorkspacePageId.Notifications));
        history.GoBack();
        Assert.True(history.RemoveSession("removed", replacement));
        Assert.Equal(replacement, history.Current.SessionKey);
        Assert.Equal(replacement, history.ChatDestination.SessionKey);
        while (history.GoBack())
            Assert.NotEqual("removed", history.Current.SessionKey);
        while (history.GoForward())
            Assert.NotEqual("removed", history.Current.SessionKey);
    }

    [Fact]
    public void RemovingRememberedChat_PreservesNotificationsAndUnrelatedHistory()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "kept"));
        history.Navigate(new(WorkspacePageId.Home, "removed"));
        history.Navigate(new(WorkspacePageId.Notifications));
        Assert.False(history.RemoveSession("removed", null));
        Assert.Equal(WorkspacePageId.Notifications, history.Current.Page);
        Assert.Null(history.ChatDestination.SessionKey);
        Assert.True(history.GoBack());
        Assert.Equal("kept", history.Current.SessionKey);
    }

    [Fact]
    public void Workspace_OnlyHomeAndFooterNotificationsHaveRoutableDestinations()
    {
        Assert.Equal(new[] { WorkspacePageId.Home, WorkspacePageId.Notifications, WorkspacePageId.Settings },
            Enum.GetValues<WorkspacePageId>());
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
            "sessions", "usage", "activity", "tasks", "meetings", "apps", "portals", "more", "agentevents" }
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

    [Theory]
    [InlineData("dashboards")]
    [InlineData("dashboards?session=agent%3Amain%3Adashboard%3Aops&agent=main")]
    public void DashboardsRoutes_AlwaysUseStandalonePagePresentation_AndShareWorkspaceHistory(string tag)
    {
        var destination = WorkspaceDestination.Settings(tag);
        Assert.True(destination.Embedded);
        Assert.Equal(tag, destination.CompanionTag);
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "kept-chat"));
        history.Navigate(destination);
        Assert.True(history.GoBack());
        Assert.Equal("kept-chat", history.Current.SessionKey);
        Assert.True(history.GoForward());
        Assert.Equal(destination, history.Current);
        Assert.False(WorkspaceDestination.Settings("connection").Embedded);
    }

    [Fact]
    public void Settings_ReturnsToExactPriorChat_AndForwardRestoresSection()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        history.Navigate(WorkspaceDestination.Settings("permissions"));
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.ChatDestination);

        Assert.True(history.GoBack());
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
        Assert.True(history.GoForward());
        Assert.True(history.GoForward());
        Assert.Equal(WorkspaceDestination.Settings("permissions"), history.Current);
        Assert.False(history.Navigate(WorkspaceDestination.Settings("permissions")));
    }

    [Fact]
    public void EmbeddedSettings_IsDistinctHistoryEntry_AndReturnsToChat()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        Assert.True(history.Navigate(WorkspaceDestination.Settings("permissions", embedded: true)));
        Assert.True(history.Current.Embedded);
        Assert.False(history.Navigate(WorkspaceDestination.Settings("permissions", embedded: true)));
        Assert.True(history.Navigate(WorkspaceDestination.Settings("permissions")));
        Assert.False(history.Current.Embedded);
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.ChatDestination);
        Assert.True(history.GoBack());
        Assert.True(history.Current.Embedded);
    }

    [Fact]
    public void SettingsExitFocus_SameSavedControl_HistoryRestores_SidebarActivationDoesNot()
    {
        var saved = new object();
        Assert.Same(saved, WorkspaceDestination.SettingsExitFocus(saved, SettingsExitCause.History));
        Assert.Null(WorkspaceDestination.SettingsExitFocus(saved, SettingsExitCause.SidebarActivation));
        Assert.Null(WorkspaceDestination.SettingsExitFocus<object>(null, SettingsExitCause.History));
    }

    [Fact]
    public void PrunedFallback_KeepsEmbeddedPresentation_OnlyForSettings()
    {
        Assert.Equal(WorkspaceDestination.Settings("connection", embedded: true),
            WorkspaceDestination.PrunedFallback(WorkspaceDestination.Settings("cron", embedded: true), "connection"));
        Assert.Equal(WorkspaceDestination.Settings("connection"),
            WorkspaceDestination.PrunedFallback(WorkspaceDestination.Settings("cron"), "connection"));
        Assert.Equal(WorkspaceDestination.Settings("connection"),
            WorkspaceDestination.PrunedFallback(new(WorkspacePageId.Home, "chat-a"), "connection"));

        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("cron", embedded: true));
        Assert.True(history.PruneSettings(tag => tag == "cron",
            WorkspaceDestination.PrunedFallback(history.Current, "connection")));
        Assert.Equal(WorkspaceDestination.Settings("connection", embedded: true), history.Current);
    }

    [Fact]
    public void ChatSessionCompletingInSettings_DoesNotReplaceSettings()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        Assert.True(history.SelectChatSession("chat-b"));
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
        Assert.Equal(new(WorkspacePageId.Home, "chat-b"), history.ChatDestination);
    }

    [Fact]
    public void RemoveSettingsEntries_PrunesOnlyMatchingSectionsFromBothStacks()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("sessions"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        history.Navigate(WorkspaceDestination.Settings("instances"));
        history.GoBack();
        history.RemoveSettingsEntries(tag => tag is "sessions" or "instances");
        Assert.False(history.CanGoForward);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
        Assert.True(history.GoForward());
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
    }

    [Fact]
    public void RemovedSession_IsPrunedAroundSettingsEntries()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "removed"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        Assert.False(history.RemoveSession("removed", "kept"));
        Assert.Equal(new(WorkspacePageId.Home, "kept"), history.ChatDestination);
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.NotEqual("removed", history.Current.SessionKey);
    }

    [Fact]
    public void PruneSettings_ReplacesPrunedCurrentWithFallbackWithoutAddingHistory()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        history.Navigate(WorkspaceDestination.Settings("instances"));
        Assert.True(history.PruneSettings(tag => tag == "instances", WorkspaceDestination.Settings("connection")));
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
        // The adjacent duplicate "connection" entry collapses so Back skips to chat.
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
    }

    [Fact]
    public void PruneSettings_WithCurrentFallback_KeepsCurrentAndCollapsesAdjacentDuplicates()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "chat-a"));
        history.Navigate(WorkspaceDestination.Settings("settings"));
        history.Navigate(WorkspaceDestination.Settings("instances"));
        history.Navigate(WorkspaceDestination.Settings("settings"));
        Assert.False(history.PruneSettings(tag => tag == "instances", history.Current));
        Assert.Equal(WorkspaceDestination.Settings("settings"), history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home, "chat-a"), history.Current);
    }

    [Fact]
    public void SettingsTags_KeepAboutAndAgentRoutesDistinct()
    {
        Assert.Equal("about", WorkspaceNavigation.NormalizeSettingsTag("about", "main"));
        Assert.Equal("about", WorkspaceNavigation.NormalizeSettingsTag("info", "main"));
        Assert.NotEqual(
            WorkspaceNavigation.NormalizeSettingsTag("settings", "main"),
            WorkspaceNavigation.NormalizeSettingsTag("about", "main"));
        Assert.NotEqual(
            WorkspaceNavigation.NormalizeSettingsTag("agent:alpha", "main"),
            WorkspaceNavigation.NormalizeSettingsTag("agent:beta", "main"));
    }

    [Fact]
    public void BackToApp_ReturnsToRetainedChatAcrossSettingsSections_AsOneHistoryEntry()
    {
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "agent:main:a"));
        history.Navigate(WorkspaceDestination.Settings("connection"));
        history.Navigate(WorkspaceDestination.Settings("permissions"));
        Assert.Equal(new WorkspaceDestination(WorkspacePageId.Home, "agent:main:a"), history.AppDestination);

        // A composer-created conversation that completes while Settings is visible is the retained chat.
        Assert.True(history.SelectChatSession("agent:main:b"));
        Assert.Equal(new WorkspaceDestination(WorkspacePageId.Home, "agent:main:b"), history.AppDestination);

        Assert.True(history.Navigate(history.AppDestination));
        Assert.True(history.GoBack());
        Assert.Equal(WorkspaceDestination.Settings("permissions"), history.Current);
    }

    [Fact]
    public void BackToApp_ReturnsToTheAppDestinationSettingsWasOpenedFrom()
    {
        var history = new WorkspaceNavigationHistory();
        Assert.Equal(new WorkspaceDestination(WorkspacePageId.Home), history.AppDestination);

        history.Navigate(new(WorkspacePageId.Notifications));
        history.Navigate(WorkspaceDestination.Settings("settings"));
        Assert.Equal(new WorkspaceDestination(WorkspacePageId.Notifications), history.AppDestination);

        // A pinned page beside the sidebar is part of the app; its in-page links open full Settings.
        var pinned = WorkspaceDestination.Settings("usage", embedded: true);
        history.Navigate(pinned);
        history.Navigate(WorkspaceDestination.Settings("config"));
        Assert.Equal(pinned, history.AppDestination);

        // An unavailable pinned page is never the return target; the retained chat is.
        history.PruneSettings(tag => tag == "usage", history.Current);
        Assert.Equal(new WorkspaceDestination(WorkspacePageId.Home), history.AppDestination);
    }

    [Fact]
    public void BackToApp_SkipsPinnedGatewayPageAfterDisconnect_WhileCurrentConfigStaysVisible()
    {
        // Pinned Usage -> full Config -> disconnect: Config is kept visible, so only history is pruned.
        var history = new WorkspaceNavigationHistory();
        history.Navigate(new(WorkspacePageId.Home, "agent:main:a"));
        history.Navigate(WorkspaceDestination.Settings("usage", embedded: true));
        history.Navigate(WorkspaceDestination.Settings("config"));

        // The window passes the current page as the fallback, so Config stays and nothing re-renders.
        history.PruneSettings(HubPageRegistry.IsGatewayPageTag, history.Current);

        Assert.Equal(WorkspaceDestination.Settings("config"), history.Current);
        Assert.Equal(new WorkspaceDestination(WorkspacePageId.Home, "agent:main:a"), history.AppDestination);
    }

    [Theory]
    [InlineData(false, false, false, false, false, true)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, false)]
    public void SettingsEscape_LeavesFullSettingsOnlyWhenNothingElseOwnsEscape(
        bool embedded, bool handled, bool isRepeat, bool overlayOpen, bool focusInEditor, bool exits)
    {
        Assert.Equal(exits, SettingsEscapePolicy.ExitsSettings(
            WorkspaceDestination.Settings("connection", embedded), handled, isRepeat, overlayOpen, focusInEditor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsEscape_DoesNothingOutsideSettings(bool notifications)
    {
        var page = notifications ? WorkspacePageId.Notifications : WorkspacePageId.Home;
        Assert.False(SettingsEscapePolicy.ExitsSettings(new(page), false, false, false, false));
    }

    [Fact]
    public void SettingsHistory_BackAndForwardWorkWithoutAnyGatewayState()
    {
        // The unconfigured path uses the same history: Settings opens, Back reaches chat, Forward returns.
        var history = new WorkspaceNavigationHistory();
        history.Navigate(WorkspaceDestination.Settings("connection"));
        Assert.True(history.GoBack());
        Assert.Equal(WorkspacePageId.Home, history.Current.Page);
        Assert.True(history.GoForward());
        Assert.Equal(WorkspaceDestination.Settings("connection"), history.Current);
    }

    [Fact]
    public void SettingsSameWindow_HasOneKeyboardOwnerOneHistoryAndSetupBypass()
    {
        var view = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        var viewXaml = File.ReadAllText(Source("Controls", "SettingsShellView.xaml"));
        Assert.DoesNotContain("OnRootPreviewKeyDown", view + viewXaml);
        Assert.DoesNotContain("GoBack", view);
        Assert.DoesNotContain("GoForward", view);
        Assert.DoesNotContain("BackStack", view);
        Assert.DoesNotContain("typeof(ChatPage)", view);
        Assert.DoesNotContain("new ChatPage", view);
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        var core = manager[manager.IndexOf("private Task<bool> ShowHubCoreAsync", StringComparison.Ordinal)..];
        core = core[..core.IndexOf("private bool ShowWorkspace", StringComparison.Ordinal)];
        var settingsRoute = core[core.IndexOf("}, tag =>", StringComparison.Ordinal)..];
        Assert.DoesNotContain("RequiresSetup", settingsRoute);
        Assert.Contains("settingsTag: tag", settingsRoute);

        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.DoesNotContain("ShowHub(\"command-center\")", workspace);
        Assert.Contains("_settings?.Deactivate()", workspace);
        Assert.Contains("settings.Dispose()", workspace);
    }

    /// <summary>Models WorkspaceWindow's modal policy over the real DeferredNavigation: Navigate, prune and dialog stack.</summary>
    private sealed class ModalHost
    {
        public readonly DeferredNavigation Deferred = new();
        public readonly List<string> Renders = new();
        public readonly List<string> ModalRenders = new();
        public int Modals;
        public string Destination = "page";

        public void Navigate(string destination)
        {
            if (Deferred.ShouldDefer(destination != Destination) && Modals > 0)
            {
                Deferred.Defer(() => Navigate(destination));
                Deferred.TryArm();
                return;
            }
            if (destination != Destination) Destination = destination;
            Render();
        }

        public void Prune(string fallback)
        {
            Destination = fallback;
            if (Modals > 0 || Deferred.HasPending)
            {
                Deferred.DeferRender();
                if (Modals > 0) Deferred.TryArm();
            }
            else Render();
        }

        public void CloseDialog()
        {
            Modals--;
            if (!Deferred.Run(() => Modals > 0, Render)) Deferred.TryArm();
        }

        public void Render()
        {
            Renders.Add(Destination);
            if (Modals > 0) ModalRenders.Add(Destination);
        }
    }

    [Fact]
    public void DeferredNavigation_PrunedRenderRunsBeforeDeferredForwardAndOnlyOnce()
    {
        var host = new ModalHost { Modals = 1 };
        host.Navigate("forward");
        host.Prune("fallback");
        host.Navigate("latest");
        host.CloseDialog();
        Assert.Equal(new[] { "fallback", "latest" }, host.Renders);
        Assert.Empty(host.ModalRenders);
        Assert.False(host.Deferred.HasPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredNavigation_PruneThenSameFallbackNeverRendersUnderDialog(bool withEarlierIntent)
    {
        var host = new ModalHost { Modals = 1 };
        if (withEarlierIntent) host.Navigate("other");
        host.Prune("fallback");
        host.Navigate("fallback");
        Assert.Empty(host.Renders);
        host.CloseDialog();
        Assert.Equal(new[] { "fallback", "fallback" }, host.Renders);
        Assert.Empty(host.ModalRenders);
    }

    [Fact]
    public void DeferredNavigation_PruneWithoutIntentThenRepeatedFallbackWaitsForSecondDialog()
    {
        var host = new ModalHost { Modals = 1 };
        host.Prune("fallback");
        host.Navigate("fallback");
        // Dialog A closes while dialog B is already open: nothing may be consumed or rendered.
        host.Modals = 2;
        host.CloseDialog();
        Assert.Empty(host.Renders);
        Assert.True(host.Deferred.HasPending);
        host.CloseDialog();
        Assert.Empty(host.ModalRenders);
        Assert.NotEmpty(host.Renders);
        Assert.False(host.Deferred.HasPending);
    }

    [Fact]
    public void DeferredNavigation_PruneWithoutRepeatRendersOnceAfterLastDialog()
    {
        var host = new ModalHost { Modals = 1 };
        host.Prune("fallback");
        host.Modals = 2;
        host.CloseDialog();
        Assert.Empty(host.Renders);
        host.CloseDialog();
        Assert.Equal(new[] { "fallback" }, host.Renders);
        Assert.Empty(host.ModalRenders);
    }

    [Fact]
    public void DeferredNavigation_ClearDropsPendingWork()
    {
        var deferred = new DeferredNavigation();
        var ran = false;
        deferred.Defer(() => ran = true);
        deferred.DeferRender();
        deferred.Clear();
        Assert.True(deferred.Run(() => false, () => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void WorkspaceWindow_ModalPolicyRetainsRenderAndRejectsNativeSetupUnderDialog()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("_deferred.ShouldDefer(destination != Destination) && DeferWhileModalOpen(", workspace);
        Assert.Contains("ArmDeferredDrain(next)", workspace);
        var admission = workspace[workspace.IndexOf("internal void NavigateNativeSetup", StringComparison.Ordinal)..
            workspace.IndexOf("internal async Task WaitForNativeSetupAsync", StringComparison.Ordinal)];
        Assert.Contains("if (OpenModalDialog() is not null)", admission);
        Assert.DoesNotContain("IsSettingsVisible", admission);
        Assert.Contains("request.WorkspaceDestination", admission);
        Assert.Contains("throw new SetupNativeOwnershipException()", admission);
        var modalAdmission = admission.IndexOf("if (OpenModalDialog()", StringComparison.Ordinal);
        Assert.True(modalAdmission < admission.IndexOf("_chat.BindNativeSetupRequest(request)", StringComparison.Ordinal));
        Assert.True(modalAdmission < admission.IndexOf("_chat.Initialize(this)", StringComparison.Ordinal));
        Assert.Contains("if (destination != Destination) Navigate(destination);\n            return;",
            admission.Replace("\r\n", "\n", StringComparison.Ordinal));
        var defer = workspace[workspace.IndexOf("private bool DeferWhileModalOpen", StringComparison.Ordinal)..
            workspace.IndexOf("private void ArmDeferredDrain", StringComparison.Ordinal)];
        Assert.DoesNotContain("IsSettingsVisible", defer);
    }

    [Fact]
    public void ChatCaptureProgress_UsesPerInvocationCallbacksAndKeepsLifetimeFence()
    {
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var capture = chat[chat.IndexOf("private async Task<string?> VoiceTranscribeAsync", StringComparison.Ordinal)..
            chat.IndexOf("private async Task ReadChatTextAloudAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("voiceService.TranscriptionReceived", capture);
        Assert.DoesNotContain("voiceService.AudioLevelChanged", capture);
        Assert.Contains("ListenOnceAsync(args, cancellationToken, OnTranscription, OnAudioLevel)", capture);
        Assert.Contains("!cancellationToken.IsCancellationRequested && _pageActive", capture);
        Assert.Contains("generation == _surfaceGeneration && ReferenceEquals(host, _reactorHost)", capture);
    }

    [Fact]
    public void SettingsTransition_SuspendsCaptureBeforeHidingRetainedChat_AndSearchKeepsLocalAliases()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var render = workspace[workspace.IndexOf("private void RenderSettings()", StringComparison.Ordinal)..
            workspace.IndexOf("private void LeaveSettings()", StringComparison.Ordinal)];
        Assert.True(render.IndexOf("_chat.SuspendSurface()", StringComparison.Ordinal) >= 0);
        Assert.True(render.IndexOf("_chat.SuspendSurface()", StringComparison.Ordinal) <
            render.IndexOf("NavigationSection.Visibility = Visibility.Collapsed", StringComparison.Ordinal));
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var suspension = chat[chat.IndexOf("internal void SuspendSurface()", StringComparison.Ordinal)..
            chat.IndexOf("public void TriggerAutoStartVoice()", StringComparison.Ordinal)];
        Assert.Contains("_pendingVoice.Cancel()", suspension);
        Assert.Contains("_reactorHost?.SetHostVisible(false)", suspension);
        Assert.DoesNotContain("DisposeReactorHost();", suspension);
        Assert.Contains("_reactorHost?.SetHostVisible(true)", chat);
        Assert.Contains("if (IsCurrentCapture()) host?.SetVoiceTranscript(text)", chat);
        Assert.Contains("if (IsCurrentCapture()) host?.SetVoiceAudioLevel(level)", chat);
        Assert.Contains("e.Key == global::Windows.System.VirtualKey.E ||", workspace);
        Assert.Contains("(IsSettingsVisible && e.Key == global::Windows.System.VirtualKey.F)", workspace);
    }

    [Fact]
    public void ModalPolicy_DefersPrunesAndNativeSetupInsteadOfReplacingUnderDialogs()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("ArmDeferredDrain(modal)", workspace);
        Assert.Contains("_deferred.DeferRender()", workspace);
        Assert.Contains("if (destination != Destination) Navigate(destination);", workspace);
        Assert.Contains("popup.Child is ContentDialog", manager);
    }

    [Fact]
    public void LeavingSettings_StopsDebugPage_NeverCancelsDialogs_AndNullStatusRefreshes()
    {
        var view = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        var debug = File.ReadAllText(Source("Pages", "DebugPage.xaml.cs"));
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("(ContentFrame.Content as DebugPage)?.Deactivate()", view);
        Assert.Contains("internal void Deactivate()", debug);
        // Dialog policy: Settings never cancels a ContentDialog; the host defers navigation instead.
        Assert.DoesNotContain("dialog.Hide()", view);
        Assert.DoesNotContain("_foreignDialogs", view);
        Assert.Contains("DeferWhileModalOpen(NavigateBack)", workspace);
        Assert.Contains("DeferWhileModalOpen(NavigateForward)", workspace);
        Assert.Contains("DeferWhileModalOpen(() => Navigate(destination, preserveConversation))", workspace);
        Assert.DoesNotContain(".Hide()", workspace[workspace.IndexOf("private bool DeferWhileModalOpen", StringComparison.Ordinal)..][..900]);
        Assert.Contains("internal void UpdateConnectionStatus(GatewayConnectionSnapshot? snapshot, ConnectionStatus status)", workspace);
        Assert.DoesNotContain("UpdateStatus(", view);
        Assert.DoesNotContain("if (snapshot is not null) _settings", workspace);
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
    public void SessionHistory_RestoresExactKeysAndClearsForwardOnlyForNewDestinations()
    {
        var history = new WorkspaceNavigationHistory();
        var first = new WorkspaceDestination(WorkspacePageId.Home, "agent:main:first");
        var second = new WorkspaceDestination(WorkspacePageId.Home, "agent:research:second");
        Assert.True(history.Navigate(first));
        Assert.True(history.Navigate(second));
        Assert.False(history.Navigate(second));
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.False(history.Navigate(first));
        Assert.True(history.CanGoForward);
        Assert.True(history.GoForward());
        Assert.Equal(second, history.Current);
        Assert.True(history.Navigate(new(WorkspacePageId.Notifications)));
        Assert.Equal(second, history.ChatDestination);
        Assert.True(history.GoBack());
        Assert.Equal(second, history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.Equal(first, history.ChatDestination);
        Assert.True(history.Navigate(new(WorkspacePageId.Home, "agent:main:third")));
        Assert.False(history.CanGoForward);
        Assert.True(history.GoBack());
        Assert.Equal(first, history.Current);
        Assert.True(history.GoBack());
        Assert.Equal(new(WorkspacePageId.Home), history.Current);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void SessionHistory_IsAppliedBeforeChatInitializationWithoutIntermediateHomeRoute()
    {
        var source = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("Navigate(new(WorkspacePageId.Home, sessionKey))", source);
        var render = source[source.IndexOf("private void RenderDestination()", StringComparison.Ordinal)..
            source.IndexOf("private void UpdateNavigationSelection()", StringComparison.Ordinal)];
        Assert.Contains("_chat.QueueSession(sessionKey)", render);
        Assert.True(render.IndexOf("_chat.QueueSession(sessionKey)", StringComparison.Ordinal) <
            render.IndexOf("_chat.Initialize(this)", StringComparison.Ordinal));
        Assert.DoesNotContain("_selectedSessionKey", source);
        Assert.Contains("destination = _navigation.ChatDestination;", source);
        Assert.Contains("preserveConversation: false", source);
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        var navigate = manager[manager.IndexOf("else if (!preserveCurrent)", StringComparison.Ordinal)..
            manager.IndexOf("if (_workspaceWindow.AppWindow.Presenter", StringComparison.Ordinal)];
        Assert.Contains("destination = destination with { SessionKey = sessionKey };", navigate);
        Assert.Contains("admitted = _workspaceWindow.TryNavigate(destination);", navigate);
        Assert.DoesNotContain("_workspaceWindow.Navigate(", navigate);
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
            ("ChatFooter", "NavigationView.PaneFooter")
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
        var chrome = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "PaneToggleChrome");
        var settings = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SettingsSection");
        var history = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SettingsHistoryBar");
        Assert.Same(chrome, reopen.Parent);
        Assert.Same(settings.Parent, chrome.Parent);
        Assert.Same(navigation.Parent!.Parent, chrome.Parent);
        var layers = chrome.Parent!.Elements().ToList();
        Assert.True(layers.IndexOf(settings) < layers.IndexOf(chrome));
        Assert.True(layers.IndexOf(chrome) < layers.IndexOf(history));
        Assert.Equal("1", (string?)chrome.Attribute("Grid.Row"));
        Assert.Equal("{Binding Visibility, ElementName=NavigationSection}", (string?)chrome.Attribute("Visibility"));
        Assert.Equal("48", (string?)chrome.Attribute("Width"));
        Assert.Equal("56", (string?)chrome.Attribute("Height"));
        Assert.Null(chrome.Attribute("Background"));
        Assert.True(settings.ElementsAfterSelf().Contains(chrome),
            "Pane buttons must stay above full-frame page backdrops.");
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
        Assert.Contains("session.Key == Destination.SessionKey", code);
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
        Assert.DoesNotContain(headers, element => (string?)element.Attribute(x + "Name") is "PagesHeader" or "PagesEmpty");
        var separator = document.Descendants().Single(element => element.Name.LocalName == "NavigationViewItemSeparator");
        Assert.Equal("home", (string?)separator.ElementsBeforeSelf().Last().Attribute("Tag"));
        var customize = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "PagesCustomizeButton");
        Assert.Contains(customize.Ancestors(), element => (string?)element.Attribute(x + "Name") == "HomeItem");
        var sessions = headers.Single(element => (string?)element.Attribute(x + "Name") == "SessionsHeader");
        Assert.Equal("SessionsSeparator", (string?)sessions.ElementsBeforeSelf().Last().Attribute(x + "Name"));
        Assert.Equal("True", (string?)sessions.Attribute("IsEnabled"));
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "ContentPresenter"
            && (string?)element.Attribute("Content") == "{TemplateBinding Content}");
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "TextBlock"
            && (string?)element.Attribute("Style") == "{StaticResource NavigationViewItemHeaderTextStyle}");
        Assert.Contains(sessions.Descendants(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Target") == "HeaderContent.Visibility"
            && (string?)element.Attribute("Value") == "Collapsed");
        var newAgent = assistant.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "NewAgentOption");
        Assert.Equal("ComboBoxItem", newAgent.Name.LocalName);
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "NewConversationButton");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("desiredItems.Add(NewAgentOption)", code);
        Assert.DoesNotContain("AssistantSelector.Items.Clear()", code);
        Assert.Contains("RestoreAssistantSelection();", code);
        Assert.Contains("ReferenceEquals(AssistantSelector.SelectedItem, NewAgentOption)", code);
        Assert.Contains("AsyncEventHandlerGuard.Run(NewAgentAsync", code);
        Assert.Contains("new AgentCreationDialog", code);
        foreach (var name in new[] { "NewSessionButton", "BackButton", "ForwardButton" })
        {
            var button = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)button.Attribute("Style"));
        }
    }

    [Fact]
    public void SidebarActionBackplates_AlignWithNativeNavigationInsetsAndCorners()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var assistant = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "AssistantSelector");
        Assert.Equal("4,8,4,8", (string?)assistant.Parent!.Attribute("Margin"));
        var footerDocument = XDocument.Load(Source("Controls", "WorkspacePaneFooter.xaml"));
        var footer = footerDocument.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "FooterGrid");
        Assert.Equal("4,8,4,8", (string?)footer.Attribute("Margin"));
        var header = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SessionsHeader");
        Assert.Contains(header.Descendants(), element =>
            (string?)element.Attribute(x + "Key") == "NavigationViewItemInnerHeaderMargin" && element.Value == "16,0,4,0");
        foreach (var (source, name) in new[]
        {
            (document, "AssistantSelector"), (document, "NewSessionButton"),
            (footerDocument, "DashboardButton"), (footerDocument, "NotificationsButton")
        })
        {
            var control = source.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("{ThemeResource ControlCornerRadius}", (string?)control.Attribute("CornerRadius"));
            if (name != "AssistantSelector")
            {
                Assert.Equal("40", (string?)control.Attribute("Width"));
                Assert.Equal("{ThemeResource NavigationViewItemOnLeftMinHeight}", (string?)control.Attribute("Height"));
                Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)control.Attribute("Style"));
            }
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
    public void NativeFooter_DashboardAndNotificationsAreIndependentAndRightOfOwner()
    {
        var doc = XDocument.Load(Source("Controls", "WorkspacePaneFooter.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var owner = doc.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "OwnerButton");
        var dashboard = doc.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "DashboardButton");
        var bell = doc.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "NotificationsButton");
        Assert.Same(owner.Parent, bell.Parent);
        Assert.Same(owner.Parent, dashboard.Parent);
        Assert.Equal("1", (string?)dashboard.Attribute("Grid.Column"));
        Assert.Equal("2", (string?)bell.Attribute("Grid.Column"));
        Assert.Equal("OnDashboardClick", (string?)dashboard.Attribute("Click"));
        Assert.Equal("WorkspaceShell_DashboardButton", (string?)dashboard.Attribute(x + "Uid"));
        Assert.Equal("OnNotificationsClick", (string?)bell.Attribute("Click"));
        var footerCode = File.ReadAllText(Source("Controls", "WorkspacePaneFooter.xaml.cs"));
        Assert.Contains("new FlyoutShowRequest(NotificationsFlyout, ReopenNotifications)", footerCode);
        Assert.Contains("_notificationsShow.DeferUntilClosed();", footerCode);
        Assert.Contains("_notificationsShow.Cancel();", footerCode);
        var flyout = Assert.Single(bell.Descendants(), element => element.Name.LocalName == "Flyout");
        Assert.Equal("OnNotificationsOpening", (string?)flyout.Attribute("Opening"));
        Assert.Equal("OnNotificationsClosed", (string?)flyout.Attribute("Closed"));
        Assert.Single(doc.Descendants(), node => (string?)node.Attribute(x + "Name") == "NotificationsButton");
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)owner.Attribute("Style"));
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)dashboard.Attribute("Style"));
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)bell.Attribute("Style"));
        Assert.Single(owner.Descendants(), element => element.Name.LocalName == "PersonPicture");
        // Monochrome Segoe Fluent glyphs in the default text color: ViewDashboard and Ringer.
        var dashboardGlyph = Assert.Single(dashboard.Elements(), element => element.Name.LocalName == "FontIcon");
        Assert.Equal("\uF246", (string?)dashboardGlyph.Attribute("Glyph"));
        Assert.Null(dashboardGlyph.Attribute("Foreground"));
        var bellGlyph = Assert.Single(bell.Descendants(), element => element.Name.LocalName == "FontIcon");
        Assert.Equal("\uEA8F", (string?)bellGlyph.Attribute("Glyph"));
        Assert.Null(bellGlyph.Attribute("Foreground"));
        // The Inbox badge keeps both tones as ThemeResource-bound borders: accent, and caution while an approval waits.
        var badges = bell.Descendants().Where(element => element.Name.LocalName == "Border").ToList();
        Assert.Equal(["NotificationsBadge", "NotificationsAttentionBadge"], badges.Select(element => (string?)element.Attribute(x + "Name")));
        Assert.Equal("{ThemeResource AccentFillColorDefaultBrush}", (string?)badges[0].Attribute("Background"));
        Assert.Equal("{ThemeResource SystemFillColorCautionBrush}", (string?)badges[1].Attribute("Background"));
        Assert.DoesNotContain(bell.Descendants(), element => element.Name.LocalName == "InfoBadge");
        Assert.DoesNotContain(doc.Descendants(), element => element.Name.LocalName is "ImageIcon" or "SvgImageSource");
        Assert.Equal(new[] { "OwnerAvatarStatusDot", "OwnerStatusDot" },
            owner.Descendants().Where(element => element.Name.LocalName == "FontIcon")
                .Select(element => (string?)element.Attribute(x + "Name")));
        var window = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        Assert.DoesNotContain(window.Descendants(), element => (string?)element.Attribute(x + "Key") == "WorkspaceSubtleButton");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("footer.SetOwnerMenu(BuildOwnerMenu(footer))", code);
        Assert.Contains("footer.DashboardRequested += (_, _) => OpenDashboard();", code);
        Assert.Contains("((IAppCommands)CurrentApp).OpenDashboard(path ?? ResolveDashboardPath())", code);
        Assert.Contains("destination.SessionKey ?? _chat.CurrentSessionKey", code);
        Assert.Contains("OpenCompanion(model.SettingsTarget)", code);
        Assert.Contains("OpenDashboard(WorkspaceAccountMenu.PairDeviceDashboardPath)", code);
        Assert.Contains("OpenCompanion(CompanionPageId.About)", code);
        Assert.Contains("MenuItem(\"GetApps\", null, null, () => _ = OpenLinkAsync(\"https://docs.openclaw.ai/platforms\"))", code);
        Assert.Contains("OpenLinkAsync(\"https://github.com/openclaw/openclaw-windows-node\")", code);
        Assert.Contains("help.Items.Add(github)", code);
        // Usage left the account menu; it stays in Settings navigation and search.
        Assert.DoesNotContain("OpenCompanion(CompanionPageId.Usage)", code);
        Assert.Equal(new[] { "OwnerUpdateDot" },
            owner.Descendants().Where(element => element.Name.LocalName == "Ellipse")
                .Select(element => (string?)element.Attribute(x + "Name")));
        Assert.DoesNotContain("WebView", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml")));
    }

    [Fact]
    public void DashboardShortcut_IsCtrlShiftD_InBothSections()
    {
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var handler = code[code.IndexOf("private void OnKeyDown(", StringComparison.Ordinal)..];
        Assert.Contains("if (ctrl && shift && !alt && e.Key == global::Windows.System.VirtualKey.D)", handler);
        Assert.Contains("if (!e.KeyStatus.WasKeyDown) OpenDashboard();", handler);
        Assert.True(handler.IndexOf("VirtualKey.D)", StringComparison.Ordinal) <
            handler.IndexOf("OpenCommandCenter()", StringComparison.Ordinal),
            "Ctrl+Shift+D must be handled before the Ctrl-only search aliases.");
        var root = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml")).Root!;
        Assert.Contains(root.Descendants(), element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "Root" &&
            (string?)element.Attribute("PreviewKeyDown") == "OnKeyDown");
    }

    [Fact]
    public void AccountMenuGatewayList_UsesOneIconColumn_WithoutRadioCheckColumn()
    {
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var gateways = code[code.IndexOf("private void PopulateGatewayMenu(", StringComparison.Ordinal)..];
        gateways = gateways[..gateways.IndexOf("private static MenuFlyoutItem MenuItem(", StringComparison.Ordinal)];
        Assert.DoesNotContain("RadioMenuFlyoutItem", gateways);
        Assert.DoesNotContain("ToggleMenuFlyoutItem", gateways);
        Assert.Contains("item.Icon = new FontIcon { Style = ChatFooter.BadgeStyle(model.StatusAccent) };", gateways);
    }

    [Fact]
    public void AccountMenuMetadata_IsNotExposedAsKeyboardShortcuts()
    {
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var menu = code[code.IndexOf("private void PopulateOwnerMenu(", StringComparison.Ordinal)..];
        menu = menu[..menu.IndexOf("private void OpenAddGateway()", StringComparison.Ordinal)];
        Assert.DoesNotMatch(@"KeyboardAcceleratorTextOverride\s*=\s*(saved\.|statusLabel|AppVersionInfo\.)", menu);
    }

    [Fact]
    public void AccountMenuCurrentGateway_HasExplicitAccessibleStateAndDescription()
    {
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var gateways = code[code.IndexOf("private void PopulateGatewayMenu(", StringComparison.Ordinal)..];
        gateways = gateways[..gateways.IndexOf("private static MenuFlyoutItem MenuItem(", StringComparison.Ordinal)];
        Assert.Contains("AutomationProperties.SetItemStatus(item, Text(\"AccountCurrentGateway\"));", gateways);
        Assert.Contains("\"WorkspaceShell_AccountCurrentGatewayStatusFormat\"", gateways);
    }

    [Fact]
    public void AccountMenuAddGateway_KeepsAnAddOrEditFormAlreadyInProgress()
    {
        var page = File.ReadAllText(Source("Pages", "ConnectionPage.xaml.cs"));
        var begin = page[page.IndexOf("internal void BeginAddGateway()", StringComparison.Ordinal)..];
        begin = begin[..begin.IndexOf("private void OnEnterAddGateway(", StringComparison.Ordinal)];
        Assert.Contains("if (_userIntent == UserIntent.AddingGateway) return;", begin);
        Assert.Contains("OnEnterAddGateway(this, new RoutedEventArgs());", begin);
        Assert.True(begin.IndexOf("return;", StringComparison.Ordinal) < begin.IndexOf("OnEnterAddGateway(", StringComparison.Ordinal),
            "An in-progress Add or Edit form must be kept before the reset path runs.");
        Assert.Contains("page.BeginAddGateway()", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs")));
        // Re-navigating to the Connection page rebuilds it, so an open page is reused instead.
        var window = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var open = window[window.IndexOf("private void OpenAddGateway()", StringComparison.Ordinal)..];
        Assert.True(open.IndexOf("current.BeginAddGateway();", StringComparison.Ordinal) <
            open.IndexOf("OpenCompanion(CompanionPageId.Connection);", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsShortcut_IsCtrlComma_AndMatchesTheAccountMenuHint()
    {
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("SettingsShortcutKey = (global::Windows.System.VirtualKey)188;", code);
        Assert.Contains("SettingsShortcutText = \"Ctrl+,\";", code);
        var handler = code[code.IndexOf("private void OnKeyDown(", StringComparison.Ordinal)..];
        Assert.Contains("else if (ctrl && !shift && !alt && e.Key == SettingsShortcutKey)", handler);
        Assert.Contains("if (!e.KeyStatus.WasKeyDown) OpenCompanion(CompanionPageId.Settings);", handler);
        Assert.Contains("KeyboardAcceleratorTextOverride = model.SettingsTarget == CompanionPageId.Settings ? SettingsShortcutText : \"\"", code);
        // A stale open menu must not reconnect a connection that already recovered.
        Assert.Contains("if (WorkspaceAccountMenu.NeedsRetry(CurrentApp.ConnectionManager?.CurrentSnapshot?.OverallState, _state.Status))", code);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void TitleBarActions_ShowOnlyWhileNoPaneFooterIsVisible(bool settingsFooterVisible, bool chatPaneOpen, bool expected) =>
        Assert.Equal(expected, WorkspaceFooterPlacement.ShowTitleBarActions(settingsFooterVisible, chatPaneOpen));

    [Fact]
    public void TitleBarActions_TreatEmbeddedPinnedPagesAsChatSidebarFooter()
    {
        var source = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var method = source[source.IndexOf("private void UpdateTitleBarActions()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("WorkspaceFooterPlacement.ShowTitleBarActions", StringComparison.Ordinal)];
        Assert.Contains("_settings is { ContentOnly: false }", method);
        var apply = source[source.IndexOf("private void ApplySettingsPresentation(", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("private void UpdateTitleBarActions()", StringComparison.Ordinal)];
        Assert.Contains("UpdateTitleBarActions();", apply);
    }

    [Fact]
    public void SearchFlyouts_DeferOnlyRealClosesAndReplayThroughTheOpenPath()
    {
        // State transitions are unit-tested in DeferredFlyoutShowStateTests; this guards the WinUI wiring.
        var helper = File.ReadAllText(Source("Controls", "FlyoutShowRequest.cs"));
        Assert.Contains("flyout.Closing += (_, args) => _state.Closing(args.Cancel);", helper);
        Assert.Contains("if (!flyout.DispatcherQueue.TryEnqueue(Settle)) Settle();", helper);
        Assert.Contains("if (_state.Settle()) _replay();", helper);
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("new FlyoutShowRequest(WorkspaceSearchFlyout, OpenCommandCenter)", workspace);
        Assert.Contains("view.CommandCenterRequested = OpenCommandCenter;", workspace);
        var hide = workspace[workspace.IndexOf("private void HideFooterFlyouts()", StringComparison.Ordinal)..];
        Assert.Contains("_workspaceSearchShow.Cancel();", hide[..hide.IndexOf("\n    }", StringComparison.Ordinal)]);
        var settings = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        // Settings replays through the host so the window's modal-dialog deferral applies.
        Assert.Contains("new FlyoutShowRequest(CommandSearchFlyout, () => (CommandCenterRequested ?? OpenCommandCenter)())", settings);
        var transient = settings[settings.IndexOf("private void HideTransientSurfaces()", StringComparison.Ordinal)..];
        transient = transient[..transient.IndexOf("\n    }", StringComparison.Ordinal)];
        var cancel = transient.IndexOf("_commandSearchShow.Cancel();", StringComparison.Ordinal);
        var hideSearch = transient.IndexOf("CommandSearchFlyout.Hide();", StringComparison.Ordinal);
        Assert.True(cancel >= 0, "Leaving Settings must cancel a queued search reopen.");
        Assert.True(hideSearch > cancel, "The queued reopen is cancelled before the flyout is hidden.");
        Assert.Contains("if (_disposed || !IsLoaded) return;", settings);
    }

    [Fact]
    public void TitleBar_StaysCompactAndRefreshesDragRegionsWhenActionsToggle()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var titleBar = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "WorkspaceTitleBar");
        var expanded = titleBar.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "TitleBarExpandedHeight");
        Assert.Equal("32", expanded.Value);
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var update = code[code.IndexOf("private void UpdateTitleBarActions()", StringComparison.Ordinal)..];
        update = update[..update.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("WorkspaceTitleBar.RecomputeDragRegions();", update);
        Assert.Contains("TitleBarFooter.HideFlyouts();", update);
        var footer = File.ReadAllText(Source("Controls", "WorkspacePaneFooter.xaml.cs"));
        Assert.Contains("DashboardButton.Height = titleBar ? TitleBarButtonHeight : _paneButtonHeight;", footer);
        var footerXaml = XDocument.Load(Source("Controls", "WorkspacePaneFooter.xaml"));
        var grid = footerXaml.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "FooterGrid");
        Assert.Null(grid.Attribute("RowSpacing"));
        Assert.Null(grid.Attribute("ColumnSpacing"));
    }

    [Fact]
    public void TitleBarActions_UseTheSharedFooterAndFollowPaneAndSection()
    {
        var doc = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var titleBarFooter = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "TitleBarFooter");
        Assert.Equal("WorkspacePaneFooter", titleBarFooter.Name.LocalName);
        Assert.Equal("TitleBar.RightHeader", titleBarFooter.Parent!.Name.LocalName);
        Assert.Equal("Collapsed", (string?)titleBarFooter.Attribute("Visibility"));
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("TitleBarFooter.Layout = WorkspacePaneFooterLayout.TitleBar;", code);
        var presentation = code[code.IndexOf("private void UpdatePanePresentation()", StringComparison.Ordinal)..];
        Assert.Contains("UpdateTitleBarActions();", presentation[..presentation.IndexOf("private void UpdateTitleBarActions()", StringComparison.Ordinal)]);
        foreach (var method in new[] { "private void RenderSettings()", "private void LeaveSettings()" })
        {
            var body = code[code.IndexOf(method, StringComparison.Ordinal)..];
            body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
            Assert.Contains("UpdateTitleBarActions();", body);
            Assert.Contains("HideFooterFlyouts();", body);
        }
        var settings = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        Assert.Contains("PaneFooterControl.Layout = NavView.IsPaneOpen ? WorkspacePaneFooterLayout.Expanded : WorkspacePaneFooterLayout.Rail;", settings);
    }

    [Fact]
    public void RailOwner_UsesCompactPaddingAndRestoresExpandedPadding()
    {
        var footer = File.ReadAllText(Source("Controls", "WorkspacePaneFooter.xaml.cs"));
        Assert.Contains("OwnerButton.Padding = rail ? new Thickness(4) : new Thickness(8);", footer);
    }

    [Fact]
    public void OwnerFooterStatusDot_PrecedesTextAndSharesLiveThemeResources()
    {
        var doc = XDocument.Load(Source("Controls", "WorkspacePaneFooter.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dot = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "OwnerStatusDot");
        var detail = doc.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "OwnerDetail");
        Assert.Same(dot.Parent, detail.Parent);
        Assert.Equal("4", (string?)dot.Parent!.Attribute("ColumnSpacing"));
        Assert.Equal("1", (string?)detail.Attribute("Grid.Column"));
        Assert.Equal("FontIcon", dot.Name.LocalName);
        Assert.Equal("Raw", (string?)dot.Attribute("AutomationProperties.AccessibilityView"));
        Assert.Equal("False", (string?)dot.Attribute("IsHitTestVisible"));
        Assert.Equal("{StaticResource ConnectionBadgeNeutral}", (string?)dot.Attribute("Style"));
        Assert.Equal("CharacterEllipsis", (string?)detail.Attribute("TextTrimming"));
        var footer = File.ReadAllText(Source("Controls", "WorkspacePaneFooter.xaml.cs"));
        Assert.Contains("OwnerStatusDot.Style = style;", footer);
        Assert.Contains("OwnerAvatarStatusDot.Style = style;", footer);
        Assert.Contains("(Style)Resources[$\"ConnectionBadge{accent}\"]", footer);
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("Icon = new FontIcon { Style = ChatFooter.BadgeStyle(model.StatusAccent) }", code);
        foreach (var accent in new[] { "Neutral", "Success", "Caution", "Critical" })
        {
            var style = doc.Descendants().Single(element => (string?)element.Attribute(x + "Key") == $"ConnectionBadge{accent}");
            Assert.Contains(style.Elements(), setter =>
                (string?)setter.Attribute("Property") == "Foreground" &&
                (string?)setter.Attribute("Value") == $"{{ThemeResource SystemFillColor{accent}Brush}}");
        }
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
            Assert.Equal("0", (string?)toggle.Attribute("Padding"));
            var icon = Assert.Single(toggle.Descendants(), element => element.Name.LocalName == "FontIcon");
            Assert.Equal("16", (string?)icon.Attribute("FontSize"));
        }
    }

    [Fact]
    public void WindowManager_OwnsOneWorkspaceWindowForChatAndSettings_AndResetsScopeOnClose()
    {
        var code = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("WorkspaceNavigation.DispatchAsync(navigateTo", code);
        Assert.Contains("if (_workspaceWindow is null || _workspaceWindow.IsClosed)", code);
        Assert.Contains("_callbacks.ApplyTheme(_workspaceWindow)", code);
        Assert.Contains("TryClose(\"Workspace window\"", code);
        // One HWND: Settings is a Workspace destination, never a second window.
        Assert.DoesNotMatch(@"(?<![A-Za-z])HubWindow(?![A-Za-z_])", code);
        Assert.DoesNotContain("_hubWindow", code);
        Assert.DoesNotContain("ShowCompanion", code);
        Assert.DoesNotContain("OnHubClosed", code);
        Assert.False(File.Exists(Source("Windows", "HubWindow.xaml")));
        Assert.False(File.Exists(Source("Windows", "HubWindow.xaml.cs")));
        Assert.Contains("settingsTag: tag", code);
        // The navigation scope is reset when the single Workspace window closes.
        var closed = code[code.IndexOf("private void OnWorkspaceClosed", StringComparison.Ordinal)..];
        Assert.Contains("_workspaceWindow = null;", closed);
        Assert.Contains("ResetNavigationScope();", closed[..closed.IndexOf("private void ResetNavigationScope", StringComparison.Ordinal)]);
        Assert.Contains("SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_workspaceWindow))", code);

        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.DoesNotContain("GatewayRegistry", workspace);
        Assert.DoesNotContain("new OpenClawGatewayClient", workspace);
        Assert.Contains("CurrentApp.PageActivator?.Reset();", workspace);
        Assert.Contains("settings.Dispose();", workspace);

        var viewMarkup = File.ReadAllText(Source("Controls", "SettingsShellView.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Parse(viewMarkup);
        // Back to app replaced the rail's Chat item, matching the macOS Settings sidebar.
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("Tag") == "chat");
        var header = Assert.Single(document.Descendants(), element => element.Name.LocalName == "NavigationView.PaneCustomContent");
        var backToApp = Assert.Single(header.Descendants(), element => (string?)element.Attribute(x + "Name") == "BackToAppButton");
        Assert.Equal("SettingsBackToApp", (string?)backToApp.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("OnBackToAppClick", (string?)backToApp.Attribute("Click"));
        Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)backToApp.Attribute("Style"));
        var title = Assert.Single(header.Descendants(), element => (string?)element.Attribute(x + "Name") == "SettingsTitle");
        Assert.Equal("Level1", (string?)title.Attribute("AutomationProperties.HeadingLevel"));
        var view = File.ReadAllText(Source("Controls", "SettingsShellView.xaml.cs"));
        Assert.DoesNotContain("NavView_ItemInvoked", view + viewMarkup);
        var click = view[view.IndexOf("private void OnBackToAppClick", StringComparison.Ordinal)..
            view.IndexOf("private void NavView_SelectionChanged", StringComparison.Ordinal)];
        Assert.Contains("_active", click);
        Assert.Contains("ExitRequested", click);
        Assert.DoesNotContain("NavigateInternal", click);
        Assert.Contains("view.ExitRequested = ExitSettings;", workspace);
        var exit = workspace[workspace.IndexOf("private void ExitSettings", StringComparison.Ordinal)..
            workspace.IndexOf("private void OnRootKeyDown", StringComparison.Ordinal)];
        Assert.Contains("var destination = _navigation.AppDestination;", exit);
        Assert.Contains("Navigate(destination);", exit);
        // A deferred exit resolves its target after the dialog; unavailable pinned pages are pruned first.
        AssertInOrder(exit, "if (DeferWhileModalOpen(ExitSettings)) return;",
            "PruneUnavailableGatewayHistory();", "var destination = _navigation.AppDestination;");

        static void AssertInOrder(string source, params string[] fragments)
        {
            var cursor = 0;
            foreach (var fragment in fragments)
            {
                var index = source.IndexOf(fragment, cursor, StringComparison.Ordinal);
                Assert.True(index >= 0, $"Expected '{fragment}' after position {cursor}.");
                cursor = index + fragment.Length;
            }
        }
        Assert.Contains("KeyDown=\"OnRootKeyDown\"", File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml")));
        Assert.DoesNotMatch(@"new\s+ChatPage\b|typeof\(ChatPage\)|\bMountReactorChat\b", view);
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        Assert.Contains("Initialize(Window ownerWindow)", chat);
        Assert.Contains("ArgumentNullException.ThrowIfNull(ownerWindow);", chat);
        Assert.DoesNotContain("public void Initialize()", chat);
        Assert.Contains("GetWindowHandle(_ownerWindow)", chat);
        Assert.DoesNotContain("CurrentApp.ActiveHubWindow!.MountReactorChat", chat);
    }

    [Fact]
    public void WorkspaceRefocusAndPendingChat_PreserveUserIntent()
    {
        var manager = File.ReadAllText(Source("Services", "WindowManager.cs"));
        Assert.Contains("preserveCurrent: navigateTo is null or \"hub\"", manager);
        Assert.Contains("if (!preserveCurrent)", manager);
        Assert.Contains("_workspaceWindow.TryNavigate(destination)", manager);
        Assert.Contains("destination = destination with { SessionKey = sessionKey };", manager);
        Assert.Contains("usePendingChatSession ? _callbacks.GetPendingChatSessionKey() : null", manager);
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("if (!_navigation.Navigate(destination)", workspace);
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var legacy = chat[chat.IndexOf("private void ShowWebViewSurface", StringComparison.Ordinal)..];
        Assert.Contains("var pendingSessionKey = _pendingSessionKey ?? (App.Current as App)?.PendingChatSessionKey", legacy);
        Assert.DoesNotContain("_hub", chat);
        Assert.Contains("_pendingSessionKey = threadIdToMount", chat);
        Assert.Contains("_pendingVoice.Request(nativeSurface: !_webViewMode)", chat);
        Assert.Contains("_pendingVoice.Cancel()", chat);
        Assert.DoesNotContain("RetryTriggerVoice", chat);
    }

    [Fact]
    public void ComposerSelection_SynchronizesWorkspaceDestinationAndHistory()
    {
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var selected = chat[chat.IndexOf("internal void OnComposerSessionSelected", StringComparison.Ordinal)..
            chat.IndexOf("private void OnWorkspaceOpenConnection", StringComparison.Ordinal)];
        Assert.Contains("_mountedThreadId = sessionKey;", selected);
        Assert.Contains("SessionSelected?.Invoke(sessionKey, previousSessionKey)", selected);
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("_chat.SessionSelected += OnChatSessionSelected;", workspace);
        Assert.Contains("_navigation.SelectChatSession(sessionKey, previousSessionKey)", workspace);
    }

    [Fact]
    public void NavigationInvalidatesDelayedComposerSelectionBeforeDeferredRendering()
    {
        var workspace = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        var render = workspace[workspace.IndexOf("private void RenderDestination()", StringComparison.Ordinal)..
            workspace.IndexOf("private void QueueChatApply()", StringComparison.Ordinal)];
        Assert.True(render.IndexOf("_chat.InvalidatePendingSessionNavigation()", StringComparison.Ordinal) <
            render.IndexOf("_chat.QueueSession", StringComparison.Ordinal));
        var chat = File.ReadAllText(Source("Pages", "ChatPage.xaml.cs"));
        var unload = chat[chat.IndexOf("private void OnUnloaded", StringComparison.Ordinal)..
            chat.IndexOf("internal void Initialize(Window ownerWindow)", StringComparison.Ordinal)];
        Assert.Contains("InvalidatePendingSessionNavigation();", unload);
        Assert.Contains("_reactorHost?.InvalidatePendingSessionNavigation()", chat);
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
        var visible = Assert.Single(WorkspaceProjection.Sessions([session], "custom", new WorkspaceSessionOrder()));
        Assert.Equal("real-key", visible.Key);
        Assert.Empty(WorkspaceProjection.Sessions([session], "different", new WorkspaceSessionOrder()));
    }

    [Fact]
    public void Projection_UsesConfiguredIdentityAndResolvedAvatar_NotGatewayFilePaths()
    {
        using var json = JsonDocument.Parse("""
            {"defaultId":"main","agents":[
              {"id":"main","name":"Roster alias","identity":{"name":"Configured assistant","emoji":"C","avatar":"avatars/main.png","avatarUrl":"data:image/png;base64,AQID"}},
              {"id":"other","name":"Other assistant","identity":{"name":" ","avatar":"https://images.example/avatar.png"}},
              {"id":"plain","identity":42},
              {"id":" "}
            ]}
            """);
        var agents = WorkspaceProjection.Agents(json.RootElement, []);
        Assert.Equal(3, agents.Count);
        Assert.Equal("Roster alias", agents[0].Name);
        Assert.Equal("C", agents[0].Emoji);
        Assert.Equal("data:image/png;base64,AQID", agents[0].AvatarUrl);
        Assert.Equal("Other assistant", agents[1].Name);
        Assert.Equal("https://images.example/avatar.png", agents[1].AvatarUrl);
        Assert.Equal("plain", agents[2].Name);
        Assert.Null(agents[2].AvatarUrl);
        Assert.Equal("main", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, null));
        Assert.Equal("other", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, "other"));
        Assert.Equal("main", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, "removed"));
    }

    [Fact]
    public void Projection_FillsUnconfiguredDefaultAgentFromGatewayIdentity()
    {
        // agents.list omits the built-in default identity; agent.identity.get supplies "Assistant".
        using var json = JsonDocument.Parse("""
            {"defaultId":"main","agents":[
              {"id":"main"},
              {"id":"named","name":"Roster name","identity":{"avatarUrl":"https://images.example/stale.png"}},
              {"id":"plain"}
            ]}
            """);
        var identities = new Dictionary<string, ChatAgentIdentity>(StringComparer.Ordinal)
        {
            ["main"] = new("main", "Assistant", null, null),
            ["named"] = new("named", "Gateway name", null, "\U0001F916"),
            ["plain"] = new("other", "Wrong agent", "https://images.example/wrong.png", "W")
        };

        var agents = WorkspaceProjection.Agents(json.RootElement, [], identities);

        Assert.Equal(("Assistant", (string?)null, (string?)null), (agents[0].Name, agents[0].Emoji, agents[0].AvatarUrl));
        Assert.Equal("Roster name", agents[1].Name);
        Assert.Equal("\U0001F916", agents[1].Emoji);
        Assert.Null(agents[1].AvatarUrl);
        Assert.Equal(("plain", (string?)null, (string?)null), (agents[2].Name, agents[2].Emoji, agents[2].AvatarUrl));
        Assert.Equal("main", WorkspaceProjection.Agents(json.RootElement, [])[0].Name);
    }

    [Fact]
    public void Projection_RespectsExplicitAgentSelection_AndDoesNotGuessMain()
    {
        using var json = JsonDocument.Parse("""{"defaultId":"custom","selectionRequired":true,"agents":[{"id":"custom"},{"id":"other"}]}""");
        var agents = WorkspaceProjection.Agents(json.RootElement, []);
        Assert.Null(WorkspaceProjection.SelectedAgentId(json.RootElement, agents, null));
        Assert.Equal("other", WorkspaceProjection.SelectedAgentId(json.RootElement, agents, "other"));
        using var legacy = JsonDocument.Parse("""{"agents":[{"id":"custom"}]}""");
        Assert.Equal("custom", WorkspaceProjection.SelectedAgentId(legacy.RootElement, WorkspaceProjection.Agents(legacy.RootElement, []), null));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "removed", true)]
    [InlineData(true, "main", false)]
    [InlineData(true, "other", false)]
    [InlineData(false, null, false)]
    [InlineData(false, "removed", false)]
    public void SessionCreation_RequiresValidExplicitSelection(bool required, string? selectedId, bool expected)
    {
        using var json = JsonDocument.Parse(
            $$"""{"defaultId":"main","selectionRequired":{{required.ToString().ToLowerInvariant()}},"agents":[{"id":"main"},{"id":"other"}]}""");
        var agents = WorkspaceProjection.Agents(json.RootElement, []);
        Assert.Equal(expected, WorkspaceProjection.RequiresAgentSelection(json.RootElement, agents, selectedId));
    }

    [Theory]
    [InlineData("""{"agents":[{"id":"main"}]}""", false)]
    [InlineData("""{"agents":[]}""", false)]
    [InlineData("""{"selectionRequired":true,"agents":[]}""", true)]
    [InlineData("null", false)]
    public void SessionCreation_PreservesLegacyPolicyAndHandlesEmptyRoster(string payload, bool expected)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(expected, WorkspaceProjection.RequiresAgentSelection(
            json.RootElement, WorkspaceProjection.Agents(json.RootElement, []), null));
        Assert.False(WorkspaceProjection.RequiresAgentSelection(null, [], null));
    }

    [Theory]
    [InlineData("null", null, false)]
    [InlineData("""{"defaultId":"main"}""", "main", false)]
    [InlineData("""{"agents":null}""", "coordinator", false)]
    [InlineData("""{"agents":[]}""", null, true)]
    [InlineData("""{"selectionRequired":true,"agents":[{"id":"coordinator"}]}""", null, false)]
    [InlineData("""{"selectionRequired":true,"agents":[{"id":"coordinator"}]}""", "coordinator", true)]
    public void SessionCreation_RequiresAuthoritativeRosterAndReadySelection(
        string payload, string? selectedAgent, bool expected)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(expected, WorkspaceProjection.IsAgentSelectionReady(
            json.RootElement, WorkspaceProjection.Agents(json.RootElement, []), selectedAgent));
    }

    [Fact]
    public void OwnerStatusAndAgentBadge_UseNativeControlsAndLiveSources()
    {
        var document = XDocument.Load(Source("Controls", "WorkspacePaneFooter.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var accent in new[] { "Neutral", "Success", "Caution", "Critical" })
        {
            var style = document.Descendants().Single(element => (string?)element.Attribute(x + "Key") == $"ConnectionBadge{accent}");
            Assert.Equal($"{{ThemeResource SystemFillColor{accent}Brush}}",
                (string?)Assert.Single(style.Elements()).Attribute("Value"));
        }
        var badge = XDocument.Load(Source("Controls", "AgentIdentityBadge.xaml"));
        Assert.Single(badge.Descendants(), element => element.Name.LocalName == "PersonPicture");
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("ConnectionStatusPresenter.Pill(snapshot?.OverallState, status)", code);
        Assert.Contains("menu.Opening +=", code);
        Assert.Contains("_workspaceWindow?.UpdateConnectionStatus(snapshot, status)",
            File.ReadAllText(Source("Services", "WindowManager.cs")));
        var avatar = File.ReadAllText(Source("Controls", "AgentIdentityBadge.xaml.cs"));
        Assert.Contains("new MediaResolver(", avatar);
        Assert.Contains("cancellation.IsCancellationRequested", avatar);
        Assert.DoesNotContain("File.Read", avatar);
    }

    [Fact]
    public void Sidebar_ResolvesAgentFromSessionKeyWhenGatewayOmitsAgentId()
    {
        var sessions = WorkspaceProjection.Sessions([
            new SessionInfo { Key = "agent:main:main", IsMain = true },
            new SessionInfo { Key = "agent:main:fork", DisplayName = "Main session" },
            new SessionInfo { Key = "agent:research:main", IsMain = true },
            new SessionInfo { Key = "agent:main:subagent:worker", IsBackground = true }
        ], "main", new WorkspaceSessionOrder());
        var session = Assert.Single(sessions);
        Assert.Equal("agent:main:fork", session.Key);
        Assert.Equal("main", session.AgentId);
    }

    [Fact]
    public void Home_UsesSelectedAgentCanonicalIdentity_AndOnlyThatConversationLeavesSidebar()
    {
        var source = new[]
        {
            new SessionInfo { Key = "agent:main:canonical", IsMain = true, DisplayName = "Renamed" },
            new SessionInfo { Key = "agent:main:main", DisplayName = "Another conversation" },
            new SessionInfo { Key = "agent:main:fork", DisplayName = "Main session", Pinned = true },
            new SessionInfo { Key = "agent:research:main", DisplayName = "Renamed research" },
        };
        Assert.Equal("agent:main:canonical", WorkspaceProjection.HomeSessionKey(source, "main", "agent:main:canonical"));
        Assert.Equal("agent:main:canonical", WorkspaceProjection.HomeSessionKey(source, "main"));
        Assert.Equal("agent:main:canonical", WorkspaceProjection.HomeSessionKey(source, null));
        Assert.Null(WorkspaceProjection.HomeSessionKey([
            new SessionInfo { Key = "agent:main:main", IsMain = true },
            new SessionInfo { Key = "agent:research:main", IsMain = true },
        ], null));
        Assert.Equal("agent:research:main", WorkspaceProjection.HomeSessionKey(source, "research", "agent:main:canonical"));
        Assert.Equal("agent:new-agent:main", WorkspaceProjection.HomeSessionKey([], "new-agent"));
        Assert.Null(WorkspaceProjection.HomeSessionKey([], null));
        Assert.Equal(new[] { "agent:main:fork", "agent:main:main" },
            WorkspaceProjection.Sessions(source, "main", new WorkspaceSessionOrder(), "agent:main:canonical").Select(row => row.Key));
        Assert.Empty(WorkspaceProjection.Sessions(source, "research", new WorkspaceSessionOrder(), "agent:main:canonical"));
    }

    [Fact]
    public void Sidebar_PrefersExplicitAgentMetadataOverSessionKey()
    {
        var sessions = new[] { new SessionInfo { Key = "agent:research:main", AgentId = "main" } };
        Assert.Equal("main", Assert.Single(WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder())).AgentId);
        Assert.Empty(WorkspaceProjection.Sessions(sessions, "research", new WorkspaceSessionOrder()));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("completed")]
    public void Sidebar_RetainsConversationAfterRunCompletes_AndOnNextTurn(string completedStatus)
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        foreach (var (status, working) in new[] { ("running", true), (completedStatus, false), ("running", true) })
        {
            var conversation = new SessionInfo
            {
                Key = "agent:main:new-conversation",
                DisplayName = "My conversation",
                Status = status,
                HasActiveRun = working,
                UpdatedAt = DateTime.UtcNow
            };
            var sessions = new[]
            {
                conversation,
                new SessionInfo { Key = "agent:main:cron:job", IsBackground = true, Status = completedStatus },
                new SessionInfo { Key = "agent:other:conversation", Status = completedStatus }
            };
            var visible = Assert.Single(WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder()));
            Assert.Equal(conversation.Key, visible.Key);
            Assert.Equal("My conversation", visible.Title);
            Assert.Equal(conversation.Key, Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
            Assert.Equal(status, conversation.Status);
        }
    }

    [Fact]
    public void Workspace_UsesSeparateAgentTemplatesAndSidebarSessionNavigation()
    {
        var document = XDocument.Load(Source("Windows", "WorkspaceWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var template = document.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "AgentIdentityTemplate");
        Assert.Equal("DataTemplate", template.Name.LocalName);
        Assert.Equal("AgentIdentityBadge", Assert.Single(template.Elements()).Name.LocalName);
        var code = File.ReadAllText(Source("Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("item.Content = agent", code);
        Assert.DoesNotContain("Content = new AgentIdentityBadge", code);
        Assert.DoesNotContain("ChatComposerSessionPicker", File.ReadAllText(Source("Chat", "ReactorChatComposer.cs")));
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
        Assert.Equal("visible", Assert.Single(WorkspaceProjection.Sessions(sessions, "custom", new WorkspaceSessionOrder())).Key);
    }

    [Theory]
    [InlineData("agent:main:cron:job", null)]
    [InlineData("agent:main:subagent:worker", null)]
    [InlineData("agent:main:acp:worker", null)]
    [InlineData("agent:main:tui-one:heartbeat", null)]
    [InlineData("agent:main:hook:run", null)]
    [InlineData("agent:main:harness:run", null)]
    [InlineData("agent:main:dreaming-narrative-one", null)]
    [InlineData("agent:main:boot", null)]
    [InlineData("agent:main:opaque", "cron")]
    [InlineData("agent:main:opaque", "subagent")]
    [InlineData("agent:main:opaque", "system")]
    public void Projection_OmittedBackgroundFlagUsesCanonicalFallbackAndCannotBecomeLatest(string key, string? classification)
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        var conversation = new SessionInfo { Key = "agent:main:conversation", UpdatedAt = new DateTime(2026, 1, 1) };
        var background = new SessionInfo
        {
            Key = key, Classification = classification, UpdatedAt = conversation.UpdatedAt!.Value.AddHours(1)
        };
        Assert.Null(background.IsBackground);
        var sessions = new[] { conversation, background };
        Assert.Equal(conversation.Key, Assert.Single(WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder())).Key);
        Assert.Equal(conversation.Key, Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
    }

    [Theory]
    [InlineData("agent:main:cron:job", null, false)]
    [InlineData("agent:main:opaque", "subagent", false)]
    [InlineData("agent:main:cron:job", "direct", null)]
    [InlineData("agent:main:main", null, null)]
    [InlineData("agent:main:dashboard:chat", null, null)]
    [InlineData("agent:main:voice:call", null, null)]
    [InlineData("agent:main:telegram:direct:peer", null, null)]
    public void Projection_PreservesForegroundMetadataAndExplicitFalseOverride(string key, string? classification, bool? background)
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:older", UpdatedAt = new DateTime(2026, 1, 1) },
            new SessionInfo { Key = key, Classification = classification, IsBackground = background, UpdatedAt = new DateTime(2026, 1, 2) }
        };
        var expected = key == "agent:main:main" ? new[] { "agent:main:older" } : new[] { key, "agent:main:older" };
        Assert.Equal(expected, WorkspaceProjection.Sessions(sessions, "main", new WorkspaceSessionOrder()).Select(s => s.Key));
        Assert.Equal(key, Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
    }

    [Fact]
    public void Projection_PinnedSessionsSortFirstAndArchivedRowsHidden()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:unpinned-newer", UpdatedAt = new DateTime(2026, 3, 1) },
            new SessionInfo { Key = "agent:main:pinned-older", UpdatedAt = new DateTime(2026, 1, 1), Pinned = true, PinnedAt = 100 },
            new SessionInfo { Key = "agent:main:pinned-newer", UpdatedAt = new DateTime(2026, 2, 1), Pinned = true, PinnedAt = 200 },
            new SessionInfo { Key = "agent:main:unpinned-older", UpdatedAt = new DateTime(2026, 2, 15) },
            new SessionInfo { Key = "agent:main:archived", UpdatedAt = new DateTime(2026, 4, 1), Archived = true },
        };

        var visible = WorkspaceProjection.Sessions(sessions, null, new WorkspaceSessionOrder());

        Assert.Equal(
            new[] { "agent:main:pinned-newer", "agent:main:pinned-older", "agent:main:unpinned-newer", "agent:main:unpinned-older" },
            visible.Select(s => s.Key));
        Assert.True(visible[0].IsPinned);
        Assert.False(visible[2].IsPinned);
        Assert.DoesNotContain(visible, s => s.IsArchived);
    }

    [Fact]
    public void Projection_ActivityDoesNotReorderSessions()
    {
        var order = new WorkspaceSessionOrder();
        var a = new SessionInfo { Key = "agent:main:a", CreatedAt = 100, UpdatedAt = new DateTime(2026, 1, 1) };
        var b = new SessionInfo { Key = "agent:main:b", CreatedAt = 200, UpdatedAt = new DateTime(2026, 1, 2) };

        Assert.Equal(
            new[] { "agent:main:b", "agent:main:a" },
            WorkspaceProjection.Sessions([a, b], null, order).Select(s => s.Key));

        var bumped = a.Clone();
        bumped.UpdatedAt = new DateTime(2026, 3, 1);
        Assert.Equal(
            new[] { "agent:main:b", "agent:main:a" },
            WorkspaceProjection.Sessions([b, bumped], null, order).Select(s => s.Key));

        var c = new SessionInfo { Key = "agent:main:c", CreatedAt = 300 };
        Assert.Equal(
            new[] { "agent:main:c", "agent:main:b", "agent:main:a" },
            WorkspaceProjection.Sessions([bumped, b, c], null, order).Select(s => s.Key));
    }

    [Fact]
    public void Projection_SessionsWithoutCreatedAtFreezeFirstSeenOrder()
    {
        var order = new WorkspaceSessionOrder();
        var l1 = new SessionInfo { Key = "agent:main:l1", UpdatedAt = new DateTime(2026, 1, 1) };
        var l2 = new SessionInfo { Key = "agent:main:l2", UpdatedAt = new DateTime(2026, 1, 2) };

        Assert.Equal(
            new[] { "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([l1, l2], null, order).Select(s => s.Key));

        var bumped = l1.Clone();
        bumped.UpdatedAt = new DateTime(2026, 3, 1);
        Assert.Equal(
            new[] { "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([l2, bumped], null, order).Select(s => s.Key));

        var l3 = new SessionInfo { Key = "agent:main:l3", UpdatedAt = new DateTime(2026, 2, 1) };
        Assert.Equal(
            new[] { "agent:main:l3", "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([bumped, l2, l3], null, order).Select(s => s.Key));

        var n = new SessionInfo { Key = "agent:main:n", CreatedAt = 1 };
        Assert.Equal(
            new[] { "agent:main:n", "agent:main:l3", "agent:main:l2", "agent:main:l1" },
            WorkspaceProjection.Sessions([bumped, l2, l3, n], null, order).Select(s => s.Key));
    }

    [Fact]
    public void Projection_LatestSessionKeyIgnoresPinnedOrderingAndArchivedRows()
    {
        using var agents = JsonDocument.Parse("""{"agents":[{"id":"main"}]}""");
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:pinned-but-old", UpdatedAt = new DateTime(2026, 1, 1), Pinned = true, PinnedAt = 900 },
            new SessionInfo { Key = "agent:main:latest", UpdatedAt = new DateTime(2026, 5, 1) },
            new SessionInfo { Key = "agent:main:archived-newest", UpdatedAt = new DateTime(2026, 6, 1), Archived = true },
        };

        Assert.Equal("agent:main:latest", Assert.Single(WorkspaceProjection.Agents(agents.RootElement, sessions)).LatestSessionKey);
    }

    [Fact]
    public void Projection_ArchivedSessionsAreNotSidebarDestinations()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:archived", UpdatedAt = new DateTime(2026, 3, 1), Archived = true, Unread = true, Pinned = true },
        };

        Assert.Empty(WorkspaceProjection.Sessions(sessions, null, new WorkspaceSessionOrder()));
    }

    [Fact]
    public void Projection_WorkingStateFollowsActiveRun()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:live", HasActiveRun = true, Status = "done", UpdatedAt = new DateTime(2026, 3, 1) },
            new SessionInfo { Key = "agent:main:legacy", Status = "running", UpdatedAt = new DateTime(2026, 2, 1) },
            new SessionInfo { Key = "agent:main:stale", HasActiveRun = false, Status = "running", UpdatedAt = new DateTime(2026, 1, 1) },
        };

        var rows = WorkspaceProjection.Sessions(sessions, null, new WorkspaceSessionOrder()).ToDictionary(row => row.Key);

        Assert.True(rows["agent:main:live"].IsWorking);
        Assert.True(rows["agent:main:legacy"].IsWorking);
        Assert.False(rows["agent:main:stale"].IsWorking);

    }

    [Fact]
    public void ItemsSync_ArrangeReordersInPlaceAndPreservesIdentity()
    {
        object home = new(), header = new(), empty = new(), a = new(), b = new(), c = new(),
            archivedHeader = new(), newItem = new();
        var items = new List<object> { home, header, empty, a, b, c, archivedHeader };
        items.Remove(b);

        WorkspaceItemsSync.Arrange(items, 3, [c, newItem, a]);

        Assert.Equal(new[] { home, header, empty, c, newItem, a, archivedHeader }, items);
        Assert.Same(c, items[3]);
        Assert.Same(a, items[5]);
        Assert.Same(home, items[0]);
        Assert.Same(archivedHeader, items[6]);
    }

    private static string Source(string folder, string file) =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", folder, file);
}
