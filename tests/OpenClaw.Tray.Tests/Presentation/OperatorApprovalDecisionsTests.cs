using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using static OpenClaw.Tray.Tests.Presentation.InboxGatewaySourceTests;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// The single operator decision owner: Inbox and toast decisions are revalidated against the
/// live pending approval immediately before sending, and only reach the kind-specific RPC on
/// the connection epoch the approval was observed on.
/// </summary>
public sealed partial class OperatorApprovalDecisionsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = Start;

    [Theory]
    [InlineData(GatewayApprovalKind.Exec)]
    [InlineData(GatewayApprovalKind.Plugin)]
    [InlineData(GatewayApprovalKind.SystemAgent)]
    public async Task DecideAsync_SendsKindSpecificDecisionOnTheObservedEpochAndRemovesTheRow(GatewayApprovalKind kind)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1", kind));

        var outcome = await decisions.DecideAsync("a1", GatewayApprovalDecisions.AllowOnce, Observation(source, "a1"));

        Assert.Equal(ApprovalDecisionOutcome.Sent, outcome);
        Assert.Equal([(kind, "a1", "allow-once", 7L)], gateway.Resolves);
        Assert.Empty(source.Items);
    }

    [Fact]
    public async Task DecideAsync_UsesTheLocalDeadlineNotTheGatewayClock()
    {
        var (source, gateway, decisions) = Create();
        // The Gateway's clock runs a day behind: its expiresAtMs is already past on this PC.
        var skewed = Start.AddDays(-1);
        gateway.Raise("PendingApprovalRequested", new GatewayPendingApproval(
            "a1", GatewayApprovalKind.Exec, "echo hi", skewed.ToUnixTimeMilliseconds(), skewed.AddMinutes(2).ToUnixTimeMilliseconds()));

        _now = Start.AddMinutes(1);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
    }

    [Theory]
    [InlineData("echo a\u00A0b")]
    [InlineData("")]
    public async Task DecideAsync_RefusesAllowWhenTheRowCannotShowTheWholeCommand(string command)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { Command = command });

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-always", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
    }

    [Fact]
    public async Task DecideAsync_RejectsDecisionsTheApprovalDoesNotOffer()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { AllowedDecisions = ["deny"] });

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "approve", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_WithoutApprovalScope_ShowsNoDecisionsAndSendsNothing()
    {
        var (source, gateway, decisions) = Create(scopes: ["operator.read"]);
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.False(Assert.Single(source.Items).CanDecide);
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_IgnoresUnknownExpiredAndOtherConnectionApprovals()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("other", "deny", Observation(source, "other")));

        gateway.Epoch = 8;
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));

        gateway.Epoch = 7;
        _now = Start.AddHours(2);
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_StaleRejectionRemovesTheRowLikeASuccess()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new GatewayApprovalStaleException("approval already resolved"));

        Assert.Equal(ApprovalDecisionOutcome.AlreadyResolved, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Empty(source.Items);
    }

    [Fact]
    public async Task DecideAsync_UnconfirmedOutcomeKeepsTheRowWithoutAutomaticRetry()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Single(gateway.Resolves); // A failure can occur after the resolve was sent.
        Assert.Single(source.Items);

        gateway.ResolveResult = () => Task.CompletedTask;
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.True(Assert.Single(source.Items).DecisionUncertain);
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_SendsOneDecisionAtATimePerApproval()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var reply = new TaskCompletionSource();
        gateway.ResolveResult = () => reply.Task;

        var first = decisions.DecideAsync("a1", "deny", Observation(source, "a1"));
        Assert.Equal(ApprovalDecisionOutcome.InFlight, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        reply.SetResult();

        Assert.Equal(ApprovalDecisionOutcome.Sent, await first);
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_IsOneShotAndBoundToItsApproval()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.Raise("PendingApprovalRequested", Approval("a2"));

        var token = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a2", "deny", token));
        // The mismatched use consumed the token.
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", token));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", "forged"));
        Assert.Empty(gateway.Resolves);

        var fresh = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideFromToastAsync("a1", "deny", fresh));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", fresh));
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_FromAnEarlierConnectionDecidesNothing()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var token = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;

        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Epoch = 8;
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "allow-once", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_RevokedWhileQueuedCannotReachTheResolveWrite()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var token = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;
        gateway.BeforeResolve = () => decisions.RevokeToastTokens("a1");

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "allow-once", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_CannotAuthorizeChangedContentUnderTheSameId()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var token = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { Command = "echo changed" });

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "allow-once", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_CannotSendADecisionTheToastDidNotOffer()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        var token = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideFromToastAsync("a1", "allow-always", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_StillRevalidatesTheDecision()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { AllowedDecisions = ["deny"] });
        var token = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideFromToastAsync("a1", "allow-once", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ChatDecision_UsesTheSamePathAndClearsTheInboxRow()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        await decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", null, "allow-always");

        Assert.Equal([(GatewayApprovalKind.Exec, "a1", "allow-always", 7L)], gateway.Resolves);
        Assert.Empty(source.Items);
    }

    [Fact]
    public async Task ChatDecision_ThrowsSoTheBannerStaysWhenTheDecisionIsNotOffered()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { Ask = "always" });

        await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", null, "allow-always"));

        Assert.Empty(gateway.Resolves);
        Assert.Single(source.Items);
    }

    [Fact]
    public async Task ChatDecision_StaleRejectionStillThrowsButClearsTheInboxRow()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new GatewayApprovalStaleException("approval already resolved"));

        await Assert.ThrowsAsync<GatewayApprovalStaleException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", null, "deny"));

        Assert.Empty(source.Items);
    }

    [Fact]
    public async Task Feed_SendsRowDecisionsThroughTheDecisionOwner()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var feed = new InboxFeed(new AppNotificationService(), source, decisions);
        var row = Assert.Single(feed.Snapshot.Entries).GatewayItem!;

        Assert.Equal(ApprovalDecisionOutcome.Sent, await feed.DecideApprovalAsync(row, "deny"));
        Assert.Empty(feed.Snapshot.Entries);
    }

    [Theory]
    [InlineData(GatewayApprovalKind.Exec, false)]
    [InlineData(GatewayApprovalKind.Exec, true)]
    [InlineData(GatewayApprovalKind.Plugin, false)]
    [InlineData(GatewayApprovalKind.Plugin, true)]
    [InlineData(GatewayApprovalKind.SystemAgent, false)]
    [InlineData(GatewayApprovalKind.SystemAgent, true)]
    public async Task IdenticalParsedReplay_KeepsTheDisplayedRowAndToastActionable(GatewayApprovalKind kind, bool useToast)
    {
        var (source, gateway, decisions) = Create();
        var original = ParsedApproval("a1", kind);
        var replay = ParsedApproval("a1", kind);
        Assert.NotSame(original.AllowedDecisions, replay.AllowedDecisions);
        gateway.Raise("PendingApprovalRequested", original);
        var feed = new InboxFeed(new AppNotificationService(), source, decisions);
        var displayed = Assert.Single(feed.Snapshot.Entries).GatewayItem!;
        var token = decisions.IssueToastToken(original, ["deny"]);
        gateway.Raise("PendingApprovalRequested", replay);

        Assert.Same(displayed.ApprovalObservation, Assert.Single(source.Items).ApprovalObservation);
        gateway.Replies[GatewayPendingApprovalParser.ListMethod(kind)] =
            System.Text.Json.JsonSerializer.SerializeToElement(new[] { ApprovalPayload("a1") });
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        Assert.Same(displayed.ApprovalObservation, Assert.Single(source.Items).ApprovalObservation);
        var outcome = useToast
            ? await decisions.DecideFromToastAsync("a1", "deny", token)
            : await feed.DecideApprovalAsync(displayed, "deny");
        Assert.Equal(ApprovalDecisionOutcome.Sent, outcome);
        Assert.Single(gateway.Resolves);
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("client-swap")]
    [InlineData("changed-request")]
    public async Task Feed_RejectsADecisionFromAnOlderDisplayedObservation(string change)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var feed = new InboxFeed(new AppNotificationService(), source, decisions);
        var displayed = Assert.Single(feed.Snapshot.Entries).GatewayItem!;
        var replacement = gateway;
        if (change == "reconnect")
        {
            gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
            gateway.Epoch = 8;
        }
        else if (change == "client-swap")
        {
            replacement = FakeGateway.Create();
            replacement.Scopes = ["operator.approvals"];
            source.AttachClient((IOperatorGatewayClient)replacement);
        }
        replacement.Raise("PendingApprovalRequested", change == "changed-request"
            ? Approval("a1") with { Command = "echo changed" }
            : Approval("a1"));

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await feed.DecideApprovalAsync(displayed, "allow-once"));
        Assert.Empty(gateway.Resolves);
        Assert.Empty(replacement.Resolves);
        Assert.Single(feed.Snapshot.Entries);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("resolved")]
    [InlineData("scope-revoked")]
    [InlineData("changed-request")]
    public async Task DecideAsync_RevalidatesTheObservationAtFinalAdmission(string change)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var observation = Observation(source, "a1");
        // The fake controls the external queue boundary, not the production admission decision.
        gateway.BeforeResolve = () =>
        {
            switch (change)
            {
                case "expired": _now = Start.AddHours(2); break;
                case "resolved": gateway.Raise("PendingApprovalResolved", "a1"); break;
                case "scope-revoked": gateway.Scopes = ["operator.read"]; break;
                default: gateway.Raise("PendingApprovalRequested", Approval("a1") with { Command = "echo changed" }); break;
            }
        };

        var outcome = await decisions.DecideAsync("a1", "allow-once", observation);

        Assert.Equal(change == "scope-revoked" ? ApprovalDecisionOutcome.NotAllowed : ApprovalDecisionOutcome.NotPending, outcome);
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_EmptyScopeListIsNotAnApprovalGrant()
    {
        var (source, gateway, decisions) = Create(scopes: []);
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.False(Assert.Single(source.Items).CanDecide);
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_MatchesContentAfterAReplayWhileTheReplyIsPending(bool changed)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", ParsedApproval("a1", GatewayApprovalKind.Exec));
        var reply = new TaskCompletionSource();
        gateway.ResolveResult = () => reply.Task;
        var sending = decisions.DecideAsync("a1", "deny", Observation(source, "a1"));
        var replay = ParsedApproval("a1", GatewayApprovalKind.Exec);
        gateway.Raise("PendingApprovalRequested", changed ? replay with { Command = "echo changed" } : replay);
        reply.SetResult();

        Assert.Equal(ApprovalDecisionOutcome.Sent, await sending);
        if (changed)
            Assert.Equal("echo changed", Assert.Single(source.Items).Approval!.Command);
        else
            Assert.Empty(source.Items);
    }

    [Fact]
    public async Task Retirement_FromAnEarlierConnectionLeavesTheReobservedApproval()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var reply = new TaskCompletionSource();
        gateway.ResolveResult = () => reply.Task;
        var sending = decisions.DecideAsync("a1", "deny", Observation(source, "a1"));

        // The same client reconnects and observes the approval again before the reply lands.
        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Epoch = 8;
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var token = decisions.IssueToastToken(source.Approvals.Pending["a1"].Approval, ToastOffers)!;
        reply.SetResult();

        Assert.Equal(ApprovalDecisionOutcome.Sent, await sending);
        Assert.Single(source.Items);
        // The new connection's toast still works.
        gateway.ResolveResult = () => Task.CompletedTask;
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideFromToastAsync("a1", "deny", token));
    }

    [Fact]
    public async Task ChatDecision_RequiresTheCurrentClientAndAnExplicitGrantEvenForLegacyIds()
    {
        var (source, gateway, decisions) = Create(scopes: []);
        var stale = FakeGateway.Create();
        stale.Scopes = ["operator.approvals"];

        await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "legacy-slug", null, "deny"));
        await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)stale, "legacy-slug", null, "deny"));
        Assert.Empty(gateway.Resolves);
        Assert.Empty(stale.Resolves);
    }

    [Fact]
    public async Task ChatDecision_ForAKnownApprovalUsesTheInboxValidation()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.Raise("PendingApprovalRequested", Approval("p1", GatewayApprovalKind.Plugin));

        await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "p1", null, "deny"));
        gateway.Epoch = 8;
        await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", null, "deny"));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ChatDecision_RefusesIdsTheOwnerDoesNotTrack()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1", expiresIn: TimeSpan.FromSeconds(5)));

        // Untracked: never seen, expired, or already resolved elsewhere.
        var unknown = await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "legacy-slug", null, "deny"));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, unknown.Outcome);
        _now = Start.AddSeconds(6);
        await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", null, "deny"));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ChatDecision_ForASlugBannerSendsTheCorrelatedCanonicalId()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("approval-canonical-id"));

        await decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "approval", "approval-canonical-id", "deny");

        Assert.Equal([(GatewayApprovalKind.Exec, "approval-canonical-id", "deny", 7L)], gateway.Resolves);
    }

    [Fact]
    public void CanDecide_FollowsTheNewHandshakeScopesOnReconnect()
    {
        var (source, gateway, _) = Create();
        Assert.True(source.CanDecideApprovals);

        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Scopes = ["operator.read"];
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);

        Assert.False(source.CanDecideApprovals);
    }

    [Fact]
    public async Task ChatBridge_SendsBannerDecisionsThroughTheDecisionOwner()
    {
        using var identity = new OpenClaw.TestSupport.TempDirectory();
        using var client = new OpenClawGatewayClient(
            "ws://127.0.0.1:9/", "token", NullLogger.Instance, identityPath: identity.Path,
            ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        (IOperatorGatewayClient Client, string Id, string? Alternate, string Decision)? call = null;
        using var bridge = new OpenClawTray.Chat.GatewayClientChatBridge(client, resolveExecApproval: (owner, id, alternate, decision) =>
        {
            call = (owner, id, alternate, decision);
            return Task.CompletedTask;
        });

        await ((OpenClawTray.Chat.IChatGatewayBridge)bridge).ResolveExecApprovalAsync("slug", "a1", "deny");

        Assert.Equal(((IOperatorGatewayClient)client, "slug", "a1", "deny"), call);
    }

    private static System.Text.Json.JsonElement ApprovalPayload(string id) =>
        System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id, request = new
            {
                title = "Review operation", description = "Fixture operation", command = "echo hi",
                proposalHash = "fixture-proposal", allowedDecisions = new[] { "allow-once", "deny" },
            },
            createdAtMs = Start.ToUnixTimeMilliseconds(), expiresAtMs = Start.AddHours(1).ToUnixTimeMilliseconds(),
        });

    private static GatewayPendingApproval ParsedApproval(string id, GatewayApprovalKind kind)
    {
        Assert.True(GatewayPendingApprovalParser.TryParse(kind, ApprovalPayload(id), out var approval));
        return approval;
    }

    [Theory]
    [InlineData("proposalHash")]
    [InlineData("scope-command")]
    [InlineData("scope-visibility")]
    public async Task ConsentFieldChanges_RejectOldRowsAndToastTokensWithoutExtendingTheDeadline(string field)
    {
        var (source, gateway, decisions) = Create();
        var original = ParsedConsentApproval(field, changed: false);
        gateway.Raise("PendingApprovalRequested", original);
        var feed = new InboxFeed(new AppNotificationService(), source, decisions);
        var displayed = Assert.Single(feed.Snapshot.Entries).GatewayItem!;
        var originalDeadline = source.Approvals.Pending["a1"].Deadline;
        var token = decisions.IssueToastToken(original, ["deny"]);
        _now = Start.AddMinutes(1);
        gateway.Raise("PendingApprovalRequested", ParsedConsentApproval(field, changed: true));

        Assert.Equal(originalDeadline, source.Approvals.Pending["a1"].Deadline);
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await feed.DecideApprovalAsync(displayed, "deny"));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", token));
        Assert.Empty(gateway.Resolves);
    }

    [Theory]
    [InlineData("proposalHash")]
    [InlineData("scope-command")]
    [InlineData("scope-visibility")]
    public async Task ConsentFieldChanges_AreRecheckedAtFinalAdmission(string field)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", ParsedConsentApproval(field, changed: false));
        var observation = Observation(source, "a1");
        gateway.BeforeResolve = () => gateway.Raise("PendingApprovalRequested", ParsedConsentApproval(field, changed: true));

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "deny", observation));
        Assert.Empty(gateway.Resolves);
    }

    [Theory]
    [InlineData("proposalHash", false)]
    [InlineData("proposalHash", true)]
    [InlineData("scope-command", false)]
    [InlineData("scope-command", true)]
    [InlineData("scope-visibility", false)]
    [InlineData("scope-visibility", true)]
    public async Task ConsentFieldReplay_PreservesIdenticalObservationsButFencesOldRetirement(string field, bool changed)
    {
        var (source, gateway, decisions) = Create();
        var original = ParsedConsentApproval(field, changed: false);
        gateway.Raise("PendingApprovalRequested", original);
        var originalObservation = Observation(source, "a1");
        var token = decisions.IssueToastToken(original, ["deny"]);
        // Independently parsed identical content keeps an already-issued token usable.
        gateway.Raise("PendingApprovalRequested", ParsedConsentApproval(field, changed: false));
        Assert.Same(originalObservation, Observation(source, "a1"));
        var reply = new TaskCompletionSource();
        gateway.ResolveResult = () => reply.Task;
        var sending = decisions.DecideFromToastAsync("a1", "deny", token);
        Assert.Single(gateway.Resolves);
        gateway.Raise("PendingApprovalRequested", ParsedConsentApproval(field, changed));
        var currentObservation = Observation(source, "a1");
        reply.SetResult();

        Assert.Equal(ApprovalDecisionOutcome.Sent, await sending);
        if (changed)
        {
            Assert.NotSame(originalObservation, currentObservation);
            Assert.Same(currentObservation, Observation(source, "a1"));
        }
        else
            Assert.Empty(source.Items);
    }

    private static GatewayPendingApproval ParsedConsentApproval(string field, bool changed)
    {
        var kind = field == "proposalHash" ? GatewayApprovalKind.SystemAgent : GatewayApprovalKind.Exec;
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = "a1", createdAtMs = Start.ToUnixTimeMilliseconds(), expiresAtMs = Start.AddHours(1).ToUnixTimeMilliseconds(),
            request = new
            {
                command = "echo display", title = "Review operation", description = "Review the proposed operation",
                proposalHash = new string(field == "proposalHash" && changed ? 'b' : 'a', 64),
                allowedDecisions = new[] { "allow-once", "deny" },
                scope = field == "scope-command"
                    ? (object)new { kind = "standing-grant", automation = "fixture", command = changed ? "  echo scoped\n" : "echo scoped" }
                    : new { kind = "external-post", target = "fixture target", visibility = field == "scope-visibility" && changed ? "public" : "restricted" },
            },
        });
        Assert.True(GatewayPendingApprovalParser.TryParse(kind, payload, out var approval));
        return approval;
    }

    private static object? Observation(InboxGatewaySource source, string approvalId) =>
        source.Approvals.Pending.TryGetValue(approvalId, out var pending) ? pending : null;

    private static readonly string[] ToastOffers = [GatewayApprovalDecisions.AllowOnce, GatewayApprovalDecisions.Deny];

    private (InboxGatewaySource Source, FakeGateway Gateway, OperatorApprovalDecisions Decisions) Create(IReadOnlyList<string>? scopes = null)
    {
        var source = new InboxGatewaySource(new RecordingUiDispatcher(), () => _now, useTimer: false);
        var gateway = FakeGateway.Create();
        gateway.Scopes = scopes ?? ["operator.approvals"];
        source.AttachClient((IOperatorGatewayClient)gateway);
        return (source, gateway, new OperatorApprovalDecisions(source, () => _now));
    }

    internal static GatewayPendingApproval Approval(string id, GatewayApprovalKind kind = GatewayApprovalKind.Exec, TimeSpan? expiresIn = null) =>
        new(id, kind, "echo hi", Start.ToUnixTimeMilliseconds(), Start.Add(expiresIn ?? TimeSpan.FromHours(1)).ToUnixTimeMilliseconds())
        {
            Title = kind == GatewayApprovalKind.Exec ? null : "Publish",
            SessionKey = "agent:main:main",
        };
}
