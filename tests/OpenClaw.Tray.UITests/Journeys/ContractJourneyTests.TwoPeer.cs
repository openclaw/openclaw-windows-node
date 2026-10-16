using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.Connection;
using OpenClaw.TestSupport.Gateway;
using OpenClaw.TestSupport.Ui;

namespace OpenClaw.Tray.UITests.Journeys;

// The saved-gateway UI changes ownership; independent peer observations verify routing.
public sealed partial class ContractJourneyTests
{
    [ContractJourneyFact("C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer")]
    [Trait("Category", "ContractJourney")]
    public Task SwitchingSavedPeerRoutesWorkToOnlyNewPeer()
    {
        var peerA = new GatewayPeerProfile("alpha");
        var peerB = new GatewayPeerProfile("bravo");
        return WithAppAsync("C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer", false,
            async (run, cancellation) =>
        {
            // 1. The app starts connected to the active primary peer A. Peer B is a saved but
            //    inactive record whose owned server has received no connection yet.
            var secondary = run.SecondaryGateway
                ?? throw new InvalidOperationException("A two-peer run must expose the inactive second peer server.");
            var registry = new GatewayRegistry(run.Profile.DataDirectory);
            registry.Load();
            Assert.Equal(run.Profile.GatewayId, registry.ActiveGatewayId);
            Assert.Equal(2, registry.GetAll().Count);
            Assert.Equal(run.SecondaryGatewayId, registry.GetAll().Single(r => r.Id != run.Profile.GatewayId).Id);
            Assert.Equal(0, secondary.ConnectionCount);
            var status = await run.InvokeAsync("app.status");
            Assert.Equal("Connected", status.GetProperty("operatorState").GetString());
            Observe("started-connected-to-active-primary-with-inactive-second-peer");

            // 2. Open the real Connection UI and attach the owned lvt/UIA driver.
            await run.InvokeAsync("app.navigate", new { page = "connection" });
            var window = await WaitWindowAsync(run, "ConnectionPageMarker", cancellation);
            await using var driver = await ConnectAsync(run, window, cancellation);
            await driver.WaitAsync("ConnectionPageMarker", Visible, cancellation);
            // Exactly one saved peer is inactive (B), so there is exactly one Connect affordance.
            // The saved-gateway card sits below the fold of the page's ScrollViewer.
            await ScrollIntoViewportAsync(window, "SavedGatewayConnectAction", cancellation);
            await driver.WaitAsync("SavedGatewayConnectAction", Visible, cancellation);
            Capture(run, window, "two-peer-before-switch.png", "SavedGatewayConnectAction");
            Observe("two-saved-peers-with-single-inactive-connect-affordance");

            // 3. Switch to peer B through the actual saved-gateway Connect action. This exercises
            //    GatewayConnectionManager.SwitchGatewayAsync via the UI, not an MCP switch bypass.
            await driver.ActAsync("invoke", "SavedGatewayConnectAction", cancellation);

            // 4. Active selection moves to peer B: the registry flips, B's owned server gains a
            //    connection, and the old primary operator connection is fully torn down.
            await run.WaitForAsync(() =>
            {
                var current = new GatewayRegistry(run.Profile.DataDirectory);
                current.Load();
                return Task.FromResult(current.ActiveGatewayId == run.SecondaryGatewayId
                    && secondary.ConnectionCount >= 1
                    && run.Gateway.ActiveConnectionCount == 0);
            }, "active selection switched to peer B and the primary connection drained", cancellationToken: cancellation);
            await secondary.WaitForCompletedRequestAsync("agents.list", cancellationToken: cancellation);

            registry = new GatewayRegistry(run.Profile.DataDirectory);
            registry.Load();
            Assert.Equal(run.SecondaryGatewayId, registry.ActiveGatewayId);
            var switched = await run.InvokeAsync("app.status");
            Assert.Equal("Connected", switched.GetProperty("operatorState").GetString());
            Assert.True(switched.GetProperty("sessionCount").GetInt32() >= 5);
            Observe("switch-moved-active-selection-and-connection-to-new-peer");

            // 5. From here, subsequent work must reach ONLY peer B. The primary is disconnected,
            //    so freeze its request count as a baseline and assert it never advances again.
            var primaryRequestsAfterSwitch = run.Gateway.Requests.Count;

            // Select peer B's distinct main conversation BY ITS DISTINCT TITLE. Selecting it by a
            // title that only peer B advertises, and loading its history, proves the new peer's
            // distinct catalog rendered in the UI rather than a stale primary catalog.
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await GatewayFixtureUiTests.SelectSessionAsync(run, peerB.MainTitle, GatewayScenario.MainSessionKey);
            await secondary.WaitForCompletedRequestAsync(
                "chat.history", GatewayScenario.MainSessionKey, cancellationToken: cancellation);
            Observe("new-peer-distinct-catalog-selected-and-history-routed-to-new-peer");

            // Routing isolation: the switched-away primary received no further requests at all.
            Assert.Equal(primaryRequestsAfterSwitch, run.Gateway.Requests.Count);
            Assert.Empty(secondary.UnexpectedRequests);
            Assert.Empty(run.Gateway.UnexpectedRequests);

            var afterWindow = await WaitWindowAsync(run, "WorkspaceNavigation", cancellation);
            Capture(run, afterWindow, "two-peer-after-switch.png", "WorkspaceNavigation");
            Observe("subsequent-work-reached-only-the-new-peer");
        }, runFactory: (executable, artifactRoot, token) =>
            GatewayFixtureRun.StartTwoPeerAsync(executable, peerA, peerB, artifactRoot, token));
    }

    /// <summary>
    /// Scrolls the nearest vertically scrollable ancestor until the control is fully inside
    /// that viewport. A user scrolls to reach the control; no visibility is assumed or forced.
    /// </summary>
    private static async Task ScrollIntoViewportAsync(AutomationElement window, string automationId,
        CancellationToken cancellation)
    {
        var target = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
            ?? throw new ContractAssertionException($"Required control '{automationId}' is not present.");
        AutomationElement? viewport = null;
        ScrollPattern? scroll = null;
        for (var parent = TreeWalker.ControlViewWalker.GetParent(target); parent is not null;
             parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (parent.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
                && ((ScrollPattern)pattern).Current.VerticallyScrollable)
            {
                viewport = parent;
                scroll = (ScrollPattern)pattern;
                break;
            }
            if (Automation.Compare(parent, window)) break;
        }
        for (var step = 0; step < 60; step++)
        {
            cancellation.ThrowIfCancellationRequested();
            var bounds = target.Current.BoundingRectangle;
            var visible = viewport?.Current.BoundingRectangle ?? window.Current.BoundingRectangle;
            if (!target.Current.IsOffscreen && !bounds.IsEmpty && visible.Contains(bounds)) return;
            if (scroll is null) break;
            var down = bounds.IsEmpty || bounds.Top >= visible.Top;
            var percent = scroll.Current.VerticalScrollPercent;
            if (down ? percent >= 100 : percent <= 0) break;
            var outside = bounds.IsEmpty || bounds.Bottom <= visible.Top || bounds.Top >= visible.Bottom;
            scroll.ScrollVertical(down
                ? outside ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement
                : outside ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement);
            await Task.Delay(50, cancellation);
        }
        throw new ContractAssertionException($"Required control '{automationId}' could not be scrolled fully into view.");
    }
}
