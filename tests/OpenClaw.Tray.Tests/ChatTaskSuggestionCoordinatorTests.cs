using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTaskSuggestionCoordinatorTests
{
    private static GatewayTaskSuggestion Suggestion(string id = "task_1") =>
        new(id, "Repair tests", "Run regression tests.", "Restore coverage.", "/repo", "agent:main:main", "main", 17);

    [Fact]
    public void StartAndDroppedEventsRecoverFromAuthoritativeList()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway, clock);
        owner.Start();
        Assert.Single(owner.Snapshot().Cards);
        gateway.Rows = [];
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(owner.Snapshot().Cards);
        Assert.Empty(gateway.Accepts);
    }

    [Fact]
    public void OtherClientResolutionInvalidatesRatherThanInventingLocalOutcome()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway, clock);
        owner.Start();
        gateway.Rows = [];
        gateway.Invalidate();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(owner.Snapshot().Cards);
        Assert.Equal(0, gateway.RefreshSessions);
    }

    [Fact]
    public async Task ConfirmedLaunchRefreshesSessionsAndKeepsSameTaskReceipt()
    {
        var gateway = new Gateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.AcceptAsync(new("task_1", "local"));
        Assert.Equal("agent:main:dashboard:new", Assert.Single(owner.Snapshot().Cards).StartedKey);
        Assert.Equal(1, gateway.RefreshSessions);
        await owner.AcceptAsync(new("task_1", "worktree"));
        Assert.Single(gateway.Accepts);
    }

    [Fact]
    public async Task LostAcknowledgmentRetainsIdentityAndOnlyExplicitRetryUsesFrozenOptions()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.AcceptAsync(new("task_1", "worktree", Cwd: "/original"));
        gateway.Rows = []; // Pending-only absence is not evidence of failure or success.
        await owner.RefreshAsync();
        Assert.True(Assert.Single(owner.Snapshot().Cards).Unconfirmed);
        Assert.Single(gateway.Accepts);
        gateway.Accept = _ => Task.FromResult("agent:main:dashboard:new");
        await owner.AcceptAsync(new("task_1", "local", Cwd: null));
        Assert.Equal(2, gateway.Accepts.Count);
        Assert.All(gateway.Accepts, request => Assert.Equal(new("task_1", "worktree", Cwd: "/original"), request));
        Assert.Equal(1, gateway.RefreshSessions);
    }

    [Fact]
    public async Task ReconnectNeverReplaysUnknownMutationOnANewConnection()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.AcceptAsync(new("task_1", "session"));
        gateway.Connect(null);
        gateway.Rows = [];
        gateway.Connect(2);
        await owner.AcceptAsync(new("task_1", "session"));
        Assert.Single(gateway.Accepts);
        var card = Assert.Single(owner.Snapshot().Cards);
        Assert.True(card.Unconfirmed);
        Assert.Equal(1, card.AttemptEpoch);
        Assert.Equal(0, gateway.RefreshSessions);
    }

    [Fact]
    public async Task LateAcceptanceFromOldConnectionCannotReportSuccessOrRefreshNewSessions()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { Accept = _ => pending.Task };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        var operation = owner.AcceptAsync(new("task_1", "local"));
        gateway.Connect(null);
        gateway.Rows = [];
        gateway.Connect(2);
        pending.SetResult("old-session");
        await operation;
        Assert.Null(Assert.Single(owner.Snapshot().Cards).StartedKey);
        Assert.Equal(0, gateway.RefreshSessions);
    }

    [Fact]
    public async Task DismissFalseStillRefreshesAndRemovesTaskAcceptedByAnotherClient()
    {
        var gateway = new Gateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        gateway.Dismiss = () => { gateway.Rows = []; return Task.FromResult(false); };
        await owner.DismissAsync("task_1");
        Assert.Empty(owner.Snapshot().Cards);
        Assert.Empty(gateway.Accepts);
    }

    [Fact]
    public async Task ConcurrentAcceptanceIsSingleFlightAndCannotBeDismissed()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { Accept = _ => pending.Task };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        var operation = owner.AcceptAsync(new("task_1", "local"));
        await owner.AcceptAsync(new("task_1", "session"));
        await owner.DismissAsync("task_1");
        Assert.Single(gateway.Accepts);
        Assert.Equal(0, gateway.Dismissals);
        pending.SetResult("new-session");
        await operation;
    }

    [Fact]
    public void UnsupportedGatewayDoesNotReceiveSpeculativeOrRepeatedProbes()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway { TaskSuggestionsSupported = false };
        using var owner = new ChatTaskSuggestionCoordinator(gateway, clock);
        owner.Start();
        clock.Advance(TimeSpan.FromMinutes(2));
        gateway.Invalidate();
        Assert.Equal(0, gateway.Lists);
        Assert.Empty(owner.Snapshot().Cards);
    }

    [Fact]
    public async Task RefreshErrorIsVisibleAndManualReadRecoveryDoesNotLaunchAnything()
    {
        var gateway = new Gateway { FailRead = true };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        Assert.True(owner.Snapshot().RefreshFailed);
        gateway.FailRead = false;
        await owner.RefreshAsync();
        Assert.False(owner.Snapshot().RefreshFailed);
        Assert.Single(owner.Snapshot().Cards);
        Assert.Empty(gateway.Accepts);
    }

    [Fact]
    public async Task EventDuringListDoesNotStarvePublicationAndSchedulesCoalescedRecovery()
    {
        var pending = new TaskCompletionSource<GatewayTaskSuggestionList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualTimeProvider();
        var gateway = new Gateway { List = () => pending.Task };
        using var owner = new ChatTaskSuggestionCoordinator(gateway, clock);
        owner.Start();
        var first = owner.RefreshAsync();
        gateway.Rows = [];
        gateway.Invalidate();
        gateway.List = null;
        pending.SetResult(new(true, [Suggestion()]));
        await first;
        Assert.Single(owner.Snapshot().Cards);
        clock.Advance(TimeSpan.FromSeconds(1));
        await owner.RefreshAsync();
        Assert.Empty(owner.Snapshot().Cards);
        Assert.InRange(gateway.Lists, 2, 3);
    }

    [Fact]
    public async Task LateReadAfterDisconnectCannotRestorePreviousGatewayCards()
    {
        var pending = new TaskCompletionSource<GatewayTaskSuggestionList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { List = () => pending.Task };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        var first = owner.RefreshAsync();
        gateway.Connect(null);
        gateway.Rows = [];
        gateway.List = null;
        gateway.Connect(2);
        pending.SetResult(new(true, [Suggestion()]));
        await first;
        Assert.Empty(owner.Snapshot().Cards);
    }

    [Fact]
    public void DisposeDetachesInvalidationAndStopsPeriodicReads()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway();
        var owner = new ChatTaskSuggestionCoordinator(gateway, clock);
        owner.Start();
        owner.Dispose();
        gateway.Invalidate();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, gateway.Lists);
        Assert.Empty(owner.Snapshot().Cards);
    }

    [Fact]
    public async Task RepositoryRejectionBeforeSideEffectsAllowsCorrectingOptions()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(GatewayRequestErrors.Create(
            "synthetic", "INVALID_REQUEST", GatewayRequestErrors.TaskWorktreeSourceRequired)) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.AcceptAsync(new("task_1", "worktree", Cwd: "/wrong"));
        var card = Assert.Single(owner.Snapshot().Cards);
        Assert.True(card.RepositoryRequired);
        Assert.Null(card.Attempt);
        gateway.Accept = _ => Task.FromResult("new-session");
        await owner.AcceptAsync(new("task_1", "worktree", Cwd: "/correct"));
        Assert.Equal("/correct", gateway.Accepts.Last().Cwd);
    }

    [Fact]
    public async Task CloudPreflightFailureAllowsCorrectingProfileWithoutReplayingMutation()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new GatewayTaskCloudProfileException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.AcceptAsync(new("task_1", "cloud", "typo"));
        Assert.True(Assert.Single(owner.Snapshot().Cards).CloudProfileRequired);
        Assert.Null(Assert.Single(owner.Snapshot().Cards).Attempt);
        gateway.Accept = _ => Task.FromResult("new-session");
        await owner.AcceptAsync(new("task_1", "cloud", "valid"));
        Assert.Equal("valid", gateway.Accepts.Last().CloudProfileId);
    }

    [Fact]
    public async Task RetryPreflightFailureCannotEraseEarlierUncertainCloudAcceptance()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.AcceptAsync(new("task_1", "cloud", "original-profile"));
        gateway.Accept = _ => Task.FromException<string>(new GatewayTaskCloudProfileException());
        await owner.AcceptAsync(new("task_1", "cloud", "changed-profile"));
        var card = Assert.Single(owner.Snapshot().Cards);
        Assert.True(card.Unconfirmed);
        Assert.False(card.CloudProfileRequired);
        Assert.Equal("original-profile", card.Attempt!.CloudProfileId);
        await owner.DismissAsync("task_1");
        Assert.Equal(0, gateway.Dismissals);
    }

    [Fact]
    public void ContinuousInvalidationsPublishSnapshotsWithoutImmediateReadChain()
    {
        var clock = new ManualTimeProvider();
        var gateway = new Gateway();
        gateway.List = () =>
        {
            gateway.Invalidate();
            gateway.Invalidate();
            return Task.FromResult(new GatewayTaskSuggestionList(true, [Suggestion()]));
        };
        using var owner = new ChatTaskSuggestionCoordinator(gateway, clock);
        owner.Start();
        Assert.Single(owner.Snapshot().Cards);
        Assert.Equal(1, gateway.Lists);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(owner.Snapshot().Cards);
        Assert.Equal(2, gateway.Lists);
    }

    [Fact]
    public async Task LaterPendingSnapshotCannotUnfreezeTimedOutAcceptanceOrPermitCrossEpochReplay()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        var original = new GatewayTaskAcceptance("task_1", "worktree", Cwd: "/original");
        await owner.AcceptAsync(original);
        await owner.RefreshAsync(); // The producer may still be awaiting authorization before claiming.
        var card = Assert.Single(owner.Snapshot().Cards);
        Assert.True(card.Unconfirmed);
        Assert.Equal(original, card.Attempt);
        await owner.AcceptAsync(new("task_1", "local"));
        Assert.All(gateway.Accepts, request => Assert.Equal(original, request));
        gateway.Connect(2);
        await owner.RefreshAsync();
        await owner.AcceptAsync(new("task_1", "cloud", "different"));
        Assert.Equal(2, gateway.Accepts.Count);
        Assert.Equal(original, Assert.Single(owner.Snapshot().Cards).Attempt);
        Assert.Equal(1, Assert.Single(owner.Snapshot().Cards).AttemptEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalHideMakesNoRpcOrOutcomeClaimAndSuppressesOnlySameIdForProviderLifetime(bool reconnect)
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        Assert.False(owner.HideLocally("task_1")); // A pending proposal still uses authoritative Dismiss.
        await owner.AcceptAsync(new("task_1", "session"));
        if (reconnect) gateway.Connect(2);
        var lists = gateway.Lists;
        Assert.True(owner.HideLocally("task_1"));
        Assert.Empty(owner.Snapshot().Cards);
        Assert.Equal(lists, gateway.Lists);
        Assert.Single(gateway.Accepts);
        Assert.Equal(0, gateway.Dismissals);
        Assert.Equal(0, gateway.RefreshSessions);
        gateway.Rows = [Suggestion(), Suggestion("unrelated")];
        await owner.RefreshAsync();
        Assert.Equal("unrelated", Assert.Single(owner.Snapshot().Cards).Suggestion.Id);
        await owner.AcceptAsync(new("task_1", "local")); // A stale captured callback must not dispatch.
        Assert.Single(gateway.Accepts);
        gateway.Connect(3);
        Assert.Equal("unrelated", Assert.Single(owner.Snapshot().Cards).Suggestion.Id);
    }

    [Fact]
    public async Task LocalHideSuppressionIsBoundedWithoutEvictingAndReopeningHiddenIds()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        for (var index = 0; index < 100; index++)
        {
            var id = "hidden_" + index;
            gateway.Rows = [Suggestion(id)];
            await owner.RefreshAsync();
            await owner.AcceptAsync(new(id, "session"));
            Assert.True(owner.HideLocally(id));
        }
        Assert.False(owner.Snapshot().CanHideLocally);
        gateway.Rows = [Suggestion("hidden_0"), Suggestion("new")];
        await owner.RefreshAsync();
        Assert.Equal("new", Assert.Single(owner.Snapshot().Cards).Suggestion.Id);
        await owner.AcceptAsync(new("new", "session"));
        Assert.False(owner.HideLocally("new"));
        Assert.Single(owner.Snapshot().Cards);
    }

    [Fact]
    public async Task EarlierUncertainWorktreeAttemptSurvivesLaterPreclaimRepositoryRejection()
    {
        var gateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        var original = new GatewayTaskAcceptance("task_1", "worktree", Cwd: "/original");
        await owner.AcceptAsync(original);
        gateway.Accept = _ => Task.FromException<string>(GatewayRequestErrors.Create("synthetic", "INVALID_REQUEST", GatewayRequestErrors.TaskWorktreeSourceRequired));
        await owner.AcceptAsync(new("task_1", "local"));
        var card = Assert.Single(owner.Snapshot().Cards);
        Assert.Equal(original, card.Attempt);
        Assert.Equal(1, card.AttemptEpoch);
        Assert.True(card.Unconfirmed);
        Assert.False(card.RepositoryRequired);
        await owner.DismissAsync("task_1");
        Assert.Equal(0, gateway.Dismissals);
        await owner.AcceptAsync(new("task_1", "cloud", "different"));
        Assert.All(gateway.Accepts, request => Assert.Equal(original, request));
    }

    [Theory]
    [InlineData("repo")]
    [InlineData(".")]
    [InlineData("~/x")]
    [InlineData("C:relative")]
    public async Task RelativeWorktreeCorrectionNeverDispatchesOrFreezesOptions(string path)
    {
        var gateway = new Gateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.AcceptAsync(new("task_1", "worktree", Cwd: path));
        Assert.Empty(gateway.Accepts);
        var card = Assert.Single(owner.Snapshot().Cards);
        Assert.Null(card.Attempt);
        Assert.True(card.RepositoryRequired);
        await owner.AcceptAsync(new("task_1", "worktree", Cwd: "/valid-repository"));
        Assert.Equal("/valid-repository", Assert.Single(gateway.Accepts).Cwd);
    }

    [Fact]
    public async Task DismissFailureSurvivesSuccessfulPendingRefreshAsCardFeedback()
    {
        var gateway = new Gateway { Dismiss = () => Task.FromException<bool>(new TimeoutException()) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start();
        await owner.DismissAsync("task_1");
        Assert.False(owner.Snapshot().RefreshFailed);
        var card = Assert.Single(owner.Snapshot().Cards);
        Assert.True(card.DismissFailed);
        gateway.Dismiss = () => { gateway.Rows = []; return Task.FromResult(true); };
        await owner.DismissAsync("task_1");
        Assert.Empty(owner.Snapshot().Cards);
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    [InlineData("device-missing")]
    public async Task IdentityPrerequisiteFailureIsExplicitScopedAndRecoversWithoutImportingPrivateTasks(string failure)
    {
        var custody = new ChatTaskSuggestionCustody();
        custody.SelectGateway("gateway");
        var originalGateway = new Gateway { Accept = _ => Task.FromException<string>(new TimeoutException()) };
        using (var original = new ChatTaskSuggestionCoordinator(originalGateway, custody: custody, gatewayId: "gateway"))
        {
            original.Start();
            await original.RefreshAsync();
            await original.AcceptAsync(new("task_1", "worktree", Cwd: "/original"));
        }
        var gateway = new Gateway { Identity = () => failure switch
        {
            "forbidden" => Task.FromException<string?>(GatewayRequestErrors.Create("synthetic", "FORBIDDEN", "MISSING_SCOPE")),
            "unavailable" => Task.FromException<string?>(new System.Text.Json.JsonException()),
            "timeout" => Task.FromException<string?>(new TimeoutException()),
            _ => Task.FromResult<string?>(null)
        } };
        using var owner = new ChatTaskSuggestionCoordinator(gateway, custody: custody, gatewayId: "gateway");
        owner.Start();
        await owner.RefreshAsync();
        Assert.Empty(owner.Snapshot().Cards);
        Assert.True(owner.Snapshot().IdentityUnavailable);
        owner.ObserveSession("first-session");
        owner.ObserveSession("unrelated-session");
        Assert.Equal("first-session", owner.Snapshot().IdentityNoticeSession);
        await owner.AcceptAsync(new("task_1", "local"));
        Assert.Empty(gateway.Accepts);
        gateway.Identity = () => Task.FromResult<string?>("verified-same-operator");
        await owner.RefreshAsync();
        var receipt = Assert.Single(owner.Snapshot().Cards);
        Assert.Equal("/original", receipt.Attempt!.Cwd);
        Assert.False(receipt.CanRetry);
        Assert.False(owner.Snapshot().IdentityUnavailable);
    }

    [Fact]
    public async Task IdentityNoticeCanBindBeforeHandshakeButNeverAppearsOnUnsupportedConnections()
    {
        var custody = new ChatTaskSuggestionCustody();
        custody.SelectGateway("gateway");
        var gateway = new Gateway { TaskSuggestionsSupported = false, Identity = () => Task.FromResult<string?>(null) };
        using var owner = new ChatTaskSuggestionCoordinator(gateway, custody: custody, gatewayId: "gateway");
        owner.ObserveSession("presented-before-handshake");
        owner.Start();
        Assert.False(owner.Snapshot().IdentityUnavailable);
        Assert.Equal(0, gateway.IdentityReads);
        gateway.TaskSuggestionsSupported = true;
        gateway.Connect(2);
        await owner.RefreshAsync();
        Assert.True(owner.Snapshot().IdentityUnavailable);
        Assert.Equal("presented-before-handshake", owner.Snapshot().IdentityNoticeSession);
    }

    [Fact]
    public async Task IdentityReadIsCachedUntilInvalidationAndLateOldIdentityCannotRestorePrivateData()
    {
        var custody = new ChatTaskSuggestionCustody();
        custody.SelectGateway("gateway");
        var gateway = new Gateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway, custody: custody, gatewayId: "gateway");
        owner.Start();
        await owner.RefreshAsync();
        await owner.RefreshAsync();
        Assert.Equal(1, gateway.IdentityReads);
        gateway.Accept = _ => Task.FromException<string>(new TimeoutException());
        await owner.AcceptAsync(new("task_1", "worktree", Cwd: "/private-original"));
        var stale = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Identity = () => stale.Task;
        gateway.InvalidateIdentity();
        Assert.Empty(owner.Snapshot().Cards);
        Assert.Equal(2, gateway.IdentityReads);
        gateway.Identity = () => Task.FromResult<string?>("different-verified-operator");
        gateway.Rows = [Suggestion("fresh-other-operator")];
        gateway.InvalidateIdentity();
        await owner.RefreshAsync();
        Assert.Equal(3, gateway.IdentityReads);
        stale.SetResult("verified-same-operator");
        await owner.RefreshAsync();
        Assert.Equal(3, gateway.IdentityReads);
        var fresh = Assert.Single(owner.Snapshot().Cards);
        Assert.Equal("fresh-other-operator", fresh.Suggestion.Id);
        Assert.Null(fresh.Attempt);
    }

    [Fact]
    public async Task PresentationObservationRevisionsAdvanceOnInvalidationAndRestoreWithoutDispatch()
    {
        var custody = new ChatTaskSuggestionCustody(); custody.SelectGateway("gateway");
        var gateway = new Gateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway, custody: custody, gatewayId: "gateway");
        var initial = owner.Snapshot();
        owner.Start(); await owner.RefreshAsync();
        var first = owner.Snapshot();
        Assert.True(first.PresentationRevision > initial.PresentationRevision);
        Assert.True(first.SnapshotRevision > initial.SnapshotRevision);
        var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Identity = () => held.Task;
        gateway.InvalidateIdentity();
        var invalidated = owner.Snapshot();
        Assert.Empty(invalidated.Cards);
        Assert.True(invalidated.PresentationRevision > first.PresentationRevision);
        held.SetResult("verified-different-operator");
        await owner.RefreshAsync();
        var restored = owner.Snapshot();
        Assert.Single(restored.Cards);
        Assert.True(restored.PresentationRevision > invalidated.PresentationRevision);
        Assert.True(restored.SnapshotRevision > invalidated.SnapshotRevision);
        gateway.Identity = () => Task.FromResult<string?>("verified-different-operator");
        gateway.InvalidateIdentity(); // Name/avatar edits may keep the operator identity.
        await owner.RefreshAsync();
        var sameOperator = owner.Snapshot();
        Assert.True(sameOperator.PresentationRevision > restored.PresentationRevision);
        await owner.RefreshAsync();
        var unchanged = owner.Snapshot();
        Assert.Equal(sameOperator.PresentationRevision, unchanged.PresentationRevision);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(owner.Snapshot)));
        Assert.Equal(32, concurrent.Select(snapshot => snapshot.SnapshotRevision).Distinct().Count());
        Assert.All(concurrent, snapshot =>
        {
            Assert.True(snapshot.SnapshotRevision > unchanged.SnapshotRevision);
            Assert.Equal(unchanged.PresentationRevision, snapshot.PresentationRevision);
        });
        Assert.True(owner.Snapshot().SnapshotRevision > concurrent.Max(snapshot => snapshot.SnapshotRevision));
        Assert.Empty(gateway.Accepts); Assert.Equal(0, gateway.Dismissals);
    }

    private sealed class Gateway : IChatTaskSuggestionGateway
    {
        public long? TaskConnectionEpoch { get; private set; } = 1;
        public bool TaskSuggestionsSupported { get; set; } = true;
        public bool TaskAdministratorRequired => false;
        public bool TaskAcceptanceSupported => TaskSuggestionsSupported;
        public bool TaskDismissalSupported => TaskSuggestionsSupported;
        public event EventHandler? TaskSuggestionsInvalidated;
        public event EventHandler? TaskOperatorIdentityInvalidated;
        public Func<Task<string?>> Identity = () => Task.FromResult<string?>("verified-same-operator");
        public int IdentityReads;
        public Task<string?> ReadTaskOperatorIdentityAsync(long epoch) { IdentityReads++; return Identity(); }
        public void InvalidateIdentity() => TaskOperatorIdentityInvalidated?.Invoke(this, EventArgs.Empty);
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public GatewayTaskSuggestion[] Rows = [Suggestion()];
        public Func<Task<GatewayTaskSuggestionList>>? List;
        public bool FailRead;
        public int Lists;
        public int RefreshSessions;
        public int Dismissals;
        public List<GatewayTaskAcceptance> Accepts = [];
        public Func<GatewayTaskAcceptance, Task<string>> Accept = _ => Task.FromResult("agent:main:dashboard:new");
        public Func<Task<bool>> Dismiss = () => Task.FromResult(true);
        public Task<GatewayTaskSuggestionList> ListTaskSuggestionsAsync(long epoch)
        {
            Lists++;
            return FailRead ? Task.FromException<GatewayTaskSuggestionList>(new TimeoutException())
                : List?.Invoke() ?? Task.FromResult(new GatewayTaskSuggestionList(true, Rows));
        }
        public Task<string> AcceptTaskSuggestionAsync(GatewayTaskAcceptance request, long epoch)
        { Accepts.Add(request); return Accept(request); }
        public Task<bool> DismissTaskSuggestionAsync(string id, long epoch)
        { Dismissals++; return Dismiss(); }
        public Task RequestSessionsAsync() { RefreshSessions++; return Task.CompletedTask; }
        public void Invalidate() => TaskSuggestionsInvalidated?.Invoke(this, EventArgs.Empty);
        public void Connect(long? epoch)
        {
            TaskConnectionEpoch = epoch;
            StatusChanged?.Invoke(this, epoch.HasValue ? ConnectionStatus.Connected : ConnectionStatus.Disconnected);
        }
    }
}
