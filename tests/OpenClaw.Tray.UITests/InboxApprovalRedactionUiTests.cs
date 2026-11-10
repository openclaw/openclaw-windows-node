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
            row.ScopeText, row.WarningText, row.RowAutomationName, row.DismissAutomationName, row.ToString() })
            Assert.DoesNotContain("s3cret42", text);
        Assert.Contains("***", row.State);
        Assert.Contains("***", row.Metadata);
        Assert.Contains("***", row.ScopeText);
        Assert.Contains("***", row.WarningText);
    });
}
