// Protects scope disclosure prerequisites shared by Inbox, chat and final decision admission.
// Run with --filter FullyQualifiedName~GatewayApprovalScopeConsentTests.
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayApprovalScopeConsentTests
{
    [Fact]
    public void StandingGrant_PreservesExactMultilineCommandAndAutomation()
    {
        var approval = Parse("""{"kind":"standing-grant","automation":" nightly ","command":"  echo one\r\n\techo two\u202E\rLAST","expiresInDays":3}""");
        Assert.Equal(" nightly ", approval.Scope!.Automation);
        Assert.Equal("  echo one\r\n\techo two\u202E\rLAST", approval.Scope.Command);
        Assert.True(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["allow-once", "allow-always", "deny"], GatewayApprovalDecisions.Effective(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.EffectiveForChat(approval));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("restricted")]
    public void ExternalPost_PreservesTheExactAudience(string visibility)
    {
        var approval = Parse(JsonSerializer.Serialize(new { kind = "external-post", target = " newsroom ", visibility }));
        Assert.Equal(" newsroom ", approval.Scope!.Target);
        Assert.Equal(visibility, approval.Scope.Visibility);
        Assert.True(GatewayApprovalDecisions.CanReviewInFull(approval));
    }

    public static IEnumerable<object[]> UnreviewableFields()
    {
        foreach (var field in new[] { "automation", "command", "visibility", "target", "amount", "currency", "host", "cwd" })
        foreach (var value in new[] { "Authorization: Bearer s3cret42", "Authori\u200Bzation: Bearer s3cret42", new string('x', 16 * 1024 + 1), new string('x', 256 * 1024 + 1) })
            yield return [field, value];
    }

    [Theory]
    [MemberData(nameof(UnreviewableFields))]
    public void ConcealedOrIncompleteFields_CannotPermit(string field, string value)
    {
        var scope = field switch
        {
            "automation" => new GatewayApprovalScope(GatewayApprovalScopeKind.StandingGrant) { Automation = value, Command = "echo hi" },
            "command" => new GatewayApprovalScope(GatewayApprovalScopeKind.StandingGrant) { Automation = "nightly", Command = value },
            "visibility" => new GatewayApprovalScope(GatewayApprovalScopeKind.ExternalPost) { Target = "newsroom", Visibility = value },
            _ => new GatewayApprovalScope(GatewayApprovalScopeKind.Payment) { Target = field == "target" ? value : "vendor", Amount = field == "amount" ? value : "12", Currency = field == "currency" ? value : "USD" },
        };
        var approval = new GatewayPendingApproval("a", GatewayApprovalKind.Exec, "echo preview", 1, 2)
        { Scope = scope, Host = field == "host" ? value : null, Cwd = field == "cwd" ? value : null };
        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void ReviewSafeAuthenticationRedaction_IsNotAnAutomaticPermitBan()
    {
        var approval = Parse("""{"kind":"standing-grant","automation":"nightly","command":"curl https://example.test/run?token=s3cret42"}""");
        Assert.True(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(approval));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("""{"kind":"unknown","target":"hidden"}""")]
    [InlineData("""{"kind":"standing-grant","automation":"nightly"}""")]
    [InlineData("""{"kind":"standing-grant","automation":"nightly","command":"echo hi","expiresInDays":"forever"}""")]
    [InlineData("""{"kind":"standing-grant","automation":"nightly","command":"echo hi","expiresInDays":1.5}""")]
    [InlineData("""{"kind":"payment","amount":"1","currency":"USD","target":"vendor","extra":"concealed consent"}""")]
    [InlineData("""{"kind":"external-post","target":"newsroom","visibility":"public","visibility":"restricted"}""")]
    [InlineData("""{"kind":"external-post","target":"newsroom","visibility":" public "}""")]
    [InlineData("""{"kind":"message-send","target":"mailbox","recipientCount":1.5}""")]
    public void UnsupportedOrMalformedScope_DoesNotDisappearIntoAnUnscopedPermit(string scope)
    {
        var approval = Parse(scope);
        Assert.NotNull(approval.Scope);
        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.EffectiveForChat(approval));
    }

    [Fact]
    public void UnsupportedWireScopeStillFencesObservationIdentity()
    {
        var first = Parse("""{"kind":"future-scope","target":"first"}""");
        Assert.True(first.HasSameContentAs(Parse("""{"kind":"future-scope","target":"first"}""")));
        Assert.False(first.HasSameContentAs(Parse("""{"kind":"future-scope","target":"second"}""")));
    }

    [Theory]
    [InlineData("cwd")]
    [InlineData("warning")]
    public void ChatCannotPermitConsentItDoesNotRender(string field)
    {
        var approval = new GatewayPendingApproval("a", GatewayApprovalKind.Exec, "echo hi", 1, 2)
        { Cwd = field == "cwd" ? "C:\\workspace" : null, WarningText = field == "warning" ? "Uploads source" : null };
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(approval));
        Assert.Equal(["deny"], GatewayApprovalDecisions.EffectiveForChat(approval));
        Assert.Equal(["allow-once", "allow-always", "deny"], GatewayApprovalDecisions.EffectiveForChat(approval with { Cwd = null, WarningText = null }));
    }

    private static GatewayPendingApproval Parse(string scope)
    {
        using var doc = JsonDocument.Parse($$"""{"id":"a","createdAtMs":1,"expiresAtMs":2,"request":{"command":"echo preview","scope":{{scope}}}}""");
        Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, doc.RootElement, out var approval));
        return approval;
    }
}
