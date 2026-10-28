using OpenClaw.Shared.Sessions;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    // Five browse rows plus 25 active hub rows; global, unknown and archived rows are not in the default roster.
    private const int HubActiveSessionCount = 30;

    [Fact]
    public async Task SessionsHub_ListScopesFiltersAndPagesThroughTheRealClient()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSessionsHub(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var client = connected.Client;

        var first = await client.ListSessionsPageAsync(new SessionListQuery { Limit = 10 }).WaitAsync(Deadline);
        Assert.Equal(10, first.Sessions.Count);
        Assert.True(first.HasMore);
        Assert.Equal(10, first.NextOffset);
        Assert.DoesNotContain(first.Sessions, row => row.Archived);
        var second = await client.ListSessionsPageAsync(new SessionListQuery { Limit = 10, Offset = 10 }).WaitAsync(Deadline);
        Assert.Empty(first.Sessions.Select(r => r.Key).Intersect(second.Sessions.Select(r => r.Key)));

        var research = await client.ListSessionsPageAsync(new SessionListQuery { AgentId = "research", Limit = 100 });
        Assert.All(research.Sessions, row => Assert.StartsWith("agent:research:", row.Key));

        var archived = await client.ListSessionsPageAsync(new SessionListQuery { Archive = SessionArchiveFilter.Archived, Limit = 100 });
        Assert.NotEmpty(archived.Sessions);
        Assert.All(archived.Sessions, row => Assert.True(row.Archived));

        var withoutUnknown = await client.ListSessionsPageAsync(new SessionListQuery { Limit = 100 });
        Assert.DoesNotContain(withoutUnknown.Sessions, row => row.Kind == "unknown");
        Assert.Contains(withoutUnknown.Sessions, row => row.Kind == "global");
        var withUnknown = await client.ListSessionsPageAsync(new SessionListQuery { Limit = 100, IncludeUnknown = true, IncludeGlobal = false });
        Assert.Contains(withUnknown.Sessions, row => row.Kind == "unknown");
        Assert.DoesNotContain(withUnknown.Sessions, row => row.Kind == "global");

        var search = await client.ListSessionsPageAsync(new SessionListQuery { Search = "release notes", Limit = 100 });
        var release = Assert.Single(search.Sessions);
        Assert.Equal("Release", release.Category);
        Assert.Equal(new SessionOwnerInfo("human", "fixture-owner", "Fixture Owner"), release.Owner);

        var recent = await client.ListSessionsPageAsync(new SessionListQuery { ActiveMinutes = 60, Limit = 100 });
        Assert.InRange(recent.Sessions.Count, 1, 6);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task SessionsHub_TranscriptSearchReportsExclusionsTruncationAndIndexing()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSessionsHub(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var client = connected.Client;

        var spike = await client.SearchSessionTranscriptsAsync("offline cache", new SessionListQuery()).WaitAsync(Deadline);
        Assert.Empty(spike.Hits);
        Assert.Equal(1, spike.ArchivedTranscriptsExcluded);

        var all = await client.SearchSessionTranscriptsAsync("offline cache", new SessionListQuery { Archive = SessionArchiveFilter.All });
        Assert.Equal("agent:main:hub-archived-spike", all.Hits[0].SessionKey);
        Assert.Contains(all.Sessions, row => row.Key == "agent:main:hub-archived-spike");

        var many = await client.SearchSessionTranscriptsAsync("backlog task", new SessionListQuery());
        Assert.Equal(25, many.Hits.Count);
        Assert.True(many.Truncated);

        Assert.True((await client.SearchSessionTranscriptsAsync(GatewayScenario.HubIndexingQuery, new SessionListQuery())).Indexing);
    }

    [Fact]
    public async Task SessionsHub_MutationsGroupsOwnersAndDeletionRoundTrip()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSessionsHub(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var client = connected.Client;
        const string key = "agent:main:hub-task-01";

        var groups = await client.PutSessionGroupsAsync(["Release", "Research", "Ops", "Customers"]).WaitAsync(Deadline);
        Assert.Equal("Customers", groups.Names[^1]);
        await client.PatchSessionConfirmedAsync(key, new SessionPatch { Category = "Customers", ReasoningLevel = "stream" },
            client.SessionMutationConnectionEpoch!.Value);
        var renamed = await client.RenameSessionGroupAsync("Customers", "Clients");
        Assert.Contains("Clients", renamed.Names);
        var row = (await client.ListSessionsPageAsync(new SessionListQuery { Search = "Backlog task 01" })).Sessions.Single();
        Assert.Equal("Clients", row.Category);
        Assert.Equal("stream", row.ReasoningLevel);
        await client.DeleteSessionGroupAsync("Clients");
        Assert.Null((await client.ListSessionsPageAsync(new SessionListQuery { Search = "Backlog task 01" })).Sessions.Single().Category);

        var profiles = await client.ListUserProfilesAsync();
        Assert.Contains(profiles, profile => profile.Id == "profile-grace");
        var owner = await client.AssignSessionOwnerAsync(key, "main", "human", "profile-grace");
        Assert.Equal(new SessionOwnerInfo("human", "profile-grace", "Grace Hopper"), owner);

        await client.ReclaimSessionAsync(GatewayScenario.HubCloudSessionKey, "research");
        var cloud = (await client.ListSessionsPageAsync(new SessionListQuery { Limit = 100 })).Sessions
            .Single(session => session.Key == GatewayScenario.HubCloudSessionKey);
        Assert.False(SessionsHubProjection.CanStopCloudWorker(cloud));

        var archived = (await client.ListSessionsPageAsync(new SessionListQuery { Archive = SessionArchiveFilter.Archived, Limit = 100 })).Sessions;
        var admin = new SessionsHubPermissions(true, client.GrantedOperatorScopes, client.AdvertisedServerMethods);
        var plan = SessionsHubDeletion.PlanDeleteAllArchived(archived, admin, _ => false);
        var outcomes = new List<SessionDeleteOutcome>();
        foreach (var request in plan.Requests)
            outcomes.Add(await client.DeleteSessionDetailedAsync(request));
        Assert.All(outcomes, outcome => Assert.True(outcome.Deleted));
        var preserved = Assert.Single(outcomes, outcome => outcome.PreservedWorktree is not null);
        Assert.Equal(GatewayScenario.HubPreservedSessionKey, preserved.Key);
        Assert.Equal("busy", preserved.PreservedWorktree!.Reason);
        Assert.Empty((await client.ListSessionsPageAsync(new SessionListQuery { Archive = SessionArchiveFilter.Archived })).Sessions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteSessionDetailedAsync(
            new SessionDeleteRequest(key) { ArchivedOnly = true }));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task SessionsHub_AdditiveCreationPreservesOtherClientsGroupsAndIsIdempotent()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSessionsHub(), token);
        await using var first = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        await using var second = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var before = await first.Client.ListSessionGroupsAsync();

        await second.Client.CreateSessionGroupAsync("Other client's group");
        await Task.WhenAll(
            first.Client.CreateSessionGroupAsync("Mine"),
            second.Client.CreateSessionGroupAsync("Theirs"),
            first.Client.CreateSessionGroupAsync("Mine"));
        var after = await first.Client.ListSessionGroupsAsync();

        Assert.Equal(before.Names, after.Names.Take(before.Names.Count));
        Assert.Contains("Other client's group", after.Names);
        Assert.Contains("Mine", after.Names);
        Assert.Contains("Theirs", after.Names);
        Assert.Equal(before.Names.Count + 3, after.Names.Count);

        var unicodeName = string.Concat(Enumerable.Repeat("\U0001F680", 512));
        Assert.Contains(unicodeName, (await first.Client.CreateSessionGroupAsync(unicodeName)).Names);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Client.CreateSessionGroupAsync(unicodeName + "\U0001F680"));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task SessionsHub_WorktreesCreateRemoveForceRestoreAndClean()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSessionsHub(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var client = connected.Client;

        var worktrees = await client.ListWorktreesAsync().WaitAsync(Deadline);
        Assert.NotNull(worktrees);
        Assert.Contains(worktrees, w => w.OwnerKind == "workboard");
        Assert.Contains(worktrees, w => w.IsRemoved);

        var branches = await client.ListWorktreeBranchesAsync("C:/src/contoso-app");
        Assert.Equal("main", branches.DefaultBranch);
        var created = await client.CreateWorktreeAsync("C:/src/contoso-app", "docs-refresh", "release/3.2");
        Assert.Equal("release/3.2", created!.BaseRef);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateWorktreeAsync("C:/src/contoso-app", "Bad Name", null));

        var refused = await client.RemoveWorktreeAsync(GatewayScenario.HubBusyWorktreeId, force: false);
        Assert.False(refused.Removed);
        Assert.NotNull(refused.SnapshotError);
        Assert.True((await client.RemoveWorktreeAsync(GatewayScenario.HubBusyWorktreeId, force: true)).Removed);
        var restored = await client.RestoreWorktreeAsync(GatewayScenario.HubBusyWorktreeId);
        Assert.False(restored!.IsRemoved);

        var gc = await client.RunWorktreeGcAsync();
        Assert.True(gc.IsPending);
        while (gc.IsPending)
            gc = await client.RunWorktreeGcAsync(gc.JobId);
        Assert.Equal("completed", gc.State);
        Assert.NotEmpty(gc.IssueReasons);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task SessionsHub_ReadOnlyScenarioGrantsReadScopeAndRejectsMutations()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSessionsHub(readOnly: true), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var client = connected.Client;

        var permissions = new SessionsHubPermissions(true, client.GrantedOperatorScopes, client.AdvertisedServerMethods);
        Assert.False(permissions.CanWrite);
        Assert.False(permissions.CanManageWorktrees);
        Assert.NotEmpty((await client.ListSessionsPageAsync(new SessionListQuery()).WaitAsync(Deadline)).Sessions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PutSessionGroupsAsync(["X"]));
    }

    [Fact]
    public async Task SessionsHub_ListFallsBackWhenAnOlderGatewayRejectsPageSourceParams()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateSessionsHub(legacySessionsList: true), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var client = connected.Client;

        var first = await client.ListSessionsPageAsync(new SessionListQuery { Limit = 10 }).WaitAsync(Deadline);
        Assert.Equal(10, first.Sessions.Count);
        var archived = await client.ListSessionsPageAsync(
            new SessionListQuery { Archive = SessionArchiveFilter.Archived, Limit = 100 }).WaitAsync(Deadline);
        Assert.NotEmpty(archived.Sessions);
        Assert.All(archived.Sessions, row => Assert.True(row.Archived));

        // One rejected probe, then the client stops sending the newer fields.
        var listRequests = server.Requests.Where(request => request.Method == "sessions.list").ToArray();
        Assert.Single(listRequests, request => request.Outcome.StartsWith("error:", StringComparison.Ordinal));
        Assert.Empty(server.UnexpectedRequests);
    }}
