using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatPlacementHistoryProjectionTests
{
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
