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
    public async Task SessionsHub_MutationsAndDeletionRoundTrip()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSessionsHub(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: HubActiveSessionCount);
        var client = connected.Client;
        const string key = "agent:main:hub-task-01";

        await client.PatchSessionConfirmedAsync(key, new SessionPatch { Category = "Customers", ReasoningLevel = "stream" },
            client.SessionMutationConnectionEpoch!.Value).WaitAsync(Deadline);
        var row = (await client.ListSessionsPageAsync(new SessionListQuery { Search = "Backlog task 01" })).Sessions.Single();
        Assert.Equal("Customers", row.Category);
        Assert.Equal("stream", row.ReasoningLevel);
        await client.PatchSessionConfirmedAsync(key, new SessionPatch { Category = SessionPatch.Clear },
            client.SessionMutationConnectionEpoch!.Value);
        Assert.Null((await client.ListSessionsPageAsync(new SessionListQuery { Search = "Backlog task 01" })).Sessions.Single().Category);

        var archived = (await client.ListSessionsPageAsync(new SessionListQuery { Archive = SessionArchiveFilter.Archived, Limit = 100 })).Sessions;
        var target = Assert.Single(archived, item => item.Key == GatewayScenario.HubPreservedSessionKey);
        var preserved = await client.DeleteSessionDetailedAsync(new SessionDeleteRequest(target.Key)
        {
            AgentId = "main",
            ExpectedSessionId = target.SessionId,
            ArchivedOnly = true,
        });
        Assert.True(preserved.Deleted);
        Assert.Equal(GatewayScenario.HubPreservedSessionKey, preserved.Key);
        Assert.Equal("busy", preserved.PreservedWorktree!.Reason);
        var remaining = (await client.ListSessionsPageAsync(new SessionListQuery { Archive = SessionArchiveFilter.Archived })).Sessions;
        Assert.Equal(archived.Where(item => item.Key != target.Key).Select(item => item.Key),
            remaining.Select(item => item.Key));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteSessionDetailedAsync(
            new SessionDeleteRequest(key) { ArchivedOnly = true }));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteSessionDetailedAsync(
            new SessionDeleteRequest("agent:main:hub-task-01")));
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
