// Exercises notification dismissal through the real mounted native destination, not a browser result.
// Run with the Gateway fixture opt-in and --filter FullyQualifiedName~NotificationAction_NativeConnection.
using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task NotificationAction_NativeConnectionDismissesOnlyItsReminderInTheSameWindow()
    {
        const string inboxRoute = "workspace:notifications";
        const string failureDismissLabel = "Dismiss Dashboard";
        const string dashboardReminderMessage = "Dashboard could not open. Check the verified gateway connection, then try again.";
        const string slackDismissLabel = "Dismiss Slack channel needs attention";
        var inboxMarker = new PropertyCondition(AutomationElement.AutomationIdProperty, "NotificationsPageMarker");
        var connectionMarker = new PropertyCondition(AutomationElement.AutomationIdProperty, "ConnectionPageMarker");

        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = inboxRoute });
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is { Current.IsOffscreen: false } &&
                FindButtons(run, slackDismissLabel).Count == 1, "original Slack reminder in the inbox");
            var window = Assert.Single(AppWindows(run), candidate =>
                candidate.FindFirst(TreeScope.Descendants, inboxMarker) is { Current.IsOffscreen: false });
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);

            // WindowManager publishes the real Dashboard failure reminder with a Connection
            // route. The existing browser boundary supplies only the external launch refusal.
            await ExpandNotificationReminderAsync(run, "Slack channel needs attention", "Open Channels");
            ArmSimulatedBrowserFailure(run);
            Invoke(Assert.Single(FindButtons(run, "Open Channels")));
            await WaitUiAsync(run, () => FindText(run, DashboardFailureMessage) is { Current.IsOffscreen: false } &&
                FindButtons(run, RetryButtonName).Count == 1, "Dashboard recovery dialog");
            AssertDashboardLaunches(run, DashboardOutcome.SimulatedFailure);

            // Scope Close to Retry's native dialog container, not the Settings banner or
            // window caption. Do not Retry: successful browser recovery clears this reminder.
            var retry = Assert.Single(FindButtons(run, RetryButtonName));
            var dialogButtons = TreeWalker.ControlViewWalker.GetParent(retry);
            Assert.NotNull(dialogButtons);
            var close = Assert.Single(dialogButtons.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Close")))
                .Cast<AutomationElement>(), button => !button.Current.IsOffscreen && button.Current.IsEnabled);
            Assert.Equal(run.AppProcessId, close.Current.ProcessId);
            Invoke(close);
            await WaitUiAsync(run, () => FindButtons(run, RetryButtonName).Count == 0, "recovery dialog closed without retry");

            await run.InvokeAsync("app.navigate", new { page = inboxRoute });
            await WaitUiAsync(run, () => window.FindFirst(TreeScope.Descendants, inboxMarker) is { Current.IsOffscreen: false } &&
                FindButtons(run, failureDismissLabel).Count == 1 && FindButtons(run, slackDismissLabel).Count == 1,
                "Dashboard and unrelated Slack reminders retained in the original inbox");
            await ExpandNotificationReminderAsync(run, "Dashboard", "Connection");
            var failureRow = Assert.Single(FindButtons(run, failureDismissLabel));
            while (failureRow.Current.ControlType != ControlType.ListItem)
                failureRow = TreeWalker.ControlViewWalker.GetParent(failureRow)
                    ?? throw new InvalidOperationException("Dashboard reminder has no notification list-item ancestor.");
            Assert.NotNull(failureRow.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, dashboardReminderMessage))));
            var connection = Assert.Single(failureRow.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Connection"))).Cast<AutomationElement>());
            Assert.True(connection.Current.IsEnabled);
            await CaptureIfRequestedAsync(run, "native-notification-before-connection.png");

            // Invoke the uniquely scoped native action through UI Automation.
            // This must traverse the shared InboxView -> TryNavigateAsync -> native admission.
            Invoke(connection);
            await WaitUiAsync(run, () => window.FindFirst(TreeScope.Descendants, connectionMarker) is
                { Current.IsOffscreen: false }, "Connection visibly mounted in the original window");
            var destinationWindow = Assert.Single(AppWindows(run), candidate =>
                candidate.FindFirst(TreeScope.Descendants, connectionMarker) is { Current.IsOffscreen: false });
            Assert.Equal(hwnd, new IntPtr(destinationWindow.Current.NativeWindowHandle));
            await CaptureIfRequestedAsync(run, "native-notification-connection-admitted.png");

            await run.InvokeAsync("app.navigate", new { page = inboxRoute });
            await WaitUiAsync(run, () => window.FindFirst(TreeScope.Descendants, inboxMarker) is { Current.IsOffscreen: false } &&
                FindButtons(run, failureDismissLabel).Count == 0, "admitted Dashboard reminder removed from the inbox");
            Assert.Single(FindButtons(run, slackDismissLabel));
            await ExpandNotificationReminderAsync(run, "Slack channel needs attention", "Open Channels");
            Assert.Single(FindButtons(run, "Open Channels"));
            AssertDashboardLaunches(run, DashboardOutcome.SimulatedFailure);
            await CaptureIfRequestedAsync(run, "native-notification-only-slack-retained.png");
        }, scenario: GatewayScenario.CreateChannelIssue("Fixture channel authorization failed."));
    }
}
