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

        var token = decisions.IssueToastToken(Approval("a1"))!;
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a2", "deny", token));
        // The mismatched use consumed the token.
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", token));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", "forged"));
        Assert.Empty(gateway.Resolves);

        var fresh = decisions.IssueToastToken(Approval("a1"))!;
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideFromToastAsync("a1", "deny", fresh));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", fresh));
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_FromAnEarlierConnectionDecidesNothing()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var token = decisions.IssueToastToken(Approval("a1"))!;

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

        var token = decisions.IssueToastToken(Approval("a1"))!;
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideFromToastAsync("a1", "allow-always", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ToastToken_StillRevalidatesTheDecision()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { AllowedDecisions = ["deny"] });
        var token = decisions.IssueToastToken(Approval("a1"))!;

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideFromToastAsync("a1", "allow-once", token));
        Assert.Empty(gateway.Resolves);
    }

    [Fact]
    public async Task ChatDecision_UsesTheSamePathAndClearsTheInboxRow()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));

        await decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", "allow-always");

        Assert.Equal([(GatewayApprovalKind.Exec, "a1", "allow-always", 7L)], gateway.Resolves);
        Assert.Empty(source.Items);
    }

    [Fact]
    public async Task ChatDecision_ThrowsSoTheBannerStaysWhenTheDecisionIsNotOffered()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { Ask = "always" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", "allow-always"));

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
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", "deny"));

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
        var token = decisions.IssueToastToken(Approval("a1"))!;
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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "legacy-slug", "deny"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)stale, "legacy-slug", "deny"));
        Assert.Empty(gateway.Resolves);
        Assert.Empty(stale.Resolves);
    }

    [Fact]
    public async Task ChatDecision_ForAKnownApprovalUsesTheInboxValidation()
    {
        var (_, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.Raise("PendingApprovalRequested", Approval("p1", GatewayApprovalKind.Plugin));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "p1", "deny"));
        gateway.Epoch = 8;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "a1", "deny"));
        Assert.Empty(gateway.Resolves);

        // Agent-stream approvals the Inbox never saw keep the chat banner's own validation.
        await decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "legacy-slug", "deny");
        Assert.Equal([(GatewayApprovalKind.Exec, "legacy-slug", "deny", 8L)], gateway.Resolves);
    }

    [Fact]
    public async Task ChatBridge_SendsBannerDecisionsThroughTheDecisionOwner()
    {
        using var identity = new OpenClaw.TestSupport.TempDirectory();
        using var client = new OpenClawGatewayClient(
            "ws://127.0.0.1:9/", "token", NullLogger.Instance, identityPath: identity.Path,
            ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        (IOperatorGatewayClient Client, string Id, string Decision)? call = null;
        using var bridge = new OpenClawTray.Chat.GatewayClientChatBridge(client, resolveExecApproval: (owner, id, decision) =>
        {
            call = (owner, id, decision);
            return Task.CompletedTask;
        });

        await bridge.ResolveExecApprovalAsync("a1", "deny");

        Assert.Equal(((IOperatorGatewayClient)client, "a1", "deny"), call);
    }

    private (InboxGatewaySource Source, FakeGateway Gateway, OperatorApprovalDecisions Decisions) Create(IReadOnlyList<string>? scopes = null)
    {
        var source = new InboxGatewaySource(new RecordingUiDispatcher(), () => _now, useTimer: false);
        var gateway = FakeGateway.Create();
        gateway.Scopes = scopes ?? ["operator.approvals"];
        source.AttachClient((IOperatorGatewayClient)gateway);
        return (source, gateway, new OperatorApprovalDecisions(source, () => _now));
    }

    internal static GatewayPendingApproval Approval(string id, GatewayApprovalKind kind = GatewayApprovalKind.Exec) =>
        new(id, kind, "echo hi", Start.ToUnixTimeMilliseconds(), Start.AddHours(1).ToUnixTimeMilliseconds())
        {
            Title = kind == GatewayApprovalKind.Exec ? null : "Publish",
            SessionKey = "agent:main:main",
        };
}
