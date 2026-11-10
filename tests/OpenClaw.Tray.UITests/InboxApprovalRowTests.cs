// Native row-model proof: build the actual Inbox row and invoke its navigation command.
// No Gateway, browser, real settings, or approval decision is involved.
using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.ViewModels;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class InboxApprovalRowTests(UIThreadFixture ui)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(GatewayApprovalKind.Plugin, "https://untrusted.example/approve?token=not-a-token")]
    [InlineData(GatewayApprovalKind.SystemAgent, "workspace:settings")]
    [InlineData(GatewayApprovalKind.Plugin, "chat:agent:other:main")]
    public async Task NonExecRowOpensDashboardHomeNeverTheSuppliedSessionOrUrl(GatewayApprovalKind kind, string session)
    {
        await ui.RunOnUIAsync(() =>
        {
            var row = Row(Approval(kind) with { SessionKey = session });
            var routes = new List<string>();
            var dashboardCalls = 0;

            row.OpenAction(routes.Add, () => dashboardCalls++);

            Assert.Equal(1, dashboardCalls);
            Assert.Empty(routes);
            Assert.True(row.OpensDashboard);
            Assert.Null(row.ActionRoute);
            Assert.Equal(Visibility.Visible, row.ActionVisibility);
            Assert.Equal(LocalizationHelper.GetString("Menu_OpenDashboard"), row.ActionLabel);
        });
    }

    [Theory]
    [InlineData(null, "chat")]
    [InlineData("agent:main:review", "chat:agent%3Amain%3Areview")]
    public async Task ExecRowRetainsNativeChatNavigation(string? session, string expectedRoute)
    {
        await ui.RunOnUIAsync(() =>
        {
            var row = Row(Approval(GatewayApprovalKind.Exec) with { SessionKey = session });
            var routes = new List<string>();
            var dashboardCalls = 0;

            row.OpenAction(routes.Add, () => dashboardCalls++);

            Assert.Equal([expectedRoute], routes);
            Assert.Equal(0, dashboardCalls);
            Assert.False(row.OpensDashboard);
            Assert.Equal(LocalizationHelper.GetString("Inbox_OpenChat"), row.ActionLabel);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Scope: entire workspace")]
    public async Task ExpandedPluginDetailsKeepFullTitleAsSafePlainText(string? description)
    {
        await ui.RunOnUIAsync(() =>
        {
            var prefix = new string('x', 200);
            var fullTitle = prefix + "\n<Allow>delete all files</Allow>\u202EEND";
            var approval = Approval(GatewayApprovalKind.Plugin) with
            {
                Title = fullTitle,
                Command = fullTitle,
                Description = description,
            };
            var row = Row(approval);
            row.IsExpanded = true;

            var expected = prefix + "\n<Allow>delete all files</Allow>\\u{202E}END";
            if (!string.IsNullOrEmpty(description))
                expected += "\n\nScope: entire workspace";
            Assert.Equal(expected, row.Details);
            Assert.Equal(Visibility.Visible, row.DetailsVisibility);
            Assert.Equal(Visibility.Collapsed, row.CommandVisibility);
            Assert.DoesNotContain('\u202E', row.Details);
            Assert.True(row.IsExpanded);
        });
    }

    private static GatewayPendingApproval Approval(GatewayApprovalKind kind) =>
        new("approval", kind, kind == GatewayApprovalKind.Exec ? "dir" : "Review plugin action", Now.ToUnixTimeMilliseconds(), Now.AddMinutes(1).ToUnixTimeMilliseconds())
        { Title = kind == GatewayApprovalKind.Exec ? null : "Review plugin action" };

    private static InboxEntry Entry(GatewayPendingApproval approval) => Assert.Single(InboxProjection.Build(
        [], [InboxGatewayItems.FromApproval(approval, Now.AddMinutes(1))]).Entries);

    private static InboxItemViewModel Row(GatewayPendingApproval approval) => InboxItemViewModel.From(Entry(approval), Now);
}
