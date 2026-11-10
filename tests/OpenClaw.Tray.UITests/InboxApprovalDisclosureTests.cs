// Exercises the production parser-to-row disclosure boundary on the shared WinUI thread.
// Run with dotnet test --filter FullyQualifiedName~InboxApprovalDisclosureTests.
using System.Text.Json;
using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.ViewModels;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

/// <summary>Approval permit controls must retain the complete plugin reviewer detail in the row.</summary>
[Collection(UICollection.Name)]
public sealed class InboxApprovalDisclosureTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ApprovalToast_MaterializesWithTheShippingToolkit(bool scoped, bool hasWorkingDirectory)
    {
        var approval = new GatewayPendingApproval("toast", GatewayApprovalKind.Exec, "echo hi", 1, 2)
        {
            Cwd = hasWorkingDirectory ? @"C:\work" : null,
            Scope = scoped ? new GatewayApprovalScope(GatewayApprovalScopeKind.Payment)
                { Amount = "12", Currency = "USD", Target = "fixture payee" } : null,
        };
        var plan = ApprovalToastPlan.Build(approval, "fixture-token", key => key,
            hostConfirmedLocal: true, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.NotNull(plan);
        var content = ToastService.CreateApprovalContent(plan).GetToastContent().GetContent();

        Assert.Contains("echo hi", content);
        Assert.Contains("Toast_ApprovalReview", content);
        if (scoped || hasWorkingDirectory)
            Assert.DoesNotContain("Inbox_ApprovalAllowOnce", content);
    }

    [Fact]
    public Task QuarantinedApprovalRow_HasNoDecisionControlsAndKeepsItsPersistentExplanation() =>
        ui.RunOnUIAsync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var approval = new GatewayPendingApproval("uncertain", GatewayApprovalKind.Exec, "echo hi",
                now.ToUnixTimeMilliseconds(), now.AddMinutes(5).ToUnixTimeMilliseconds());
            var item = InboxGatewayItems.FromApproval(approval, now.AddMinutes(5)) with { DecisionUncertain = true };
            var entry = Assert.Single(InboxProjection.Build([], [item]).Entries);
            var row = InboxItemViewModel.From(entry, now);
            Assert.Equal(Visibility.Collapsed, row.AllowOnceVisibility);
            Assert.Equal(Visibility.Collapsed, row.AllowAlwaysVisibility);
            Assert.Equal(Visibility.Collapsed, row.DenyVisibility);
            Assert.False(row.DecisionsEnabled);
            Assert.Equal(Visibility.Visible, row.DecisionNoteVisibility);
            Assert.False(string.IsNullOrWhiteSpace(row.DecisionNoteText));
            Assert.Equal("echo hi", row.CommandText);
        });

    [Theory]
    [InlineData(null)]
    [InlineData("Execute the requested SQL")]
    public Task PluginPermitRow_PreservesCompleteReviewerDetail(string? description) =>
        ui.RunOnUIAsync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var detail = "  UPDATE projects SET note = '" + new string('a', 2000) +
                "';\nDELETE FROM drafts WHERE expired = 1;\n";
            var payload = JsonSerializer.SerializeToElement(new
            {
                id = "plugin-disclosure",
                request = new { title = "Run query", description, detail, allowedDecisions = new[] { "allow-once", "deny" } },
                createdAtMs = now.ToUnixTimeMilliseconds(),
                expiresAtMs = now.AddMinutes(5).ToUnixTimeMilliseconds(),
            });
            Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Plugin, payload, out var approval));
            var item = InboxGatewayItems.FromApproval(approval, now.AddMinutes(5));
            var entry = Assert.Single(InboxProjection.Build([], [item]).Entries);

            var row = InboxItemViewModel.From(entry, now);

            Assert.Equal("Run query\n\n" + (description is null ? detail : description + "\n\n" + detail), row.Details);
            Assert.Equal(Visibility.Visible, row.DetailsVisibility);
            Assert.Equal(Visibility.Visible, row.AllowOnceVisibility);
            Assert.True(row.DecisionsEnabled);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task GatewayLimitedPluginDetailRow_OffersOnlyDeny(bool suppressed) =>
        ui.RunOnUIAsync(() =>
        {
            const string suffix = "\u2026[truncated]";
            var detail = suppressed
                ? "[exec approval warning exceeds display size limit; full text suppressed]"
                : new string('x', 16 * 1024 - suffix.Length) + suffix;
            var now = DateTimeOffset.UtcNow;
            var payload = JsonSerializer.SerializeToElement(new
            {
                id = "gateway-limited",
                request = new { title = "Review operation", detail },
                createdAtMs = now.ToUnixTimeMilliseconds(),
                expiresAtMs = now.AddMinutes(5).ToUnixTimeMilliseconds(),
            });
            Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Plugin, payload, out var approval));
            var item = InboxGatewayItems.FromApproval(approval, now.AddMinutes(5));
            var entry = Assert.Single(InboxProjection.Build([], [item]).Entries);

            var row = InboxItemViewModel.From(entry, now);

            Assert.Contains(detail, row.Details);
            Assert.Equal(Visibility.Collapsed, row.AllowOnceVisibility);
            Assert.Equal(Visibility.Collapsed, row.AllowAlwaysVisibility);
            Assert.Equal(Visibility.Visible, row.DenyVisibility);
            Assert.Equal(Visibility.Visible, row.DecisionNoteVisibility);
            Assert.True(row.DecisionsEnabled);
        });
}
