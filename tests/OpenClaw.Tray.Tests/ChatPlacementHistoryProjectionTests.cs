using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatPlacementHistoryProjectionTests
{
    [Fact]
    public void ChildActivityRequiresLiveFactsAndKeepsExactParentIdentity()
    {
        var input = Input(ConnectionStatus.Connected) with
        {
            Sessions =
            [
                new() { Key = "parent", HasActiveRun = false, HasActiveSubagentRun = true },
                new() { Key = "child", ParentSessionKey = "parent", HasActiveRun = true, CurrentActivity = "Checking results" },
                new() { Key = "old", ParentSessionKey = "PARENT", HasActiveRun = false, CurrentActivity = "Stale activity" },
            ],
        };
        var threads = ChatSnapshotProjector.Project(input).Threads;
        Assert.True(threads[0].WaitingForSubagents);
        Assert.Equal("parent", threads[1].ParentSessionKey);
        Assert.Equal("Checking results", threads[1].CurrentActivity);
        Assert.Null(threads[2].CurrentActivity);
    }

    [Fact]
    public void OlderHistoryPreservesActiveRowsAndRevisionWhileRetiringResetCursor()
    {
        var history = new ChatHistoryState();
        var token = history.CreateCommitToken("main", 0);
        history.MarkCommitted(token, "instance", "older-1");
        Assert.Equal(new ChatHistoryPageRequest("older-1", "instance"), history.OlderPage("main"));
        var revision = history.Revision("main");
        history.MarkCommitted(token, "instance", "older-2", olderPage: true);
        Assert.Equal(revision, history.Revision("main"));
        history.ClearSessionForReset("main");
        Assert.Null(history.OlderPage("main"));
        Assert.Empty(history.SnapshotOlderHistoryThreads());

        var prior = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatMessageDeltaEvent("Active answer"));
        var active = Assert.Single(prior.Entries);
        var earlier = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatUserMessageEvent("Older question"));
        var (merged, _) = ChatHistoryState.PrependOlderPage(new("instance", earlier,
            new Dictionary<string, ChatEntryMetadata>(), 0), prior, new Dictionary<string, ChatEntryMetadata>());
        Assert.Equal(["Older question", "Active answer"], merged.Entries.Select(entry => entry.Text));
        Assert.Same(active, merged.Entries[1]);
        Assert.Equal(prior.ActiveAssistantId, merged.ActiveAssistantId);
        Assert.True(merged.TurnActive);
        Assert.Equal(2, merged.Entries.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void HistoryState_SnapshotDoesNotExposeMutableLoadedThreads()
    {
        var history = new ChatHistoryState();
        history.MarkCommitted(history.CreateCommitToken("main", 0), sessionId: null);
        var committed = history.SnapshotCurrentHistoryThreads();

        history.AdvanceConnectionGeneration(clearLoaded: true);

        Assert.Contains("main", committed);
        Assert.Empty(history.SnapshotCurrentHistoryThreads());
    }

    [Fact]
    public void Project_CopiesCurrentHistoryThreadsWithExactSessionKeys()
    {
        var currentHistory = new HashSet<string> { "main" };
        var snapshot = ChatSnapshotProjector.Project(Input(ConnectionStatus.Connected, currentHistory));

        currentHistory.Clear();
        currentHistory.Add("other");

        Assert.Equal("main", Assert.Single(snapshot.CurrentHistoryThreads!));
        Assert.False(snapshot.CurrentHistoryThreads!.Contains("MAIN"));
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Error)]
    public void Project_NonconnectedStateDoesNotExposeHistoryAuthority(ConnectionStatus status)
    {
        var snapshot = ChatSnapshotProjector.Project(Input(status, new HashSet<string> { "main" }));

        Assert.Empty(snapshot.CurrentHistoryThreads!);
        Assert.True(snapshot.Timelines["main"].HistoryLoaded);
        Assert.Empty(snapshot.Timelines["main"].Entries);
    }

    [Fact]
    public void Project_MissingHistoryAuthorityDoesNotTrustCachedEmptyHistory()
    {
        var snapshot = ChatSnapshotProjector.Project(Input(ConnectionStatus.Connected));

        Assert.Empty(snapshot.CurrentHistoryThreads!);
        Assert.True(snapshot.Timelines["main"].HistoryLoaded);
        Assert.Empty(snapshot.Timelines["main"].Entries);
    }

    [Fact]
    public void ComposerInputs_FreshnessAndNewTargetParticipateInProjectionIdentity()
    {
        var inputs = new ChatComposerInputs(
            "connected", false, new ChatThread { Id = "main", Title = "Main" },
            [], null, false, [], null, false);

        Assert.False(inputs.PlacementHistoryReady);
        Assert.False(inputs.PlacementTargetIsNew);
        Assert.True(inputs.HasSameProjection(inputs with { }));
        Assert.False(inputs.HasSameProjection(inputs with { PlacementHistoryReady = true }));
        Assert.False(inputs.HasSameProjection(inputs with { PlacementTargetIsNew = true }));
    }

    [Fact]
    public void Root_RequiresExplicitHistoryAuthoritySeparateFromVisibleEmptiness()
    {
        // retirement_condition: replace this guard when root projection is executable without WinUI.
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs"));
        var start = root.IndexOf("var placementHistoryReady =", StringComparison.Ordinal);
        var end = root.IndexOf("var welcomeEligible =", start, StringComparison.Ordinal);
        var readiness = root[start..end];

        Assert.Contains("isGatewayConnected", readiness);
        Assert.Contains("isComposeOnly", readiness);
        Assert.Contains("snapshot.ComposeTarget.IsReady", readiness);
        Assert.Contains("string.Equals(snapshot.ComposeTarget.SessionKey, effectiveThread?.Id, StringComparison.Ordinal)", readiness);
        Assert.Contains("snapshot.CurrentHistoryThreads?.Contains(effectiveThread.Id) == true", readiness);
        Assert.DoesNotContain("HistoryLoaded", readiness);
        Assert.Contains("ShowPlacementPickers = placementHistoryReady && mode == ReactorChatTimelineMode.Empty", root);
        Assert.Contains("PlacementHistoryReady = placementHistoryReady", root);
        Assert.Contains("PlacementTargetIsEmpty = isEmptyConversation", root);
        Assert.Contains("PlacementTargetIsNew = isComposeOnly", root);
    }

    private static ChatSnapshotProjectionInput Input(
        ConnectionStatus status,
        IReadOnlySet<string>? currentHistoryThreads = null) =>
        new(
            Sessions: [new SessionInfo { Key = "main", IsMain = true }],
            Timelines: new Dictionary<string, ChatTimelineState>
            {
                ["main"] = ChatTimelineState.Initial() with { HistoryLoaded = true }
            },
            TimelineGenerations: new Dictionary<string, long>(),
            HistoryRevisions: new Dictionary<string, long>(),
            QueuedMessages: new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>(),
            SessionsListReceived: true,
            AvailableModels: [],
            ModelChoices: [],
            CommandCatalog: null,
            Status: status,
            MainSessionKey: "main",
            HasHandshakeSnapshot: true,
            RememberedDefaultThreadId: null,
            RememberedThreadTitle: null,
            RememberedModel: null,
            RememberedModelProvider: null,
            CurrentHistoryThreads: currentHistoryThreads);
}
