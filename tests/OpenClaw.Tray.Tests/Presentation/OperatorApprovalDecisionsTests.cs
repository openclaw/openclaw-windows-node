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
public sealed class OperatorApprovalDecisionsTests
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

        var outcome = await decisions.DecideAsync("a1", GatewayApprovalDecisions.AllowOnce);

        Assert.Equal(ApprovalDecisionOutcome.Sent, outcome);
        Assert.Equal([(kind, "a1", "allow-once", 7L)], gateway.Resolves);
        Assert.Empty(source.Items);
    }

    [Fact]
    public async Task DecideAsync_UsesTheLocalDeadlineNotTheGatewayClock()
    {
        var (_, gateway, decisions) = Create();
        // The Gateway's clock runs a day behind: its expiresAtMs is already past on this PC.
        var skewed = Start.AddDays(-1);
        gateway.Raise("PendingApprovalRequested", new GatewayPendingApproval(
            "a1", GatewayApprovalKind.Exec, "echo hi", skewed.ToUnixTimeMilliseconds(), skewed.AddMinutes(2).ToUnixTimeMilliseconds()));

        _now = Start.AddMinutes(1);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny"));
    }

    [Theory]
    [InlineData("echo a\u00A0b")]
    [InlineData("")]
    public async Task DecideAsync_RefusesAllowWhenTheRowCannotShowTheWholeCommand(string command)
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { Command = command });

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-once"));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-always"));
        Assert.Empty(gateway.Resolves);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny"));
    }

    [Fact]
    public async Task DecideAsync_RejectsDecisionsTheApprovalDoesNotOffer()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { AllowedDecisions = ["deny"] });

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-once"));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "approve"));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_WithoutApprovalScope_ShowsNoDecisionsAndSendsNothing()
    {
        var (source, gateway, decisions) = Create(scopes: ["operator.read"]);
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.False(Assert.Single(source.Items).CanDecide);
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "deny"));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_IgnoresUnknownExpiredAndOtherConnectionApprovals()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("other", "deny"));

        gateway.Epoch = 8;
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "deny"));

        gateway.Epoch = 7;
        _now = Start.AddHours(2);
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "deny"));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task DecideAsync_StaleRejectionRemovesTheRowLikeASuccess()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new GatewayApprovalStaleException("approval already resolved"));

        Assert.Equal(ApprovalDecisionOutcome.AlreadyResolved, await decisions.DecideAsync("a1", "deny"));
        Assert.Empty(source.Items);
    }

    [Fact]
    public async Task DecideAsync_FailureKeepsTheRowForRetry()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());

        Assert.Equal(ApprovalDecisionOutcome.Failed, await decisions.DecideAsync("a1", "deny"));
        Assert.Single(source.Items);

        gateway.ResolveResult = () => Task.CompletedTask;
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny"));
    }

    [Fact]
    public async Task DecideAsync_SendsOneDecisionAtATimePerApproval()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var reply = new TaskCompletionSource();
        gateway.ResolveResult = () => reply.Task;

        var first = decisions.DecideAsync("a1", "deny");
        Assert.Equal(ApprovalDecisionOutcome.InFlight, await decisions.DecideAsync("a1", "allow-once"));
        reply.SetResult();

        Assert.Equal(ApprovalDecisionOutcome.Sent, await first);
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_IsOneShotAndBoundToItsApproval()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.Raise("PendingApprovalRequested", Approval("a2"));

        var token = decisions.IssueToastToken(Approval("a1"), ToastOffers)!;
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a2", "deny", token));
        // The mismatched use consumed the token.
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", token));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", "forged"));
        Assert.Empty(gateway.Resolves);

        var fresh = decisions.IssueToastToken(Approval("a1"), ToastOffers)!;
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideFromToastAsync("a1", "deny", fresh));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", fresh));
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_FromAnEarlierConnectionDecidesNothing()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var token = decisions.IssueToastToken(Approval("a1"), ToastOffers)!;

        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Epoch = 8;
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "allow-once", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_CannotSendADecisionTheToastDidNotOffer()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        var token = decisions.IssueToastToken(Approval("a1"), ToastOffers)!;
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideFromToastAsync("a1", "allow-always", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_StillRevalidatesTheDecision()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { AllowedDecisions = ["deny"] });
        var token = decisions.IssueToastToken(Approval("a1"), ToastOffers)!;

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

    [Fact]
    public async Task DecideAsync_EmptyScopeListIsNotAnApprovalGrant()
    {
        var (source, gateway, decisions) = Create(scopes: []);
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        Assert.False(Assert.Single(source.Items).CanDecide);
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "deny"));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task Retirement_FromAnEarlierConnectionLeavesTheReobservedApproval()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var reply = new TaskCompletionSource();
        gateway.ResolveResult = () => reply.Task;
        var sending = decisions.DecideAsync("a1", "deny");

        // The same client reconnects and observes the approval again before the reply lands.
        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Epoch = 8;
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var token = decisions.IssueToastToken(Approval("a1"), ToastOffers)!;
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
        var (_, gateway, decisions) = Create(scopes: []);
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
        var (_, gateway, decisions) = Create();
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
        var (_, gateway, decisions) = Create();
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
        var (_, gateway, decisions) = Create();
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
