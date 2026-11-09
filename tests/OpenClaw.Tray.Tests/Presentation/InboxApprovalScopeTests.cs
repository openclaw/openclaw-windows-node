// Observable consent text and toast payloads, independent of the production formatter.
// Run with --filter FullyQualifiedName~InboxApprovalScopeTests.
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class InboxApprovalScopeTests
{
    [Fact]
    public void StandingGrant_ShowsExactCommandBeyondThePreviewIncludingCriticalSuffix()
    {
        var command = "  echo safe\r\n\t" + new string('x', 300) + "\u202E\rDELETE ALL";
        var scope = new GatewayApprovalScope(GatewayApprovalScopeKind.StandingGrant)
        { Automation = "nightly", Command = command, ExpiresInDays = 3 };
        var text = InboxGatewayItems.ScopeLabel(scope, Format);
        Assert.Equal("Allows nightly for 3 days\nCommand:\n  echo safe\n\t" + new string('x', 300) + "\\u{202E}\\u{000D}DELETE ALL", text);
        Assert.DoesNotContain("…", text);
    }

    [Theory]
    [InlineData("public", "Posts to newsroom\nVisibility: public")]
    [InlineData("restricted", "Posts to newsroom\nVisibility: restricted")]
    public void ExternalPost_ShowsItsActualAudience(string visibility, string expected) =>
        Assert.Equal(expected, InboxGatewayItems.ScopeLabel(new(GatewayApprovalScopeKind.ExternalPost)
        { Target = "newsroom", Visibility = visibility }, Format));

    [Theory]
    [InlineData(GatewayApprovalScopeKind.StandingGrant)]
    [InlineData(GatewayApprovalScopeKind.MessageSend)]
    [InlineData(GatewayApprovalScopeKind.Payment)]
    [InlineData(GatewayApprovalScopeKind.ExternalPost)]
    public void ScopeTextAndToastNeverExposeSyntheticSecrets(GatewayApprovalScopeKind kind)
    {
        const string text = "Authorization: Bearer s3cret42";
        var scope = new GatewayApprovalScope(kind)
        { Automation = text, Command = text, Target = text, Amount = text, Currency = text, Visibility = text };
        var rendered = InboxGatewayItems.ScopeLabel(scope, Format);
        Assert.DoesNotContain("s3cret42", rendered);
        Assert.Contains("***", rendered);
        var approval = Approval(GatewayApprovalKind.Plugin) with { Scope = scope };
        var toast = Assert.IsType<ApprovalToastPlan>(ApprovalToastPlan.Build(approval, "one-shot-token", Format, true, DateTimeOffset.MaxValue));
        Assert.Contains(rendered, toast.Lines);
        Assert.DoesNotContain("allow-once", ApprovalToastPlan.Decisions(approval, true));
        Assert.DoesNotContain("s3cret42", string.Join("\n", toast.Lines));
    }

    [Theory]
    [InlineData(GatewayApprovalKind.Plugin, true)]
    [InlineData(GatewayApprovalKind.Plugin, false)]
    [InlineData(GatewayApprovalKind.SystemAgent, true)]
    [InlineData(GatewayApprovalKind.SystemAgent, false)]
    public void NonExecToastRedactsTitleAndCommandFallback(GatewayApprovalKind kind, bool titlePresent)
    {
        const string text = "Authori\u200Bzation: Bearer s3cret42";
        var approval = Approval(kind) with { Command = text, Title = titlePresent ? text : null };
        var toast = Assert.IsType<ApprovalToastPlan>(ApprovalToastPlan.Build(approval, "one-shot-token", Format, true, DateTimeOffset.MaxValue));
        Assert.DoesNotContain("s3cret42", toast.Lines[0]);
        Assert.Contains("***", toast.Lines[0]);
        Assert.DoesNotContain("allow-once", ApprovalToastPlan.Decisions(approval, true));
        var arguments = toast.Buttons.SelectMany(button => button.Arguments).Concat(toast.LaunchArguments);
        Assert.DoesNotContain(arguments, entry => entry.Value.Contains("s3cret42", StringComparison.Ordinal));
    }

    [Fact]
    public void ScopedExecRequiresInboxButSimpleLocalExecStillOffersAllow()
    {
        var approval = Approval(GatewayApprovalKind.Exec);
        Assert.Equal(["allow-once", "deny"], ApprovalToastPlan.Decisions(approval, true));
        Assert.Equal(["deny"], ApprovalToastPlan.Decisions(approval with
        { Scope = new(GatewayApprovalScopeKind.ExternalPost) { Target = "newsroom", Visibility = "public" } }, true));
        var authenticated = approval with { Command = "curl https://x.test/?token=s3cret42" };
        var toast = Assert.IsType<ApprovalToastPlan>(ApprovalToastPlan.Build(authenticated, "one-shot-token", Format, true, DateTimeOffset.MaxValue));
        Assert.Equal("curl https://x.test/?token=***", toast.Lines[0]);
        Assert.Equal(["allow-once", "deny"], ApprovalToastPlan.Decisions(authenticated, true));
    }

    private static GatewayPendingApproval Approval(GatewayApprovalKind kind) =>
        new("fixture", kind, "echo hello", 1, 2) { Title = kind == GatewayApprovalKind.Exec ? null : "Publish" };

    private static string Format(string key) => key switch
    {
        "Inbox_ApprovalScopeStandingGrantFormat" => "Allows {0}",
        "Inbox_ApprovalScopeStandingGrantDaysFormat" => "Allows {0} for {1} days",
        "Inbox_ApprovalScopeCommandFormat" => "Command:\n{0}",
        "Inbox_ApprovalScopeMessageSendFormat" => "Sends to {0} recipients at {1}",
        "Inbox_ApprovalScopePaymentFormat" => "Pays {0} {1} to {2}",
        "Inbox_ApprovalScopeExternalPostFormat" => "Posts to {0}",
        "Inbox_ApprovalScopeVisibilityFormat" => "Visibility: {0}",
        _ => key,
    };
}
