using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task DiagnosticsHeartbeatRefreshShowsRecordedOutcomeWithoutMutatingGateway()
    {
        const long timestamp = 1791257718360;
        const string privatePreview = "Synthetic heartbeat preview must not appear in Diagnostics.";
        var scenario = GatewayScenario.CreateHeartbeat(JsonSerializer.SerializeToElement(new
        {
            ts = timestamp, status = "skipped", reason = "target-none", preview = privatePreview
        }));
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "debug" });
            await WaitUiAsync(run, () => FindById(run, "GatewayHeartbeatRefresh") is
                { Current.IsEnabled: true, Current.IsOffscreen: false }
                && FindById(run, "GatewayHeartbeatStatus")?.Current.Name
                    == "Refresh to read the last agent heartbeat.", "ready Diagnostics heartbeat");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "last-heartbeat");

            run.Gateway.HoldHeartbeatReads();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                Invoke(FindById(run, "GatewayHeartbeatRefresh")!);
                await run.Gateway.WaitForRequestAsync("last-heartbeat", null, cancellationToken: deadline.Token);
                await WaitUiAsync(run, () => FindById(run, "GatewayHeartbeatRefresh")?.Current.IsEnabled == false
                    && FindById(run, "GatewayHeartbeatStatus")?.Current.Name == "Reading agent heartbeat...",
                    "held heartbeat read visibly loading");
            }
            finally { run.Gateway.ReleaseHeartbeatReads(); }

            await run.Gateway.WaitForCompletedRequestAsync("last-heartbeat", cancellationToken: deadline.Token);
            var expected = "Skipped at " + DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("g");
            await WaitUiAsync(run, () => FindById(run, "GatewayHeartbeatStatus") is
                { Current.IsOffscreen: false } status && status.Current.Name == expected
                && FindById(run, "GatewayHeartbeatRefresh") is
                    { Current.IsEnabled: true, Current.IsOffscreen: false }, "recorded heartbeat outcome and timestamp");
            Assert.Null(FindText(run, privatePreview));
            var request = Assert.Single(run.Gateway.Requests, request => request.Method == "last-heartbeat");
            Assert.Equal("ok", request.Outcome);
            Assert.DoesNotContain(run.Gateway.Requests,
                request => request.Method is "set-heartbeats" or "system-event" or "wake");

            var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "GatewayHeartbeatStatus")) is not null);
            OwnedWindowCapture.Save(run, window, "diagnostics-heartbeat-recorded.png",
                FindById(run, "GatewayHeartbeatStatus")!, FindById(run, "GatewayHeartbeatRefresh")!);
            Assert.Equal(expected, FindById(run, "GatewayHeartbeatStatus")!.Current.Name);
        }, scenario: scenario);
    }
}
