// The canonical decision owner binds message recipients/audience at row review and final send admission.
// Run with --filter FullyQualifiedName~OperatorApprovalDecisionsTests.MessageScope.
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed partial class OperatorApprovalDecisionsTests
{
    [Fact]
    public async Task MessageScope_EquivalentEventListReplayKeepsObservationAndOriginalDeadline()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", MessageScopeApproval("recipient", changed: false));
        var original = Observation(source, "a1");
        var deadline = source.Approvals.Pending["a1"].Deadline;
        _now = Start.AddMinutes(1);
        gateway.Raise("PendingApprovalRequested", MessageScopeApproval("recipient", changed: false, fromList: true));
        Assert.Same(original, Observation(source, "a1"));
        Assert.Equal(deadline, source.Approvals.Pending["a1"].Deadline);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "allow-once", original));
        Assert.Equal([(GatewayApprovalKind.Exec, "a1", "allow-once", 7L)], gateway.Resolves);
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("case")]
    [InlineData("order")]
    [InlineData("audience")]
    [InlineData("absent-empty")]
    public async Task MessageScope_ChangesInvalidateOldRowAndToastWithoutExtendingDeadline(string field)
    {
        var (source, gateway, decisions) = Create();
        var approval = MessageScopeApproval(field, changed: false);
        gateway.Raise("PendingApprovalRequested", approval);
        var original = Observation(source, "a1");
        var deadline = source.Approvals.Pending["a1"].Deadline;
        var token = decisions.IssueToastToken(approval, ["deny"]);
        Assert.NotNull(token);
        _now = Start.AddMinutes(1);
        gateway.Raise("PendingApprovalRequested", MessageScopeApproval(field, changed: true, fromList: true));
        Assert.NotSame(original, Observation(source, "a1"));
        Assert.Equal(deadline, source.Approvals.Pending["a1"].Deadline);
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "allow-once", original));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideFromToastAsync("a1", "deny", token));
        Assert.Empty(gateway.Resolves);
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("case")]
    [InlineData("order")]
    [InlineData("audience")]
    [InlineData("absent-empty")]
    public async Task MessageScope_ChangedAfterQueueingRefusesPermitAtFinalAdmission(string field)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", MessageScopeApproval(field, changed: false));
        var original = Observation(source, "a1");
        gateway.BeforeResolve = () => gateway.Raise("PendingApprovalRequested", MessageScopeApproval(field, changed: true));
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "allow-once", original));
        Assert.Empty(gateway.Resolves);
        Assert.NotSame(original, Observation(source, "a1"));
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("audience")]
    public async Task MessageScope_OldResolveReplyCannotRetireChangedObservation(string field)
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", MessageScopeApproval(field, changed: false));
        var reply = new TaskCompletionSource();
        gateway.ResolveResult = () => reply.Task;
        var sending = decisions.DecideAsync("a1", "allow-once", Observation(source, "a1"));
        Assert.Single(gateway.Resolves);
        gateway.Raise("PendingApprovalRequested", MessageScopeApproval(field, changed: true));
        var replacement = Observation(source, "a1");
        reply.SetResult();
        Assert.Equal(ApprovalDecisionOutcome.Sent, await sending);
        Assert.Same(replacement, Observation(source, "a1"));
        Assert.Single(source.Items);
        Assert.Single(gateway.Resolves);
    }

    [Fact]
    public async Task MessageScope_ConcealedRecipientNeverReachesPermitRpcButCanBeDenied()
    {
        var (source, gateway, decisions) = Create();
        var approval = MessageScopeApproval("recipient", changed: false);
        gateway.Raise("PendingApprovalRequested", approval with
        { Scope = approval.Scope! with { Recipients = ["Authorization: Bearer s3cret42"] } });
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-always", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Equal([(GatewayApprovalKind.Exec, "a1", "deny", 7L)], gateway.Resolves);
    }

    private static GatewayPendingApproval MessageScopeApproval(string field, bool changed, bool fromList = false)
    {
        var scope = new Dictionary<string, object>
        {
            ["kind"] = "message-send", ["target"] = "email", ["recipientCount"] = 2,
            ["audience"] = field == "audience" && changed ? "external" : "internal",
        };
        if (field != "absent-empty" || changed)
        {
            string[] recipients = field == "absent-empty" ? [] : changed ? field switch
            {
                "recipient" => ["Other@example.test", "second@example.test"],
                "case" => ["reader@example.test", "second@example.test"],
                "order" => ["second@example.test", "Reader@example.test"],
                _ => ["Reader@example.test", "second@example.test"],
            } : ["Reader@example.test", "second@example.test"];
            scope["recipients"] = recipients;
        }
        var payload = JsonSerializer.SerializeToElement(new
        {
            id = "a1", createdAtMs = Start.ToUnixTimeMilliseconds(), expiresAtMs = Start.AddHours(1).ToUnixTimeMilliseconds(),
            request = new { command = "echo preview", scope },
        });
        if (fromList)
            return Assert.Single(GatewayPendingApprovalParser.ParseList(GatewayApprovalKind.Exec, JsonSerializer.SerializeToElement(new[] { payload })));
        Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, payload, out var approval));
        return approval;
    }
}
