// The final decision owner refuses permits for undisclosed or concealed consent, not only the row UI.
// Run with --filter FullyQualifiedName~OperatorApprovalDecisionsTests.
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed partial class OperatorApprovalDecisionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayLimitedPluginDetail_RefusesPermitsBeforeRpcButAllowsDeny(bool suppressed)
    {
        const string suffix = "\u2026[truncated]";
        var detail = suppressed
            ? "[exec approval warning exceeds display size limit; full text suppressed]"
            : new string('x', 16 * 1024 - suffix.Length) + suffix;
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("p1", GatewayApprovalKind.Plugin) with
        {
            Title = "Review operation",
            Detail = detail,
        });

        Assert.Equal(ApprovalDecisionOutcome.NotAllowed,
            await decisions.DecideAsync("p1", "allow-once", Observation(source, "p1")));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed,
            await decisions.DecideAsync("p1", "allow-always", Observation(source, "p1")));
        Assert.Empty(gateway.Resolves);
        Assert.Equal(ApprovalDecisionOutcome.Sent,
            await decisions.DecideAsync("p1", "deny", Observation(source, "p1")));
        Assert.Equal([(GatewayApprovalKind.Plugin, "p1", "deny", 7L)], gateway.Resolves);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("oversized")]
    [InlineData("truncated")]
    [InlineData("audience")]
    public async Task UnreviewableScope_RefusesEveryPermitBeforeRpcButAllowsDeny(string scenario)
    {
        var (source, gateway, decisions) = Create();
        var scope = scenario == "audience"
            ? new GatewayApprovalScope(GatewayApprovalScopeKind.ExternalPost) { Target = "newsroom", Visibility = "unknown" }
            : new GatewayApprovalScope(GatewayApprovalScopeKind.StandingGrant)
            {
                Automation = "nightly",
                Command = scenario switch { "secret" => "Authorization: Bearer s3cret42", "oversized" => new string('x', 256 * 1024 + 1), _ => new string('x', 16 * 1024 + 1) },
            };
        gateway.Raise("PendingApprovalRequested", Approval("a1") with { Scope = scope });
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, await decisions.DecideAsync("a1", "allow-always", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "deny", Observation(source, "a1")));
        Assert.Equal([(GatewayApprovalKind.Exec, "a1", "deny", 7L)], gateway.Resolves);
    }

    [Fact]
    public async Task ValidScopedConsent_CanBeAllowedFromInboxButNotFromChat()
    {
        var (source, gateway, decisions) = Create();
        gateway.Raise("PendingApprovalRequested", Approval("a1") with
        { Scope = new(GatewayApprovalScopeKind.StandingGrant) { Automation = "nightly", Command = "echo exact\n\tLAST" } });
        var refused = await Assert.ThrowsAsync<ApprovalDecisionRefusedException>(() => decisions.ResolveChatExecApprovalAsync(
            (IOperatorGatewayClient)gateway, "a1", null, "allow-once"));
        Assert.Equal(ApprovalDecisionOutcome.NotAllowed, refused.Outcome);
        Assert.Empty(gateway.Resolves);
        Assert.Equal(ApprovalDecisionOutcome.Sent, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        Assert.Equal([(GatewayApprovalKind.Exec, "a1", "allow-once", 7L)], gateway.Resolves);
    }

    [Fact]
    public async Task ScopeChangedWhileWaitingForSend_CannotPermitFromAnOldReview()
    {
        var (source, gateway, decisions) = Create();
        var original = Approval("a1") with
        { Scope = new(GatewayApprovalScopeKind.StandingGrant) { Automation = "nightly", Command = "echo safe" } };
        gateway.Raise("PendingApprovalRequested", original);
        gateway.BeforeResolve = () => gateway.Raise("PendingApprovalRequested", original with
        { Scope = original.Scope! with { Command = "Authorization: Bearer s3cret42" } });
        Assert.Equal(ApprovalDecisionOutcome.NotPending, await decisions.DecideAsync("a1", "allow-once", Observation(source, "a1")));
        Assert.Empty(gateway.Resolves);
        Assert.NotNull(Observation(source, "a1"));
    }
}
