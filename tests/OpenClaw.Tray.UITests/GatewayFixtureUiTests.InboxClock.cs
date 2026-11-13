// Proves the shared Inbox's real age clock after page/flyout rebinding, using one
// shipped system.notify call and no further producer event. Run InboxAgeClock_*
// with scripts/test-gateway-fixture.ps1 -Screenshots for owned-window age evidence.
using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string InboxAgeTransitionTimeoutVariable = "OPENCLAW_GATEWAY_FIXTURE_INBOX_AGE_TIMEOUT_SECONDS";
    // A real minute boundary plus the shipped 30-second tick and scheduling margin.
    private const int DefaultInboxAgeTransitionTimeoutSeconds = 110;
    private static readonly TimeSpan InboxAgeTransitionTimeout =
        int.TryParse(Environment.GetEnvironmentVariable(InboxAgeTransitionTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(DefaultInboxAgeTransitionTimeoutSeconds);
    private static readonly TimeSpan InboxAgeNavigationBudget = TimeSpan.FromSeconds(30);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxAgeClock_PageAndBellAdvanceAfterReopenWithoutAnotherNotification()
    {
        await WithAppAsync(async run =>
        {
            const string title = "Fixture backup failed";
            const string body = "Retry the synthetic backup after the maintenance window.";
            using (var tools = await run.Client.ListToolsAsync())
                Assert.Contains(tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
                    tool => tool.GetProperty("name").GetString() == "system.notify");

            await run.InvokeAsync("app.navigate", new { page = "workspace:notifications" });
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is { Current.IsOffscreen: false },
                "full Inbox before the single notification");
            // The shipped mapper classifies a failed backup as an error. Do not inject a row,
            // timestamp, severity or fake clock into the app or its notification service.
            var sentAt = DateTimeOffset.UtcNow;
            var sent = await run.InvokeAsync("system.notify", new { title, body, sound = false });
            Assert.True(sent.GetProperty("sent").GetBoolean());
            await WaitUiAsync(run, () => FindRow() is { } row && FindAge(row, "now") is not null,
                "new notification renders its initial native age");
            // NodeService queues the notification onto the UI thread. Its actual creation is
            // between the call and this observation, not necessarily before the MCP response.
            var observedAt = DateTimeOffset.UtcNow;
            await ExpandAsync(FindRow()!);

            // Leave and re-enter the page before measuring its clock. Page recreation may
            // replace controls; identity must remain stable during each subsequent idle wait.
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is null or { Current.IsOffscreen: true },
                "Inbox page unloaded");
            await run.InvokeAsync("app.navigate", new { page = "workspace:notifications" });
            await WaitUiAsync(run, () => FindRow() is not null, "same notification on reopened page");
            var pageRow = FindRow()!;
            await ExpandAsync(pageRow);
            await WaitUiAsync(run, () => FindAge(pageRow, "now") is not null, "reopened page starts at now");
            var pageIdentity = pageRow.GetRuntimeId();
            Capture("inbox-age-page-before.png", pageRow, "now");
            await WaitForAgeAsync(pageRow, "1m", minutes: 1);
            AssertSameExpandedRow(pageIdentity);
            Capture("inbox-age-page-after.png", pageRow, "1m");

            await run.InvokeAsync("app.navigate", new { page = "chat" });
            Invoke(await WaitForElementByIdAsync(run, "WorkspaceNotifications", "Inbox bell"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNotificationsOpenPage") is { Current.IsOffscreen: false } &&
                FindRow() is not null, "same notification in bell flyout");
            await ExpandAsync(FindRow()!);
            // This shipped action closes the flyout before navigating. Reopen normally,
            // without a synthetic notification to restart the unbound view's clock.
            Invoke(FindById(run, "WorkspaceNotificationsOpenPage")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNotificationsOpenPage") is null or { Current.IsOffscreen: true },
                "bell flyout closed");
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is { Current.IsOffscreen: false },
                "bell navigation reached the Inbox page");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            Invoke(await WaitForElementByIdAsync(run, "WorkspaceNotifications", "reopened Inbox bell"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNotificationsOpenPage") is { Current.IsOffscreen: false } &&
                FindRow() is not null, "same notification on reopened bell");
            var bellRow = FindRow()!;
            Assert.Equal(ExpandCollapseState.Expanded,
                ((ExpandCollapsePattern)bellRow.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
            await WaitUiAsync(run, () => FindAge(bellRow, "1m") is not null, "reopened bell retains notification age");
            var bellIdentity = bellRow.GetRuntimeId();
            Capture("inbox-age-bell-before.png", bellRow, "1m");
            await WaitForAgeAsync(bellRow, "2m", minutes: 2);
            AssertSameExpandedRow(bellIdentity);
            Capture("inbox-age-bell-after.png", bellRow, "2m");

            AutomationElement? VisibleList() => AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNotificationsList")).Cast<AutomationElement>())
                .SingleOrDefault(element => !element.Current.IsOffscreen);

            AutomationElement? FindRow() => VisibleList()?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true)).Cast<AutomationElement>()
                .SingleOrDefault(element => element.Current.Name.StartsWith(title, StringComparison.Ordinal));

            AutomationElement? FindAge(AutomationElement row, string expected) => row.FindAll(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.NameProperty, expected))).Cast<AutomationElement>()
                .SingleOrDefault(element => FullyVisible(element));

            bool FullyVisible(AutomationElement element)
            {
                if (element.Current.IsOffscreen || VisibleList() is not { } list) return false;
                var viewport = list.Current.BoundingRectangle;
                var bounds = element.Current.BoundingRectangle;
                return !viewport.IsEmpty && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
                    bounds.Left >= viewport.Left && bounds.Top >= viewport.Top &&
                    bounds.Right <= viewport.Right && bounds.Bottom <= viewport.Bottom;
            }

            async Task ExpandAsync(AutomationElement row)
            {
                var pattern = (ExpandCollapsePattern)row.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                pattern.Expand();
                await WaitUiAsync(run, () => pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded,
                    "native notification row expanded");
            }

            async Task WaitForAgeAsync(AutomationElement row, string expected, int minutes)
            {
                // No MCP calls, navigation or fixture producer events during this wait. The
                // expected minute is bounded by independent observations of the real clock,
                // not by InboxProjection or the value currently rendered by the app.
                await WaitUiAsync(run, () =>
                {
                    var now = DateTimeOffset.UtcNow;
                    return now >= observedAt.AddMinutes(minutes) && now < sentAt.AddMinutes(minutes + 1) &&
                        FindAge(row, expected) is not null;
                }, $"idle Inbox clock advances to {expected} without another notification", InboxAgeTransitionTimeout);
                output.WriteLine($"Inbox age {expected}: notification observed between {sentAt:O} and {observedAt:O}; verified at {DateTimeOffset.UtcNow:O}.");
            }

            void AssertSameExpandedRow(int[] identity)
            {
                var current = FindRow()!;
                Assert.Equal(identity, current.GetRuntimeId());
                Assert.Equal(ExpandCollapseState.Expanded,
                    ((ExpandCollapsePattern)current.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
                Assert.Contains(current.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>(),
                    element => element.Current.Name == body && !element.Current.IsOffscreen);
            }

            void Capture(string name, AutomationElement row, string age)
            {
                var ageElement = FindAge(row, age);
                Assert.NotNull(ageElement);
                if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                    OwnedWindowCapture.Save(run, AppWindows(run).OrderByDescending(window =>
                        window.Current.BoundingRectangle.Width * window.Current.BoundingRectangle.Height).First(),
                        name, row, ageElement);
            }
        }, testTimeout: InboxAgeTransitionTimeout + InboxAgeTransitionTimeout + InboxAgeNavigationBudget);
    }
}
