using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Which decisions an operator surface may send for a Gateway approval, and which resolve
/// rejections mean the approval is already gone (upstream APPROVAL_NOT_FOUND / ALREADY_RESOLVED).
/// </summary>
public class GatewayApprovalDecisionsTests
{
    [Fact]
    public void Effective_UsesTheRequestsAllowedDecisionsInPermitFirstOrder()
    {
        var approval = Exec("git push") with { AllowedDecisions = ["deny", "allow-once"] };

        Assert.Equal(["allow-once", "deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void Effective_DefaultsToAllDecisionsAndAskAlwaysOmitsAlwaysAllow()
    {
        Assert.Equal(["allow-once", "allow-always", "deny"], GatewayApprovalDecisions.Effective(Exec("dir")));
        Assert.Equal(["allow-once", "deny"], GatewayApprovalDecisions.Effective(Exec("dir") with { Ask = "always" }));
    }

    [Fact]
    public void Effective_OffersOnlyDenyWhenTheCommandIsMissing()
    {
        var approval = Exec("");

        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void Effective_OffersOnlyDenyForOversizedCommands()
    {
        var approval = Exec(new string('a', 300 * 1024));

        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void Effective_DoesNotApplyCommandReviewToPluginApprovals()
    {
        var plugin = new GatewayPendingApproval("p", GatewayApprovalKind.Plugin, "", 1, 2) { Title = "Publish" };

        Assert.Equal(["allow-once", "allow-always", "deny"], GatewayApprovalDecisions.Effective(plugin));
    }

    [Theory]
    [InlineData(16 * 1024 + 1)]
    [InlineData(256 * 1024 + 1)]
    public void Effective_IncompleteWarningOffersOnlyDeny(int length)
    {
        var approval = Exec("echo hi") with { WarningText = new string('x', length) };

        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void Effective_ConcealedWarningOffersOnlyDeny()
    {
        var approval = Exec("echo hi") with { WarningText = "Authorization: Bearer private-operation-data" };

        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("detail")]
    public void Effective_IncompletePluginDisclosureOffersOnlyDeny(string field)
    {
        var text = new string('x', 16 * 1024 + 1);
        var approval = new GatewayPendingApproval("p", GatewayApprovalKind.Plugin, "Publish", 1, 2)
        {
            Title = field == "title" ? text : "Publish",
            Description = field == "description" ? text : null,
            Detail = field == "detail" ? text : null,
        };

        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Effective_GatewayLimitedPluginDetailOffersOnlyDeny(bool suppressed)
    {
        const string suffix = "\u2026[truncated]";
        var detail = suppressed
            ? "[exec approval warning exceeds display size limit; full text suppressed]"
            : new string('x', 16 * 1024 - suffix.Length) + suffix;
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = "gateway-limited",
            request = new { title = "Review operation", description = "Review all changes", detail },
            createdAtMs = 1,
            expiresAtMs = 2,
        });
        Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Plugin, payload, out var approval));

        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void Effective_CompletePluginDetailAtTheDisplayLimitStillPermits()
    {
        var approval = new GatewayPendingApproval("p", GatewayApprovalKind.Plugin, "", 1, 2)
        {
            Title = "Review operation",
            Detail = new string('x', 16 * 1024),
        };

        Assert.True(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["allow-once", "allow-always", "deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Theory]
    [InlineData("""["allow-session"]""")]
    [InlineData("\"deny\"")]
    [InlineData("{}")]
    public void Effective_StatedUnknownOrMalformedDecisionsOfferNothingInsteadOfDefaults(string allowed)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            $$"""{ "id": "a", "request": { "command": "dir", "allowedDecisions": {{allowed}} }, "createdAtMs": 1, "expiresAtMs": 2 }""");
        Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, doc.RootElement, out var approval));

        Assert.True(approval.AllowedDecisionsSpecified);
        Assert.Empty(GatewayApprovalDecisions.Effective(approval));
        Assert.Equal(["allow-once", "allow-always", "deny"], GatewayApprovalDecisions.Effective(Exec("dir")));
    }

    [Theory]
    [InlineData(new[] { "operator.approvals" }, true)]
    [InlineData(new[] { "operator.read", "operator.admin" }, true)]
    [InlineData(new[] { "operator.read", "operator.write" }, false)]
    [InlineData(new string[0], false)]
    public void HasDecisionGrant_RequiresAnExplicitApprovalOrAdminScope(string[] scopes, bool expected) =>
        Assert.Equal(expected, GatewayApprovalDecisions.HasDecisionGrant(scopes));

    [Theory]
    [InlineData(GatewayApprovalKind.Exec, "exec.approval.resolve")]
    [InlineData(GatewayApprovalKind.Plugin, "plugin.approval.resolve")]
    [InlineData(GatewayApprovalKind.SystemAgent, "approval.resolve")]
    public void ResolveMethod_IsKindSpecific(GatewayApprovalKind kind, string method) =>
        Assert.Equal(method, GatewayApprovalDecisions.ResolveMethod(kind));

    [Theory]
    [InlineData("APPROVAL_NOT_FOUND", null, "nope", true)]
    [InlineData("APPROVAL_ALREADY_RESOLVED", null, "gone", true)]
    [InlineData("INVALID_REQUEST", "APPROVAL_ALREADY_RESOLVED", "approval already resolved", true)]
    [InlineData("INVALID_REQUEST", "APPROVAL_NOT_FOUND", "x", true)]
    [InlineData(null, null, "unknown or expired approval id", true)]
    [InlineData("INVALID_REQUEST", null, "This approval only permits Deny.", false)]
    [InlineData("FORBIDDEN", "MISSING_SCOPE", "missing scope", false)]
    public void IsStaleRejection_MatchesUpstreamStaleApprovalErrors(string? code, string? reason, string message, bool stale) =>
        Assert.Equal(stale, GatewayApprovalDecisions.IsStaleRejection(code, reason, message));

    private static GatewayPendingApproval Exec(string command) =>
        new("a", GatewayApprovalKind.Exec, command, 1, 2);
}
