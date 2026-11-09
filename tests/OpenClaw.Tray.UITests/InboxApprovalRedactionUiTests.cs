// Native row presentation and accessible strings use the same secret-safe approval projections.
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.ViewModels;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class InboxApprovalRedactionUiTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(GatewayApprovalKind.Exec)]
    [InlineData(GatewayApprovalKind.Plugin)]
    [InlineData(GatewayApprovalKind.SystemAgent)]
    public Task ApprovalRowTextAndAccessibleNamesDoNotContainRawSecrets(GatewayApprovalKind kind) => ui.RunOnUIAsync(() =>
    {
        const string secretText = "Authorization: Bearer s3cret42";
        var now = DateTimeOffset.UtcNow;
        var approval = new GatewayPendingApproval("a", kind, secretText, now.ToUnixTimeMilliseconds(), now.AddMinutes(1).ToUnixTimeMilliseconds())
        {
            Title = kind == GatewayApprovalKind.Exec ? null : secretText,
            Description = secretText, Host = secretText, Cwd = secretText, WarningText = secretText,
            Scope = new(GatewayApprovalScopeKind.Payment) { Amount = secretText, Currency = secretText, Target = secretText },
        };
        var entry = Assert.Single(InboxProjection.Build([], [InboxGatewayItems.FromApproval(approval, now.AddMinutes(1))]).Entries);
        var row = InboxItemViewModel.From(entry, now);
        foreach (var text in new[] { row.Title, row.State, row.CommandText, row.Details, row.Metadata,
            row.ScopeText, row.WarningText, row.RowAutomationName, row.DismissAutomationName, row.AllowOnceAutomationName, row.AllowAlwaysAutomationName, row.DenyAutomationName, row.ToString() })
            Assert.DoesNotContain("s3cret42", text);
        Assert.Contains("***", row.State);
        Assert.Contains("***", row.Metadata);
        Assert.Contains("***", row.ScopeText);
        Assert.Contains("***", row.WarningText);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, row.AllowOnceVisibility);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, row.AllowAlwaysVisibility);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, row.DenyVisibility);
    });
    [Fact]
    public Task StandingScopeRowRetainsFullCommandInSelectableDisclosure() => ui.RunOnUIAsync(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var command = "  echo first\r\n\t" + new string('x', 300) + "\u202E\rCRITICAL_SUFFIX";
        var approval = new GatewayPendingApproval("scope", GatewayApprovalKind.Exec, "echo preview", now.ToUnixTimeMilliseconds(), now.AddMinutes(2).ToUnixTimeMilliseconds())
        { Scope = new(GatewayApprovalScopeKind.StandingGrant) { Automation = "nightly", Command = command } };
        var entry = Assert.Single(InboxProjection.Build([], [InboxGatewayItems.FromApproval(approval, now.AddMinutes(2))]).Entries);
        var row = InboxItemViewModel.From(entry, now);
        Assert.Contains("Command:\n  echo first\n\t" + new string('x', 300) + "\\u{202E}\\u{000D}CRITICAL_SUFFIX", row.ScopeText);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, row.AllowOnceVisibility);
    });

    [Theory]
    [InlineData("public", "Visibility: public")]
    [InlineData("restricted", "Visibility: restricted")]
    public Task ExternalPostRowDisclosesAudience(string audience, string expected) => ui.RunOnUIAsync(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var approval = new GatewayPendingApproval("scope", GatewayApprovalKind.Plugin, "Publish", now.ToUnixTimeMilliseconds(), now.AddMinutes(2).ToUnixTimeMilliseconds())
        { Title = "Publish", Scope = new(GatewayApprovalScopeKind.ExternalPost) { Target = "newsroom", Visibility = audience } };
        var entry = Assert.Single(InboxProjection.Build([], [InboxGatewayItems.FromApproval(approval, now.AddMinutes(2))]).Entries);
        var row = InboxItemViewModel.From(entry, now);
        Assert.Contains(expected, row.ScopeText);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, row.AllowOnceVisibility);
    });
}
