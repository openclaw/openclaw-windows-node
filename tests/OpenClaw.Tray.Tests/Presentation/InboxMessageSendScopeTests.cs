// Independent display and permit contracts for known message-send recipient samples and audiences.
// Run with --filter FullyQualifiedName~InboxMessageSendScopeTests.
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class InboxMessageSendScopeTests
{
    [Fact]
    public void FullRecipientSampleAndAudienceAreSelectableScopeTextNotPreviewContent()
    {
        var last = new string('z', 110) + "@example.test";
        var approval = Approval(new(GatewayApprovalScopeKind.MessageSend)
        {
            Target = "email", RecipientCount = 12,
            Recipients = ["first@example.test", "second@example.test", "third@example.test", "fourth@example.test", last],
            Audience = "external",
        });
        Assert.Equal("Sends to 12 recipients at email\nListed recipients:\nfirst@example.test\nsecond@example.test\nthird@example.test\nfourth@example.test\n" + last + "\nAudience: external",
            InboxGatewayItems.ScopeLabel(approval.Scope!, Text));
        Assert.Equal(["allow-once", "allow-always", "deny"], InboxGatewayItems.InboxDecisions(approval));
        Assert.Equal(["deny"], ApprovalToastPlan.Decisions(approval, hostConfirmedLocal: true));
        Assert.Equal(["deny"], GatewayApprovalDecisions.EffectiveForChat(approval));
    }

    [Theory]
    [InlineData(false, "Sends to 2 recipients at email")]
    [InlineData(true, "Sends to 2 recipients at email\nListed recipients:\nNone listed")]
    public void AbsentAndExplicitlyEmptySamplesDoNotClaimThereAreNoActualRecipients(bool specified, string expected)
    {
        var scope = new GatewayApprovalScope(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 2, Recipients = specified ? [] : null };
        Assert.Equal(expected, InboxGatewayItems.ScopeLabel(scope, Text));
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(Approval(scope)));
    }

    [Theory]
    [InlineData("internal", "Sends to 2 recipients at email\nAudience: internal")]
    [InlineData("external", "Sends to 2 recipients at email\nAudience: external")]
    public void AudienceOnlyUsesExactWireValue(string audience, string expected) =>
        Assert.Equal(expected, InboxGatewayItems.ScopeLabel(new(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 2, Audience = audience }, Text));

    [Fact]
    public void RecipientLineControlsCannotImpersonateAdditionalEntries()
    {
        var scope = new GatewayApprovalScope(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 1, Recipients = ["first\nsecond\t\u202E\uE000@example.test"] };
        Assert.Equal("Sends to 1 recipients at email\nListed recipients:\nfirst\\u{000A}second\\u{0009}\\u{202E}\\u{E000}@example.test",
            InboxGatewayItems.ScopeLabel(scope, Text));
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(Approval(scope)));
    }

    [Theory]
    [InlineData("Authorization: Bearer s3cret42")]
    [InlineData("Autho\u200Brization: Bearer s3cret42")]
    [InlineData("Autho\uE000rization: Bearer s3cret42")]
    public void SecretRecipientIsMaskedInScopeAndToastAndCannotPermit(string recipient)
    {
        var approval = Approval(new(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 1, Recipients = [recipient], Audience = "external" });
        var scopeText = InboxGatewayItems.ScopeLabel(approval.Scope!, Text);
        Assert.DoesNotContain("s3cret42", scopeText);
        Assert.Contains("***", scopeText);
        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], InboxGatewayItems.InboxDecisions(approval));
        var plan = Assert.IsType<ApprovalToastPlan>(ApprovalToastPlan.Build(approval, "fixture-token", Text, true, DateTimeOffset.MaxValue));
        Assert.DoesNotContain("s3cret42", string.Join("\n", plan.Lines));
        Assert.DoesNotContain(plan.Buttons.SelectMany(button => button.Arguments), entry => entry.Value.Contains("s3cret42", StringComparison.Ordinal));
    }

    [Fact]
    public void SafeAuthenticationOnlyRedactionDoesNotBanOtherwiseReviewableConsent()
    {
        var approval = Approval(new(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 1, Recipients = ["https://example.test/?token=s3cret42"], Audience = "internal" });
        Assert.Equal("Sends to 1 recipients at email\nListed recipients:\nhttps://example.test/?token=***\nAudience: internal",
            InboxGatewayItems.ScopeLabel(approval.Scope!, Text));
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(approval));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("count")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("audience")]
    public void DirectModelCannotBypassRecipientBoundsOrAudiencePolicy(string invalid)
    {
        var approval = Approval(new(GatewayApprovalScopeKind.MessageSend)
        {
            Target = "email", RecipientCount = 1,
            Recipients = invalid switch
            {
                "length" => [new string('x', 129)],
                "count" => ["a", "b", "c", "d", "e", "f"],
                "empty" => [""],
                "null" => [null!],
                _ => ["reader@example.test"],
            },
            Audience = invalid == "audience" ? "Authorization: Bearer s3cret42" : "external",
        });
        Assert.False(GatewayApprovalDecisions.CanReviewInFull(approval));
        Assert.Equal(["deny"], InboxGatewayItems.InboxDecisions(approval));
        if (invalid == "audience")
        {
            var text = InboxGatewayItems.ScopeLabel(approval.Scope!, Text);
            Assert.Contains("***", text);
            Assert.DoesNotContain("s3cret42", text);
        }
    }

    private static GatewayPendingApproval Approval(GatewayApprovalScope scope) =>
        new("message", GatewayApprovalKind.Exec, "echo preview", 1, 60001) { Scope = scope };

    private static string Text(string key) => key switch
    {
        "Inbox_ApprovalScopeMessageSendFormat" => "Sends to {0} recipients at {1}",
        "Inbox_ApprovalScopeRecipientsFormat" => "Listed recipients:\n{0}",
        "Inbox_ApprovalScopeNoRecipientsListed" => "None listed",
        "Inbox_ApprovalScopeAudienceFormat" => "Audience: {0}",
        _ => key,
    };
}
