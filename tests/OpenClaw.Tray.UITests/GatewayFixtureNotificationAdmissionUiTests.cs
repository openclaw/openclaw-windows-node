using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "GatewayFixture")]
    public async Task NotificationAction_BrowserRefusalAndSeparateRetryKeepReminderUntilItsOwnActionSucceeds(
        bool settingsBanner)
    {
        const string reminderTitle = "Slack channel needs attention";
        const string actionLabel = "Open Channels";
        const string inboxRoute = "workspace:notifications";
        const string containmentReminder = "Windows containment unavailable: commands run uncontained";
        var sourceRoute = settingsBanner ? "settings" : inboxRoute;
        var sourceMarker = settingsBanner ? "SettingsPageMarker" : "NotificationsPageMarker";

        await WithAppAsync(async run =>
        {
            // The fixture supplies only Gateway health input. The production client and App
            // must create the reminder, and the mounted inbox/banner owns its action click.
            if (settingsBanner)
            {
                // An unsupported host can already have a containment warning in front of Slack.
                // Acknowledge only that known warning through its ordinary inbox action. This
                // neither changes capability checks nor claims that containment is available.
                await run.InvokeAsync("app.navigate", new { page = inboxRoute });
                await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is { Current.IsOffscreen: false } &&
                    FindButtons(run, "Dismiss " + reminderTitle).Count == 1, "Slack reminder present before banner selection");
                var hostWarning = FindButtons(run, "Dismiss " + containmentReminder);
                Assert.InRange(hostWarning.Count, 0, 1);
                if (hostWarning.Count == 1)
                {
                    await CaptureIfRequestedAsync(run, "notification-host-warning-before-acknowledgment.png");
                    Invoke(hostWarning[0]);
                    await WaitUiAsync(run, () => FindButtons(run, "Dismiss " + containmentReminder).Count == 0,
                        "known host warning acknowledged through the inbox");
                    Assert.Single(FindButtons(run, "Dismiss " + reminderTitle));
                }
            }
            await run.InvokeAsync("app.navigate", new { page = sourceRoute });
            if (!settingsBanner)
                await ExpandNotificationReminderAsync(run, reminderTitle, actionLabel);
            await WaitUiAsync(run, () =>
                FindById(run, sourceMarker) is { Current.IsOffscreen: false } &&
                FindButtons(run, actionLabel).Count == 1, "actionable channel reminder on the selected surface");
            AssertDashboardLaunches(run);
            await CaptureIfRequestedAsync(run, "notification-before-action.png");

            // Reuse the existing browser boundary: no real browser is opened. Its false
            // result must flow back through App and WindowManager to the clicked reminder.
            ArmSimulatedBrowserFailure(run);
            Invoke(Assert.Single(FindButtons(run, actionLabel)));
            await WaitUiAsync(run, () => FindText(run, DashboardFailureMessage) is { Current.IsOffscreen: false } &&
                FindButtons(run, RetryButtonName).Count == 1, "refused browser launch showing recovery");
            AssertDashboardLaunches(run, DashboardOutcome.SimulatedFailure);
            await CaptureIfRequestedAsync(run, "notification-browser-refused.png");

            // Retry belongs to the failure dialog, not the original notification action.
            // Even successful recovery must not retroactively dismiss that reminder.
            Invoke(Assert.Single(FindButtons(run, RetryButtonName)));
            await WaitForDashboardLaunchesAsync(run, 2, "separate dialog retry accepted by the browser boundary");
            await WaitUiAsync(run, () => FindButtons(run, RetryButtonName).Count == 0, "retry dialog closed");
            AssertDashboardLaunches(run, DashboardOutcome.SimulatedFailure, DashboardOutcome.Opened);

            await run.InvokeAsync("app.navigate", new { page = inboxRoute });
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is { Current.IsOffscreen: false } &&
                FindButtons(run, "Dismiss " + reminderTitle).Count == 1,
                "original reminder retained in the inbox after refusal and separate recovery");
            await ExpandNotificationReminderAsync(run, reminderTitle, actionLabel);
            Assert.Single(FindButtons(run, actionLabel));
            await CaptureIfRequestedAsync(run, "notification-retained-after-retry.png");

            await run.InvokeAsync("app.navigate", new { page = sourceRoute });
            if (!settingsBanner)
                await ExpandNotificationReminderAsync(run, reminderTitle, actionLabel);
            await WaitUiAsync(run, () => FindById(run, sourceMarker) is { Current.IsOffscreen: false } &&
                FindButtons(run, actionLabel).Count == 1, "original action available again on the selected surface");
            Invoke(Assert.Single(FindButtons(run, actionLabel)));
            await WaitForDashboardLaunchesAsync(run, 3, "original notification action accepted by the browser boundary");
            await WaitUiAsync(run, () => FindButtons(run, actionLabel).Count == 0,
                "accepted notification removed from its source surface");
            AssertDashboardLaunches(run, DashboardOutcome.SimulatedFailure, DashboardOutcome.Opened, DashboardOutcome.Opened);

            await run.InvokeAsync("app.navigate", new { page = inboxRoute });
            // Other reminders, including host capability warnings, must not make this test
            // depend on an empty inbox. Only the clicked reminder is meant to disappear.
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is { Current.IsOffscreen: false } &&
                FindButtons(run, "Dismiss " + reminderTitle).Count == 0,
                "original reminder absent from the inbox after its own action succeeded");
            Assert.Empty(FindButtons(run, "Dismiss " + reminderTitle));
            Assert.Empty(FindButtons(run, actionLabel));
            await CaptureIfRequestedAsync(run, "notification-dismissed-after-admission.png");
        }, scenario: GatewayScenario.CreateChannelIssue("Fixture channel authorization failed."));
    }

    // The page hosts the same expandable InboxView as the bell. Observe its actual row,
    // expand once, then require the enabled native action before the caller invokes it.
    private static async Task ExpandNotificationReminderAsync(GatewayFixtureRun run, string title, string actionLabel)
    {
        AutomationElement? row = null;
        await WaitUiAsync(run, () =>
        {
            row = FindButtons(run, "Dismiss " + title).SingleOrDefault();
            return row is not null;
        }, "notification reminder present");
        var reminderRow = row!;
        while (reminderRow.Current.ControlType != ControlType.ListItem)
            reminderRow = TreeWalker.ControlViewWalker.GetParent(reminderRow)
                ?? throw new InvalidOperationException("Reminder has no Inbox list-item ancestor.");
        Assert.Equal(run.AppProcessId, reminderRow.Current.ProcessId);
        if (reminderRow.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
            ((ScrollItemPattern)scroll).ScrollIntoView();
        var expander = Assert.Single(reminderRow.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true))
            .Cast<AutomationElement>());
        var pattern = (ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            pattern.Expand();
        await WaitUiAsync(run, () =>
        {
            var button = reminderRow.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, actionLabel)));
            if (pattern.Current.ExpandCollapseState != ExpandCollapseState.Expanded ||
                button is null || button.Current.IsOffscreen || !button.Current.IsEnabled)
                return false;
            var bounds = button.Current.BoundingRectangle;
            return !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
                bounds.Width > 0 && bounds.Height > 0 &&
                reminderRow.Current.BoundingRectangle.Contains(bounds) &&
                AppWindows(run).Any(window => window.Current.BoundingRectangle.Contains(bounds));
        }, "expanded notification action visible and enabled");
    }
}
