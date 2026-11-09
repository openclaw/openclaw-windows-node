using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using static OpenClaw.Tray.Tests.Presentation.InboxGatewaySourceTests;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Actionable approval toasts: arguments never carry command text, "Allow once" only appears for
/// a command shown in full, and toasts follow the notification preference, background state and
/// the approval's lifetime.
/// </summary>
public sealed class ApprovalToastTests
{
    private static readonly string[] SafeArgumentKeys = ["action", "approvalId", "decision", "token"];
    private static readonly DateTimeOffset Deadline = new(2026, 11, 6, 12, 5, 0, TimeSpan.Zero);
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_ShortCommand_OffersAllowOnceDenyAndReviewWithoutCommandTextInArguments()
    {
        var approval = OperatorApprovalDecisionsTests.Approval("a1");

        var plan = ApprovalToastPlan.Build(approval, "TOKEN", Key, hostConfirmedLocal: true, Deadline)!;

        Assert.Equal(["Inbox_ApprovalAllowOnce", "Inbox_ApprovalDeny", "Toast_ApprovalReview"], plan.Buttons.Select(button => button.Label));
        Assert.Equal(["echo hi", "Toast_ApprovalRunsOnThisComputer"], plan.Lines);
        var arguments = plan.Buttons.SelectMany(button => button.Arguments).Concat(plan.LaunchArguments).ToList();
        Assert.All(arguments, pair => Assert.Contains(pair.Key, SafeArgumentKeys));
        Assert.DoesNotContain(arguments, pair => pair.Value.Contains("echo", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, pair => pair.Value == GatewayApprovalDecisions.AllowAlways);
        Assert.Equal(
            new Dictionary<string, string> { ["action"] = "approval_decision", ["approvalId"] = "a1", ["decision"] = "deny", ["token"] = "TOKEN" },
            plan.Buttons[1].Arguments);
        Assert.Equal("open_inbox", plan.LaunchArguments["action"]);
    }

    [Fact]
    public void Plan_ScopeOrWorkingDirectory_RequiresFullInboxReview()
    {
        var plain = OperatorApprovalDecisionsTests.Approval("a1");
        var scoped = plain with
        {
            Scope = new GatewayApprovalScope(GatewayApprovalScopeKind.Payment) { Amount = "612", Currency = "USD", Target = "TAP" },
        };
        foreach (var approval in new[] { scoped, plain with { Cwd = @"C:\work" }, plain with { Cwd = @"D:\production" } })
        {
            var plan = ApprovalToastPlan.Build(approval, "TOKEN", Key, hostConfirmedLocal: true, Deadline)!;
            Assert.Equal(["deny"], ApprovalToastPlan.Decisions(approval, hostConfirmedLocal: true));
            Assert.Equal(["Inbox_ApprovalDeny", "Toast_ApprovalReview"], plan.Buttons.Select(button => button.Label));
            Assert.True(plan.Lines.Count <= 2);
        }
    }

    [Theory]
    [InlineData("echo  two  spaces")]
    [InlineData("echo 1234567890 1234567890 1234567890 1234567890 1234567890 1234567890 1234567890")]
    [InlineData("")]
    public void Plan_CommandNotShownInFull_OffersOnlyDenyAndReview(string command)
    {
        var approval = OperatorApprovalDecisionsTests.Approval("a1") with { Command = command };

        var plan = ApprovalToastPlan.Build(approval, "TOKEN", Key, hostConfirmedLocal: true, Deadline)!;

        Assert.Equal(["Inbox_ApprovalDeny", "Toast_ApprovalReview"], plan.Buttons.Select(button => button.Label));
    }

    [Theory]
    [InlineData(GatewayApprovalKind.Plugin)]
    [InlineData(GatewayApprovalKind.SystemAgent)]
    public void Plan_NonExecApprovals_AreReviewedInTheInbox(GatewayApprovalKind kind)
    {
        var plan = ApprovalToastPlan.Build(OperatorApprovalDecisionsTests.Approval("a1", kind), "TOKEN", Key, hostConfirmedLocal: true, Deadline)!;

        Assert.Equal(["Inbox_ApprovalDeny", "Toast_ApprovalReview"], plan.Buttons.Select(button => button.Label));
        Assert.Equal(["Publish"], plan.Lines);
    }

    [Fact]
    public void Plan_WarningOrUnconfirmedHost_OffersOnlyDenyAndReview()
    {
        var warned = OperatorApprovalDecisionsTests.Approval("a1") with { WarningText = "Moves money." };
        var plain = OperatorApprovalDecisionsTests.Approval("a1");

        var warnedPlan = ApprovalToastPlan.Build(warned, "TOKEN", Key, hostConfirmedLocal: true, Deadline)!;
        var remotePlan = ApprovalToastPlan.Build(plain, "TOKEN", Key, hostConfirmedLocal: false, Deadline)!;

        foreach (var plan in new[] { warnedPlan, remotePlan })
        {
            Assert.Equal(["Inbox_ApprovalDeny", "Toast_ApprovalReview"], plan.Buttons.Select(button => button.Label));
            Assert.DoesNotContain("Toast_ApprovalRunsOnThisComputer", plan.Lines);
        }
        Assert.Equal(["deny"], ApprovalToastPlan.Decisions(warned, hostConfirmedLocal: true));
        Assert.Equal(["deny"], ApprovalToastPlan.Decisions(plain, hostConfirmedLocal: false));
    }

    [Fact]
    public async Task Presenter_RemoteHostToastTokenCannotAllow()
    {
        var harness = Create(hostLocal: false);
        harness.Gateway.Raise("PendingApprovalRequested", OperatorApprovalDecisionsTests.Approval("a1"));
        var deny = Assert.Single(harness.Shown).Plan.Buttons.Single(button => button.Label == "Inbox_ApprovalDeny").Arguments;

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed,
            await harness.Decisions.DecideFromToastAsync("a1", "allow-once", deny["token"]));
        Assert.Empty(harness.Gateway.Resolves);
    }

    [Fact]
    public void Plan_WithoutAToastDecision_IsNotShown()
    {
        var approval = OperatorApprovalDecisionsTests.Approval("a1") with { Command = "echo  two  spaces", AllowedDecisions = ["allow-once"] };

        Assert.Null(ApprovalToastPlan.Build(approval, "TOKEN", Key, hostConfirmedLocal: true, Deadline));
    }

    [Fact]
    public async Task Presenter_ShowsLiveApprovalsInTheBackgroundAndItsButtonsDecideOnce()
    {
        var harness = Create();

        harness.Gateway.Raise("PendingApprovalRequested", OperatorApprovalDecisionsTests.Approval("a1"));

        var (plan, tag, group) = Assert.Single(harness.Shown);
        Assert.Equal(ApprovalToastPresenter.TagFor("a1"), tag);
        Assert.Equal(ApprovalToastPresenter.ToastGroup, group);
        Assert.True(tag.Length <= 64);
        var deny = plan.Buttons.Single(button => button.Label == "Inbox_ApprovalDeny").Arguments;
        Assert.Equal(ApprovalDecisionOutcome.Sent,
            await harness.Decisions.DecideFromToastAsync(deny["approvalId"], deny["decision"], deny["token"]));
        Assert.Equal([(tag, group)], harness.Withdrawn);
    }

    [Fact]
    public void Presenter_StaysQuietInTheForegroundWhenNotificationsAreOffOrWithoutApprovalAccess()
    {
        var foreground = Create(foreground: true);
        var disabled = Create(enabled: false);
        var readOnly = Create(scopes: ["operator.read"]);

        foreach (var harness in new[] { foreground, disabled, readOnly })
            harness.Gateway.Raise("PendingApprovalRequested", OperatorApprovalDecisionsTests.Approval("a1"));

        Assert.Empty(foreground.Shown);
        Assert.Empty(disabled.Shown);
        Assert.Empty(readOnly.Shown);
    }

    [Fact]
    public async Task Presenter_WithdrawsAndRevokesWhenTheApprovalIsResolvedElsewhere()
    {
        var harness = Create();
        harness.Gateway.Raise("PendingApprovalRequested", OperatorApprovalDecisionsTests.Approval("a1"));
        var deny = Assert.Single(harness.Shown).Plan.Buttons.Single(button => button.Label == "Inbox_ApprovalDeny").Arguments;

        harness.Gateway.Raise("PendingApprovalResolved", "a1");

        Assert.Single(harness.Withdrawn);
        Assert.Equal(ApprovalDecisionOutcome.NotPending,
            await harness.Decisions.DecideFromToastAsync(deny["approvalId"], deny["decision"], deny["token"]));
        Assert.Empty(harness.Gateway.Resolves);
    }

    [Fact]
    public void Presenter_DoesNotToastApprovalsHydratedOnConnect()
    {
        var harness = Create();
        harness.Gateway.Replies["exec.approval.list"] = System.Text.Json.JsonSerializer.SerializeToElement(new[]
        {
            new { id = "listed", request = new { command = "dir" }, createdAtMs = _now.ToUnixTimeMilliseconds(), expiresAtMs = _now.AddMinutes(5).ToUnixTimeMilliseconds() },
        });

        harness.Gateway.Raise("StatusChanged", ConnectionStatus.Connected);

        Assert.Contains("exec.approval.list", harness.Gateway.Requests);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task Presenter_WithdrawsUncertainToastAndDoesNotReissueOnReplay()
    {
        var harness = Create();
        var approval = OperatorApprovalDecisionsTests.Approval("a1");
        harness.Gateway.Raise("PendingApprovalRequested", approval);
        var (plan, tag, group) = Assert.Single(harness.Shown);
        var deny = plan.Buttons.Single(button => button.Label == "Inbox_ApprovalDeny").Arguments;
        harness.Gateway.ResolveResult = () => Task.FromException(new TimeoutException());

        Assert.Equal(ApprovalDecisionOutcome.Uncertain, await harness.Decisions.DecideFromToastAsync(
            "a1", "deny", deny["token"]));
        Assert.Equal([(tag, group)], harness.Withdrawn);
        harness.Gateway.Raise("PendingApprovalRequested", approval);
        Assert.Single(harness.Shown);
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await harness.Decisions.DecideFromToastAsync(
            "a1", "deny", deny["token"]));
        Assert.Single(harness.Gateway.Resolves);
    }

    private Harness Create(bool foreground = false, bool enabled = true, IReadOnlyList<string>? scopes = null, bool hostLocal = true)
    {
        var source = new InboxGatewaySource(new RecordingUiDispatcher(), () => _now, useTimer: false);
        var gateway = FakeGateway.Create();
        gateway.Scopes = scopes ?? ["operator.approvals"];
        source.AttachClient((IOperatorGatewayClient)gateway);
        var decisions = new OperatorApprovalDecisions(source, () => _now);
        var harness = new Harness(gateway, decisions);
        _ = new ApprovalToastPresenter(source, decisions, Key,
            notificationsEnabled: () => enabled,
            isAppForeground: () => foreground,
            isHostConfirmedLocal: _ => hostLocal,
            show: (plan, tag, group) => { harness.Shown.Add((plan, tag, group)); return true; },
            withdraw: (tag, group) => harness.Withdrawn.Add((tag, group)));
        return harness;
    }

    private static string Key(string key) => key;

    private sealed record Harness(FakeGateway Gateway, OperatorApprovalDecisions Decisions)
    {
        public List<(ApprovalToastPlan Plan, string Tag, string Group)> Shown { get; } = [];
        public List<(string Tag, string Group)> Withdrawn { get; } = [];
    }
}
