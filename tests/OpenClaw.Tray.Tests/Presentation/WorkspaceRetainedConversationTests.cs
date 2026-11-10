using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceRetainedConversationTests
{
    private const string Key = "agent:research:dashboard:loaded";
    private static SessionInfo Loaded() => new()
    {
        Key = Key, SessionId = "immutable-loaded-session", AgentId = "research",
        DisplayName = "Loaded research", Classification = "dashboard",
    };

    [Fact]
    public void CacheClearKeepsOnlyConfirmedLoadedNavigationWithoutRestoringRosterAccess()
    {
        using var roster = JsonDocument.Parse("""{"agents":[{"id":"research"}]}""");
        var loaded = Loaded();
        var state = new AppState { Status = ConnectionStatus.Connected, AgentsList = roster.RootElement,
            Sessions = [loaded, new SessionInfo { Key = "agent:research:dashboard:unloaded", AgentId = "research" }] };
        var selection = new WorkspaceAgentSelection();
        selection.Select("research");
        void Refresh(bool rosterUpdated = false) => selection.Refresh("gateway", state.AgentsList,
            WorkspaceProjection.Agents(state.AgentsList, state.Sessions), rosterUpdated);
        Refresh(true);
        state.PropertyChanged += (_, _) => Refresh();
        Assert.Equal(2, WorkspaceProjection.Sessions(state.Sessions, selection.AgentId, new()).Count);
        state.Status = ConnectionStatus.Disconnected;
        state.ClearCachedData();

        Assert.Empty(state.Sessions);
        Assert.Null(state.AgentsList);
        Assert.False(selection.HasSessionAccess);
        Assert.False(selection.IsReady);
        var row = Assert.Single(WorkspaceProjection.Sessions(state.Sessions, selection.AgentId, new(),
            hasSessionAccess: selection.HasSessionAccess, retainedSession: loaded));
        Assert.Equal(Key, row.Key);
        Assert.Equal("Loaded research", row.Title);
        Assert.Equal("research", selection.AgentId);
    }

    [Fact]
    public void MissingRetainedAuthorityCannotExposeAnUnverifiedRoster()
    {
        Assert.Empty(WorkspaceProjection.Sessions([Loaded()], "research", new(),
            hasSessionAccess: false, retainedSession: null));
    }

    [Fact]
    public void CurrentAuthoritativeEmptyRosterDoesNotResurrectRetainedNavigation()
    {
        Assert.Empty(WorkspaceProjection.Sessions([], "research", new(),
            hasSessionAccess: true, retainedSession: Loaded()));
    }

    [Fact]
    public void CurrentRosterOverridesRetainedMetadataAndDifferentAssistantCannotSeeIt()
    {
        var current = Loaded();
        current.DisplayName = "Current authority title";
        var row = Assert.Single(WorkspaceProjection.Sessions([current], "research", new(),
            hasSessionAccess: true, retainedSession: Loaded()));
        Assert.Equal("Current authority title", row.Title);
        Assert.Empty(WorkspaceProjection.Sessions([], "other-assistant", new(),
            hasSessionAccess: false, retainedSession: Loaded()));
    }
}
