using System.Text.Json;
using OpenClaw.Shared.Sessions;

namespace OpenClaw.Shared.Tests;

public sealed class SessionsHubTests
{
    private static OpenClawGatewayClient NewClient() =>
        new("ws://localhost:18789", "test-token", new TestLogger());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── Query scoping ──

    [Fact]
    public void Query_ActiveViewSendsRecencyScopeAndNoArchiveFlag()
    {
        var parameters = new SessionListQuery
        {
            AgentId = " research ", Search = "  deploy ", ActiveMinutes = 30, IncludeUnknown = true, Limit = 25, Offset = 50,
        }.ToParameters();

        Assert.Equal("research", parameters["agentId"]);
        Assert.Equal("deploy", parameters["search"]);
        Assert.Equal(30, parameters["activeMinutes"]);
        Assert.Equal(true, parameters["includeGlobal"]);
        Assert.Equal(true, parameters["includeUnknown"]);
        Assert.Equal(25, parameters["limit"]);
        Assert.Equal(50, parameters["offset"]);
        Assert.Equal("sessions-page", parameters["source"]);
        Assert.False(parameters.ContainsKey("archived"));
    }

    [Theory]
    [InlineData(SessionArchiveFilter.Archived, true)]
    [InlineData(SessionArchiveFilter.All, "all")]
    public void Query_ArchivedAndAllViewsIgnoreRecencyAndSetArchiveMembership(SessionArchiveFilter filter, object expected)
    {
        var parameters = new SessionListQuery { Archive = filter, ActiveMinutes = 30 }.ToParameters();

        Assert.Equal(expected, parameters["archived"]);
        Assert.False(parameters.ContainsKey("activeMinutes"));
    }

    [Fact]
    public void SearchScope_ReusesMembershipFiltersWithoutPagination()
    {
        var scope = new SessionListQuery { AgentId = "main", Search = "x", Limit = 10, Offset = 20, Archive = SessionArchiveFilter.All }
            .ToSearchScope();

        Assert.Equal("main", scope["agentId"]);
        Assert.Equal("all", scope["archived"]);
        Assert.False(scope.ContainsKey("limit"));
        Assert.False(scope.ContainsKey("offset"));
        Assert.False(scope.ContainsKey("source"));
        Assert.False(scope.ContainsKey("excludeDock"));
        // Transcript search hides the same dock sessions the roster hides.
        Assert.Equal(true, new SessionListQuery().ToSearchScope(excludeDock: true)["excludeDock"]);
    }

    [Theory]
    [InlineData("15", 15)]
    [InlineData(" 7 ", 7)]
    [InlineData("0", null)]
    [InlineData("-3", null)]
    [InlineData("2.5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ParsePositiveInteger_AcceptsOnlyStrictPositiveIntegers(string input, int? expected) =>
        Assert.Equal(expected, SessionListQuery.ParsePositiveInteger(input));

    // ── Parsing ──

    [Fact]
    public void ParseSessionListPage_ReadsPagingAndHubRowFields()
    {
        using var client = NewClient();
        var page = client.ParseSessionListPage(Json("""
            {"totalCount":120,"hasMore":true,"nextOffset":50,"sessions":[{
              "key":"agent:main:fixture","sessionId":"s1","kind":"direct","category":" Release ",
              "reasoningLevel":"stream","fastMode":true,"thinkingLevel":"high","verboseLevel":"full",
              "owner":{"actor":{"type":"human","id":"p1","label":"Ada"}},
              "placement":{"state":"active","generation":3},
              "archived":true,"archivedAt":1700000000000,"archiveReason":"manual","lastRunError":"boom",
              "color":"purple","icon":"bot"
            }]}
            """));

        Assert.Equal(120, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal(50, page.NextOffset);
        var row = Assert.Single(page.Sessions);
        Assert.Equal("direct", row.Kind);
        Assert.Equal("Release", row.Category);
        Assert.Equal("stream", row.ReasoningLevel);
        Assert.Equal("on", row.FastMode);
        Assert.Equal("full", row.VerboseLevel);
        Assert.Equal(new SessionOwnerInfo("human", "p1", "Ada"), row.Owner);
        Assert.Equal("active", row.PlacementState);
        Assert.True(row.Archived);
        Assert.Equal(1700000000000, row.ArchivedAt);
        Assert.Equal("manual", row.ArchiveReason);
        Assert.Equal("boom", row.LastRunError);
        Assert.Equal("purple", row.Color);
        Assert.Equal("bot", row.Icon);
    }

    [Theory]
    [InlineData("false", "off")]
    [InlineData("\"auto\"", "auto")]
    [InlineData("null", null)]
    public void ReadFastMode_MapsWireUnion(string json, string? expected) =>
        Assert.Equal(expected, OpenClawGatewayClient.ReadFastMode(Json(json)));

    [Fact]
    public void ParseSessionOwner_UsesActorIdAndFallsBackOnlyToMatchingIdentity()
    {
        // actor.id drives ownership policy; the display identity must not replace it.
        Assert.Equal(new SessionOwnerInfo("human", "profile-1", "Grace"), OpenClawGatewayClient.ParseSessionOwner(Json("""
            {"actor":{"type":"human","id":"profile-1","label":"Grace","identity":{"type":"profile","id":"profile-9"}}}
            """)));
        Assert.Equal(new SessionOwnerInfo("human", "profile-9", null), OpenClawGatewayClient.ParseSessionOwner(Json("""
            {"actor":{"type":"human","identity":{"type":"profile","id":"profile-9"}}}
            """)));
        Assert.Equal(new SessionOwnerInfo("human", null, null), OpenClawGatewayClient.ParseSessionOwner(Json("""
            {"actor":{"type":"human","identity":{"type":"remote","pluginId":"p","domain":"d","idKind":"k","id":"r-1"}}}
            """)));
        Assert.Null(OpenClawGatewayClient.ParseSessionOwner(Json("""{"actor":{}}""")));
    }

    [Fact]
    public void ParseSessionSearchResult_ReadsHitsAndStateFlags()
    {
        using var client = NewClient();
        var result = client.ParseSessionSearchResult(Json("""
            {"results":[
               {"sessionKey":"agent:main:a","sessionId":"s","messageId":"m","role":"user","timestamp":5,"snippet":"hello <b>world</b>","score":1.5},
               {"sessionKey":"","snippet":"dropped"}],
             "indexing":true,"truncated":true,"archivedTranscriptsExcluded":3,
             "sessions":[{"key":"agent:main:a","label":"Alpha"}]}
            """));

        var hit = Assert.Single(result.Hits);
        Assert.Equal("agent:main:a", hit.SessionKey);
        Assert.Equal("user", hit.Role);
        Assert.True(result.Indexing);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.ArchivedTranscriptsExcluded);
        Assert.Equal("Alpha", Assert.Single(result.Sessions).Label);
    }

    [Fact]
    public void ParseSessionGroupCatalog_OrdersByPositionAndDropsBlanks()
    {
        var catalog = OpenClawGatewayClient.ParseSessionGroupCatalog(Json("""
            {"groups":[{"name":"Later","position":2},{"name":"First","position":0},{"name":" ","position":1}]}
            """));

        Assert.Equal(["First", "Later"], catalog.Names);
    }

    [Fact]
    public void ParseSessionDeleteOutcome_ReadsPreservedWorktreeAndRejectsNotOk()
    {
        var outcome = OpenClawGatewayClient.ParseSessionDeleteOutcome(Json("""
            {"ok":true,"key":"k","deleted":true,"archived":[],"worktreePreserved":{"id":"w","branch":"b","path":"p","reason":"busy"}}
            """), "k");

        Assert.True(outcome.Deleted);
        Assert.Equal(new PreservedWorktreeInfo("w", "b", "p", "busy"), outcome.PreservedWorktree);
        Assert.Throws<InvalidOperationException>(() =>
            OpenClawGatewayClient.ParseSessionDeleteOutcome(Json("""{"ok":false,"reason":"nope"}"""), "k"));
    }

    [Fact]
    public void ParseUserProfiles_SkipsMergedProfilesAndUsesEmailFallback()
    {
        var profiles = OpenClawGatewayClient.ParseUserProfiles(Json("""
            {"profiles":[
              {"id":"a","displayName":null,"emails":["a@example.test"],"mergedInto":null},
              {"id":"b","displayName":"Bea","emails":[],"mergedInto":"a"}]}
            """));

        var profile = Assert.Single(profiles);
        Assert.Equal("a@example.test", profile.Label);
    }

    [Fact]
    public void ParseWorktrees_AndBranchesAndGc()
    {
        var worktree = Assert.Single(OpenClawGatewayClient.ParseWorktrees(Json("""
            {"worktrees":[{"id":"w1","name":"fix-a","repoRoot":"C:/src/app","path":"C:/wt/fix-a","branch":"openclaw/fix-a",
              "baseRef":"main","ownerKind":"session","ownerId":"agent:main:x","createdAt":1,"lastActiveAt":2,"removedAt":3}]}
            """)));
        Assert.True(worktree.IsRemoved);
        Assert.Equal("session", worktree.OwnerKind);
        Assert.Equal("app", WorktreesProjection.RepoName(worktree.RepoRoot));

        var branches = OpenClawGatewayClient.ParseWorktreeBranches(Json("""
            {"branches":[{"name":"main","kind":"local"},{"name":"origin/dev","kind":"remote"}],"defaultBranch":"main","repositoryStatus":"git"}
            """));
        Assert.Equal(2, branches.Branches.Count);
        Assert.Equal("main", branches.DefaultBranch);

        var gc = OpenClawGatewayClient.ParseWorktreeGc(Json("""
            {"jobId":"j","state":"running","removed":["w1"],"orphansDeleted":1,"snapshotsPruned":2,
             "issues":[{"stage":"idle","outcome":"failed","reason":"locked"}]}
            """));
        Assert.True(gc.IsPending);
        Assert.Equal(["w1"], gc.Removed);
        Assert.Equal(["locked"], gc.IssueReasons);
    }

    // ── Grouping and sorting ──

    private static SessionInfo Row(string key, string? category = null, DateTime? updated = null, bool pinned = false,
        long? pinnedAt = null, SessionOwnerInfo? owner = null, string? channel = null, string? kind = null, bool archived = false) =>
        new()
        {
            Key = key, Label = key, Category = category, UpdatedAt = updated, Pinned = pinned, PinnedAt = pinnedAt,
            Owner = owner, Channel = channel, Kind = kind, Archived = archived, SessionId = $"id-{key}",
        };

    [Fact]
    public void Group_CategoryKeepsCatalogOrderEmptyGroupsAndTrailingUngrouped()
    {
        var rows = new[] { Row("a", "Zeta"), Row("b"), Row("c", "Ops") };

        var groups = SessionsHubProjection.Group(rows, SessionsGroupBy.Category, ["Ops", "Empty"]);

        Assert.Equal(["Ops", "Empty", "Zeta", ""], groups.Select(group => group.Id));
        Assert.Empty(groups[1].Rows);
        Assert.Equal("b", Assert.Single(groups[^1].Rows).Key);
    }

    [Fact]
    public void Group_DateUsesLocalCalendarBuckets()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Local);
        DateTime Utc(DateTime local) => local.ToUniversalTime();
        var rows = new[]
        {
            Row("today", updated: Utc(now.Date.AddHours(1))),
            Row("yesterday", updated: Utc(now.Date.AddHours(-1))),
            Row("week", updated: Utc(now.Date.AddDays(-6))),
            Row("older", updated: Utc(now.Date.AddDays(-7).AddMinutes(-1))),
            Row("none"),
        };

        var groups = SessionsHubProjection.Group(rows, SessionsGroupBy.Date, nowLocal: now);

        Assert.Equal(["today", "yesterday", "week", "older", ""], groups.Select(group => group.Id));
        Assert.All(groups, group => Assert.Equal(group.Id.Length == 0 ? "none" : group.Id, Assert.Single(group.Rows).Key));
    }

    [Fact]
    public void Group_PersonAgentAndChannelSortAlphabeticallyWithUngroupedLast()
    {
        var rows = new[]
        {
            Row("agent:research:x", owner: new("human", "p2", "Zed"), channel: "telegram"),
            Row("agent:main:y", owner: new("agent", "main", null), channel: "discord"),
            Row("agent:main:z"),
        };

        Assert.Equal(["agent:main", "human:p2", ""],
            SessionsHubProjection.Group(rows, SessionsGroupBy.Person).Select(group => group.Id));
        Assert.Equal(["main", "research"],
            SessionsHubProjection.Group(rows, SessionsGroupBy.Agent).Select(group => group.Id));
        Assert.Equal(["discord", "telegram", ""],
            SessionsHubProjection.Group(rows, SessionsGroupBy.Channel).Select(group => group.Id));
    }

    [Fact]
    public void Group_KindPrefersGatewayKindForGlobalAndUnknownRows()
    {
        Assert.Equal("global", SessionsHubProjection.DisplayKind(Row("global", kind: "global")));
        Assert.Equal("cron", SessionsHubProjection.DisplayKind(Row("agent:main:cron:nightly", kind: "direct")));
    }

    [Fact]
    public void Sort_PinsFirstThenSelectedColumn()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Row("b", updated: t0.AddHours(3)),
            Row("a", updated: t0.AddHours(1), pinned: true, pinnedAt: 10),
            Row("c", updated: t0.AddHours(2)),
            Row("d", updated: t0, pinned: true, pinnedAt: 20),
        };

        Assert.Equal(["d", "a", "b", "c"],
            SessionsHubProjection.Sort(rows, SessionsSortColumn.Updated, descending: true, row => row.Key).Select(row => row.Key));
        Assert.Equal(["d", "a", "b", "c"],
            SessionsHubProjection.Sort(rows, SessionsSortColumn.Name, descending: false, row => row.Key).Select(row => row.Key));
    }

    [Fact]
    public void Page_ClampsIndexAndReportsCount()
    {
        var rows = Enumerable.Range(0, 23).Select(i => Row($"r{i:D2}")).ToArray();

        var (page, index, count) = SessionsHubProjection.Page(rows, 9, 10);

        Assert.Equal(2, index);
        Assert.Equal(3, count);
        Assert.Equal(["r20", "r21", "r22"], page.Select(row => row.Key));
    }

    // ── Selection, deletion, permissions ──

    [Fact]
    public void Selection_TogglesPagesAndReconcilesRemovedRows()
    {
        var selection = SessionsHubSelection.Empty.Toggle("a").Set(["b", "c"], selected: true);
        Assert.Equal(3, selection.Count);
        Assert.Null(selection.PageState(["a", "z"]));
        Assert.True(selection.PageState(["a", "b"]));

        selection = selection.Toggle("a").Reconcile(["b", "x"]);

        Assert.Equal(["b"], selection.Keys);
        Assert.False(selection.PageState(["x"]));
    }

    [Fact]
    public void DeletePlan_BlocksMainAndAdminOnlyRowsAndGuardsIdentity()
    {
        var write = new SessionsHubPermissions(true, ["operator.write"], []);
        var rows = new[]
        {
            Row("agent:main:main", archived: true),
            Row("agent:main:archived", archived: true),
            Row("agent:main:active"),
        };

        var plan = SessionsHubDeletion.Plan(rows, write, row => row.Key == "agent:main:main");

        var request = Assert.Single(plan.Requests);
        Assert.Equal("agent:main:archived", request.Key);
        Assert.True(request.ArchivedOnly);
        Assert.Equal("id-agent:main:archived", request.ExpectedSessionId);
        Assert.Equal("main", request.AgentId);
        Assert.Equal(["agent:main:main", "agent:main:active"], plan.Blocked.Select(row => row.Key));

        var admin = new SessionsHubPermissions(true, ["operator.admin"], []);
        var adminPlan = SessionsHubDeletion.Plan(rows, admin, _ => false);
        Assert.False(adminPlan.Requests.Single(r => r.Key == "agent:main:active").ArchivedOnly);
    }

    [Fact]
    public void DeleteAllArchived_PlansOnlyArchivedRows()
    {
        var admin = new SessionsHubPermissions(true, ["operator.admin"], []);
        var plan = SessionsHubDeletion.PlanDeleteAllArchived(
            [Row("a", archived: true), Row("b"), Row("c", archived: true)], admin, _ => false);

        Assert.Equal(["a", "c"], plan.Requests.Select(request => request.Key));
        Assert.All(plan.Requests, request => Assert.True(request.ArchivedOnly));
    }

    [Theory]
    [InlineData(true, "operator.write", true, true)]
    [InlineData(true, "operator.admin", true, true)]
    [InlineData(true, "operator.sessions.write", true, false)]
    [InlineData(true, "operator.read", true, false)]
    [InlineData(true, "operator.admin", false, false)]
    [InlineData(false, "operator.admin", true, false)]
    public void GroupCreation_RequiresCatalogWriteAndAnExplicitAtomicMethod(
        bool connected, string scope, bool advertised, bool expected)
    {
        var permissions = new SessionsHubPermissions(connected, [scope],
            advertised ? ["sessions.groups.create"] : []);
        Assert.Equal(expected, permissions.CanCreateGroup);
    }

    [Fact]
    public void UndoArchivePatch_RestoresPinOnlyWhenPinned()
    {
        Assert.Equal(true, SessionsHubDeletion.UndoArchivePatch(true).ToPayload("k")["pinned"]);
        Assert.False(SessionsHubDeletion.UndoArchivePatch(false).ToPayload("k").ContainsKey("pinned"));
    }

    [Fact]
    public void Permissions_DisconnectedOrReadOnlyCannotMutate()
    {
        Assert.False(SessionsHubPermissions.Disconnected.CanWrite);
        var read = new SessionsHubPermissions(true, ["operator.read"], ["worktrees.list"]);
        Assert.False(read.CanWrite);
        Assert.False(read.CanManageWorktrees);
        Assert.False(read.CanDelete(Row("a", archived: true)));

        var admin = new SessionsHubPermissions(true, ["operator.admin"], ["sessions.list"]);
        Assert.True(admin.CanWrite);
        Assert.False(admin.Supports("worktrees.list"));
        Assert.False(admin.CanManageWorktrees);
        Assert.True(new SessionsHubPermissions(true, ["operator.admin"], []).Supports("worktrees.list"));
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("local", null, false, false)]
    [InlineData("reclaimed", null, false, false)]
    [InlineData("provisioning", null, false, true)]
    [InlineData("active", null, false, true)]
    [InlineData("active", null, true, false)]
    [InlineData("failed", "restart", false, false)]
    [InlineData("failed", "stop-first", false, true)]
    public void CanStopCloudWorker_FollowsPlacementPolicy(string? state, string? recovery, bool running, bool expected)
    {
        var row = Row("a");
        row.PlacementState = state;
        row.PlacementRecoveryAction = recovery;
        row.HasActiveRun = running;
        Assert.Equal(expected, SessionsHubProjection.CanStopCloudWorker(row));
    }

    [Theory]
    [InlineData("fix-login", true)]
    [InlineData("", true)]
    [InlineData("Fix", false)]
    [InlineData("-lead", false)]
    [InlineData("has space", false)]
    public void WorktreeName_FollowsGatewayPattern(string name, bool valid) =>
        Assert.Equal(valid, WorktreesProjection.IsValidName(name));

    [Fact]
    public void Worktrees_SortByMostRecentActivity()
    {
        WorktreeRecord W(string id, long active) => new(id, id, "/r", null, "b", null, "manual", null, 0, active, null);
        Assert.Equal(["b", "a"], WorktreesProjection.Sort([W("a", 1), W("b", 5)]).Select(row => row.Id));
    }

    [Theory]
    [InlineData("invalid sessions.list params: at root: unexpected property 'source'", true)]
    [InlineData("invalid sessions.list params: at root: unexpected property 'excludeDock'", true)]
    [InlineData("invalid sessions.list params: at /limit: must be >= 1", false)]
    [InlineData("invalid sessions.patch params: at root: unexpected property 'source'", false)]
    [InlineData(null, false)]
    public void ListFallback_RecognizesOnlyRejectedPageSourceParams(string? message, bool expected) =>
        Assert.Equal(expected, OpenClawGatewayClient.IsRejectedPageSourceParam(message));

    [Fact]
    public void Selection_PageKeysExcludeRowsInCollapsedGroups()
    {
        var rows = new[] { Row("a", category: "Ops"), Row("b", category: "Ops"), Row("c", category: "Release"), Row("d") };
        var groups = SessionsHubProjection.Group(rows, SessionsGroupBy.Category, ["Ops", "Release"]);
        var collapsed = new HashSet<string>(StringComparer.Ordinal) { "Ops" };

        var keys = SessionsHubSelection.ExpandedKeys(rows, groups, collapsed);

        Assert.Equal(["c", "d"], keys);
        Assert.Equal(["a", "b", "c", "d"],
            SessionsHubSelection.ExpandedKeys(rows, groups, new HashSet<string>(StringComparer.Ordinal)));
    }

    // ── Appearance ──

    [Theory]
    [InlineData(" Purple ", "purple")]
    [InlineData("CYAN", "cyan")]
    [InlineData("magenta", null)]
    [InlineData("#ff0000", null)]
    [InlineData(null, null)]
    public void Appearance_NormalizesOnlyGatewayColorNames(string? input, string? expected) =>
        Assert.Equal(expected, SessionAppearance.NormalizeColor(input));

    [Theory]
    [InlineData("bot", "bot")]
    [InlineData(" Kanban ", "kanban")]
    [InlineData("data:image/svg+xml,%3Csvg%3E", null)]
    [InlineData("rocket", null)]
    public void Appearance_MapsOnlyNamedIconGlyphs(string input, string? expected) =>
        Assert.Equal(expected, SessionAppearance.NormalizeIcon(input));

    [Fact]
    public void Appearance_PaletteColorIsStableAndInPalette()
    {
        var first = SessionAppearance.PaletteColor("research");
        Assert.Equal(first, SessionAppearance.PaletteColor(" Research "));
        Assert.Contains(first, SessionAppearance.ColorIds);
    }

    [Theory]
    [InlineData("Fixture Research", "FR")]
    [InlineData("ops", "O")]
    [InlineData("  ", "?")]
    [InlineData("agent:main", "AM")]
    public void Appearance_InitialsUseTheFirstTwoWords(string name, string expected) =>
        Assert.Equal(expected, SessionAppearance.Initials(name));

    [Fact]
    public void Patch_EmitsCategoryColorAndIconAndClearsWithNull()
    {
        var set = new SessionPatch { Category = "Ops", Color = "green", Icon = "book" }.ToPayload("k");
        Assert.Equal("Ops", set["category"]);
        Assert.Equal("green", set["color"]);
        Assert.Equal("book", set["icon"]);
        var clear = new SessionPatch { Category = SessionPatch.Clear, Color = SessionPatch.Clear, Icon = SessionPatch.Clear };
        Assert.True(clear.HasChanges);
        var payload = clear.ToPayload("k");
        Assert.True(payload.ContainsKey("color") && payload["color"] is null);
        Assert.True(payload.ContainsKey("icon") && payload["icon"] is null);
        Assert.True(payload.ContainsKey("category") && payload["category"] is null);
    }

    [Fact]
    public void LiveSignature_IgnoresActivityTicksButTracksRosterChanges()
    {
        SessionInfo Row(string status = "active", string? activity = null, string owner = "Ada") =>
            new() { Key = "agent:main:a", Status = status, CurrentActivity = activity, LastSeen = DateTime.UtcNow,
                Owner = new SessionOwnerInfo("human", "profile-1", owner) };
        var baseline = SessionsHubProjection.LiveSignature([Row()]);

        Assert.Equal(baseline, SessionsHubProjection.LiveSignature([Row(activity: "Reading files")]));
        Assert.NotEqual(baseline, SessionsHubProjection.LiveSignature([Row(status: "done")]));
        Assert.NotEqual(baseline, SessionsHubProjection.LiveSignature([Row(owner: "Ada L.")]));
        Assert.NotEqual(baseline, SessionsHubProjection.LiveSignature([Row(), new SessionInfo { Key = "agent:main:b" }]));
    }

    [Theory]
    [InlineData("agent.run.started", "agent:main:a", true)]
    [InlineData("agent.input.settled", "agent:main:a", true)]
    [InlineData("agent.run.started", "global:untracked", false)]
    [InlineData("agent.run.started", null, false)]
    [InlineData("agent.other", "agent:main:a", false)]
    [InlineData("groups", null, false)]
    [InlineData("chat.title", "agent:main:a", false)]
    [InlineData(null, "agent:main:a", false)]
    public void TrackedRunChange_SkipsOnlyRunReasonsForTrackedSessions(string? reason, string? key, bool expected) =>
        Assert.Equal(expected, SessionsHubProjection.IsTrackedRunChange(
            new SessionListChange(reason, key), [new SessionInfo { Key = "agent:main:a" }]));
}