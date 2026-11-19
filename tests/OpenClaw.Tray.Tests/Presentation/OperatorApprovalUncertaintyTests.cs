// Loss-of-ack recovery through the real shared decision/source owners, with only transport inputs controlled.
// Run OperatorApprovalDecisionsTests.Uncertainty_*; no test asks the double to decide whether retry is safe.
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using static OpenClaw.Tray.Tests.Presentation.InboxGatewaySourceTests;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed partial class OperatorApprovalDecisionsTests
{
    [Fact]
    public async Task Uncertainty_PreAdmissionFailureDoesNotQuarantineOrReadAndExplicitRetryIsAllowed()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.BeforeResolve = () => throw new IOException("refused before admission");
        Assert.Equal(ApprovalDecisionOutcome.Failed, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
        Assert.Empty(gateway.ConnectionReads);
        Assert.False(decisions.IsUncertain("a1"));

        gateway.BeforeResolve = null;
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task Uncertainty_LostAckBlocksRowsToastsChatAndReopenedFeedWithoutResending()
    {
        var (source, gateway, decisions) = Create();
        var approval = Approval("a1");
        gateway.Raise("PendingApprovalRequested", approval);
        var token = decisions.IssueToastToken(approval, ToastOffers)!;
        var otherToken = decisions.IssueToastToken(approval, ToastOffers)!;
        gateway.ResolveResult = () => Task.FromException(new TimeoutException("accepted but ack lost"));

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideFromToastAsync("a1", "deny", token));
        var reopened = new InboxFeed(new AppNotificationService(), source, decisions);
        var row = Assert.Single(reopened.Snapshot.Entries).GatewayItem!;
        Assert.True(row.DecisionUncertain);
        Assert.Equal(Start.AddHours(1), row.ExpiresAt);
        Assert.Same(approval, row.Approval);
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await reopened.DecideApprovalAsync(row, "allow-once"));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", otherToken));
        Assert.Null(decisions.IssueToastToken(approval, ToastOffers));
        var chat = await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "slug", "a1", "deny"));
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, chat.Outcome);
        Assert.Single(gateway.Resolves);
        Assert.Equal([("exec.approval.list", 7L)], gateway.ConnectionReads);
    }

    [Theory]
    [InlineData(GatewayApprovalKind.Exec, "exec.approval.list")]
    [InlineData(GatewayApprovalKind.Plugin, "plugin.approval.list")]
    [InlineData(GatewayApprovalKind.SystemAgent, "openclaw.approval.list")]
    public async Task Uncertainty_CompleteAbsenceRetiresOnlyOriginalObservationWithoutClaimingAcceptance(GatewayApprovalKind kind, string method)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1", kind));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        gateway.ConnectionRead = (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Empty(source.Items);
        Assert.Equal([(method, 7L)], gateway.ConnectionReads);
        Assert.Single(gateway.Resolves);
        Assert.True(decisions.IsUncertain("a1")); // Not a receipt proving which decision ran.
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"approvals\":[],\"hasMore\":true}")]
    [InlineData("{\"approvals\":[{\"id\":\"a1\"}]}")]
    public async Task Uncertainty_MalformedReadNeverProvesAbsenceOrPermitsAnotherDecision(string json)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        gateway.ConnectionRead = (_, _) => Task.FromResult(JsonSerializer.Deserialize<JsonElement>(json));

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.True(Assert.Single(source.Items).DecisionUncertain);
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task Uncertainty_FailedReconciliationKeepsTheOriginalContentAndDeadline()
    {
        var (source, gateway, decisions) = Create();
        var original = Approval("a1");
        gateway.Raise("PendingApprovalRequested", original);
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        gateway.ConnectionRead = (_, _) => Task.FromException<JsonElement>(new IOException("read failed"));
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        var row = Assert.Single(source.Items);
        Assert.Same(original, row.Approval);
        Assert.Equal(Start.AddHours(1), row.ExpiresAt);
        Assert.True(row.DecisionUncertain);
        Assert.Single(gateway.Resolves);
        Assert.Single(gateway.ConnectionReads);
    }

    [Fact]
    public async Task Uncertainty_StillPendingIsNotACompletionBarrierForTheOriginalResolve()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var original = Observation(source, "a1");
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        gateway.ConnectionRead = (_, _) => Task.FromResult(UncertaintyPendingReply("a1"));

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", original));
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        Assert.Same(original, Observation(source, "a1"));
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "allow-once", original));
        Assert.Equal(Start.AddHours(1), Assert.Single(source.Items).ExpiresAt);
        Assert.Single(gateway.Resolves);
        Assert.Single(gateway.ConnectionReads);
    }

    [Fact]
    public async Task Uncertainty_UnavailableListingStaysQuarantinedAndExpiresLocally()
    {
        var (source, gateway, decisions) = Create();
        gateway.Methods = ["exec.approval.resolve"];
        gateway.Raise("PendingApprovalRequested", Approval("a1", expiresIn: TimeSpan.FromMinutes(1)));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Empty(gateway.ConnectionReads);
        Assert.True(Assert.Single(source.Items).DecisionUncertain);

        _now = Start.AddMinutes(2);
        source.Tick();
        Assert.Empty(source.Items);
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task Uncertainty_OldHydrationAbsenceCannotRemoveTheQuarantinedObservation()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        Task<ApprovalDecisionOutcome>? sent = null;
        gateway.Reads["exec.approval.list"] = () =>
        {
            sent = decisions.DecideAsync("a1", "deny", Observation(source, "a1"));
            return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        };
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await sent!);
        Assert.True(Assert.Single(source.Items).DecisionUncertain);
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task Uncertainty_ChangedEventDuringReconciliationFencesTheOldAbsenceReply()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var original = Observation(source, "a1");
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        gateway.ConnectionRead = (_, _) =>
        {
            _now = Start.AddMinutes(1);
            gateway.Raise("PendingApprovalRequested", Approval("a1") with { Command = "changed operation" });
            return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        };

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", original));
        var row = Assert.Single(source.Items);
        Assert.Equal("changed operation", row.Approval!.Command);
        Assert.True(row.DecisionUncertain);
        Assert.Equal(Start.AddHours(1), row.ExpiresAt);
        Assert.NotSame(original, row.ApprovalObservation);
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task Uncertainty_ReconnectDuringReadCannotRetireOrReenableTheNewObservation()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        gateway.ConnectionRead = (_, _) =>
        {
            gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
            gateway.Epoch = 8;
            gateway.Raise("StatusChanged", ConnectionStatus.Connected);
            _now = Start.AddMinutes(1);
            gateway.Raise("PendingApprovalRequested", Approval("a1"));
            return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        };

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        var row = Assert.Single(source.Items);
        Assert.True(row.DecisionUncertain);
        Assert.Equal(Start.AddHours(1), row.ExpiresAt);
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", row.ApprovalObservation));
        Assert.Single(gateway.Resolves);
        Assert.Equal([("exec.approval.list", 7L)], gateway.ConnectionReads);
    }

    [Fact]
    public async Task Uncertainty_LateFailureAfterReplacementNeverReadsTheReplacementClient()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var lostReply = new TaskCompletionSource();
        gateway.ResolveResult = () => lostReply.Task;
        var sending = decisions.DecideAsync("a1", "deny", Observation(source, "a1"));
        var replacement = FakeGateway.Create();
        replacement.Scopes = ["operator.approvals"];
        source.AttachClient((IOperatorGatewayClient)replacement);
        _now = Start.AddMinutes(1);
        replacement.Raise("PendingApprovalRequested", Approval("a1") with { Command = "new observation" });
        lostReply.SetException(new TimeoutException());

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await sending);
        var row = Assert.Single(source.Items);
        Assert.Equal("new observation", row.Approval!.Command);
        Assert.Equal(Start.AddHours(1), row.ExpiresAt);
        Assert.True(row.DecisionUncertain);
        Assert.Empty(gateway.ConnectionReads);
        Assert.Empty(replacement.ConnectionReads);
        Assert.Empty(replacement.Resolves);
    }

    [Fact]
    public async Task Uncertainty_ChatLossOfAckUsesTheSameQuarantineAndReconciliation()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        gateway.ResolveResult = () => Task.FromException(new IOException("ack lost"));
        var error = await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() =>
            decisions.ResolveChatExecApprovalAsync((IOperatorGatewayClient)gateway, "slug", "a1", "deny"));
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, error.Outcome);
        Assert.True(Assert.Single(source.Items).DecisionUncertain);
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Single(gateway.Resolves);
        Assert.Single(gateway.ConnectionReads);
    }

    [Fact]
    public async Task Uncertainty_RemainsObservableWhenTheFactRowDisappearedBeforeTheLostAck()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1"));
        var lostReply = new TaskCompletionSource();
        gateway.ResolveResult = () => lostReply.Task;
        var sending = decisions.DecideAsync("a1", "deny", Observation(source, "a1"));
        gateway.Raise("PendingApprovalResolved", "a1");
        Assert.Empty(source.Items);
        var notified = false;
        source.Changed += (_, _) => notified |= decisions.IsUncertain("a1");
        lostReply.SetException(new TimeoutException());

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await sending);
        Assert.True(notified); // Chat's decision projection must refresh even with an empty Inbox.
        Assert.Empty(source.Items);
        Assert.Empty(gateway.ConnectionReads);
    }

    [Theory]
    [InlineData(GatewayApprovalKind.Exec, false, false)]
    [InlineData(GatewayApprovalKind.Exec, true, false)]
    [InlineData(GatewayApprovalKind.Plugin, false, false)]
    [InlineData(GatewayApprovalKind.Plugin, true, false)]
    [InlineData(GatewayApprovalKind.SystemAgent, false, false)]
    [InlineData(GatewayApprovalKind.SystemAgent, true, false)]
    [InlineData(GatewayApprovalKind.Exec, false, true)]
    [InlineData(GatewayApprovalKind.Plugin, true, true)]
    [InlineData(GatewayApprovalKind.SystemAgent, false, true)]
    public async Task Uncertainty_ReconnectHydrationAloneRestoresTheBlockedRowWithItsOriginalDeadline(
        GatewayApprovalKind kind, bool wrapped, bool expired)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", ParsedApproval("a1", kind));
        var deadline = source.Approvals.Pending["a1"].Deadline;
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Epoch = 8;
        _now = expired ? deadline.AddSeconds(1) : Start.AddMinutes(1);
        var rows = new[] { ApprovalPayload("a1") };
        gateway.Replies[GatewayPendingApprovalParser.ListMethod(kind)] = wrapped
            ? JsonSerializer.SerializeToElement(new { approvals = rows }) : JsonSerializer.SerializeToElement(rows);

        // No requested event is replayed: the current connection inventory is the only producer.
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);

        Assert.True(decisions.IsUncertain("a1"));
        if (expired)
            Assert.Empty(source.Items);
        else
        {
            var row = Assert.Single(source.Items);
            Assert.True(row.DecisionUncertain);
            Assert.Equal(deadline, row.ExpiresAt);
            Assert.Equal(8L, source.Approvals.Pending["a1"].ConnectionEpoch);
            Assert.Null(decisions.IssueToastToken(row.Approval!, ["deny"]));
            var reopened = new InboxFeed(new AppNotificationService(), source, decisions);
            Assert.Equal(ApprovalDecisionOutcome.Uncertain, await reopened.DecideApprovalAsync(row, "deny"));
        }
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task Uncertainty_LateOldInventoryCannotEraseTheHydratedQuarantinedRow()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", ParsedApproval("a1", GatewayApprovalKind.Exec));
        gateway.ResolveResult = () => Task.FromException(new TimeoutException());
        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        var oldReply = new TaskCompletionSource<JsonElement>();
        gateway.Reads["exec.approval.list"] = () => oldReply.Task;
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Epoch = 8;
        gateway.Reads.Remove("exec.approval.list");
        gateway.Replies["exec.approval.list"] = JsonSerializer.SerializeToElement(new[] { ApprovalPayload("a1") });
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        var restored = Assert.Single(source.Items);
        oldReply.SetResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));

        Assert.Same(restored.ApprovalObservation, Assert.Single(source.Items).ApprovalObservation);
        Assert.True(Assert.Single(source.Items).DecisionUncertain);
        Assert.Single(gateway.Resolves);
    }

    private static JsonElement UncertaintyPendingReply(string id) => JsonSerializer.SerializeToElement(new[]
    {
        new { id, request = new { command = "echo hi" }, createdAtMs = Start.ToUnixTimeMilliseconds(), expiresAtMs = Start.AddHours(1).ToUnixTimeMilliseconds() },
    });
}
