using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsagePeriodSwitch_MountedPageClearsBeforeNewCostsAndAcceptsOlderSnapshot()
    {
        var second = new UsagePeriodFixture(UsagePeriodFixture.GatewayMode.B);
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindById(run, "TotalCostText")?.Current.Name == "$7.00"
                && UsagePeriodSelected(run, "7 Days"), "mounted Usage seeded with Gateway A");
            var total = FindById(run, "TotalCostText")!;
            await CaptureIfRequestedAsync(run, "01-mounted-gateway-a.png", total, FindUsagePeriod(run, "7 Days")!);

            var gateway = run.SecondaryGateway!;
            gateway.HoldUsageCosts(7);
            try
            {
                gateway.HoldUsageCosts(30);
                try
                {
                    var connection = await run.ConnectSecondaryGatewayAsync();
                    Assert.True(connection.GetProperty("connected").GetBoolean());
                    await run.WaitForAsync(async () =>
                    {
                        var status = await run.InvokeAsync("app.connection.status");
                        return status.GetProperty("gateway").GetProperty("id").GetString() == run.SecondaryGatewayId
                            && status.GetProperty("operator").GetProperty("connected").GetBoolean();
                    }, "Gateway B live operator connection after shared-token validation");
                    await gateway.WaitForRequestAsync("usage.cost");
                    await WaitUiAsync(run, () => total.Current.Name == "\u2014",
                        "Gateway A total cleared while all Gateway B cost responses are held");
                    Assert.True(Automation.Compare(total, FindById(run, "TotalCostText")!));
                    Assert.Null(FindUsageValue(run, "$7.00"));
                    Assert.DoesNotContain(gateway.Requests, request => request.Method == "usage.cost" && request.Outcome == "ok");
                    await CaptureIfRequestedAsync(run, "02-mounted-before-b-costs.png", total, FindUsagePeriod(run, "7 Days")!);
                }
                finally
                {
                    gateway.ReleaseUsageCosts(30);
                }

                await WaitUiAsync(run, () => FindText(run, "Usage period is not available") is not null
                    && UsagePeriodSelected(run, "7 Days"), "mounted Usage recovers from Gateway B range mismatch");
                Assert.True(Automation.Compare(total, FindById(run, "TotalCostText")!));
                Assert.Equal("\u2014", total.Current.Name);
                Assert.Null(FindUsageValue(run, "$300.00"));
                Assert.Null(FindButton(run, "Open Connection"));
                Assert.Null(FindText(run, "No daily usage for this period"));
                Assert.Null(FindText(run, "Loading daily costs"));
                await CaptureIfRequestedAsync(run, "03-mounted-b-warning.png",
                    total, FindText(run, "Usage period is not available")!, FindUsagePeriod(run, "7 Days")!);
                second.EnableMatchingSevenDays();
            }
            finally
            {
                gateway.ReleaseUsageCosts(7);
            }
            await WaitUiAsync(run, () => total.Current.Name == "$70.00"
                && UsagePeriodSelected(run, "7 Days"), "same mounted Usage accepts Gateway B older seven-day snapshot");
            Assert.True(Automation.Compare(total, FindById(run, "TotalCostText")!));
            Assert.Null(FindText(run, "Usage period is not available"));
            Assert.Null(FindUsageValue(run, "$7.00"));
            await CaptureIfRequestedAsync(run, "04-mounted-b-older-seven.png", total, FindUsagePeriod(run, "7 Days")!);
        }, scenario: GatewayScenario.CreateUsagePeriod(new UsagePeriodFixture(UsagePeriodFixture.GatewayMode.A)),
            secondaryScenario: GatewayScenario.CreateUsagePeriod(second));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsagePeriodSwitch_ClearsPreviousGatewayAndAcceptsOlderMatchingSnapshot()
    {
        var second = new UsagePeriodFixture(UsagePeriodFixture.GatewayMode.B);
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUsagePeriodAsync(run, "7 Days", "$7.00");
            await CaptureIfRequestedAsync(run, "01-gateway-a-seven.png",
                FindUsageValue(run, "$7.00")!, FindUsagePeriod(run, "7 Days")!);

            await run.InvokeAsync("app.navigate", new { page = "connection" });
            await WaitUiAsync(run, () => FindById(run, "SavedGatewayConnectAction") is not null,
                "saved synthetic Gateway B connection action");
            Invoke(FindById(run, "SavedGatewayConnectAction")!);
            await run.WaitForAsync(async () =>
            {
                var status = await run.InvokeAsync("app.connection.status");
                return status.GetProperty("gateway").GetProperty("id").GetString() == run.SecondaryGatewayId
                    && status.GetProperty("operator").GetProperty("connected").GetBoolean();
            }, "Gateway B connected through Connection settings");
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindText(run, "Usage period is not available") is not null
                && UsagePeriodSelected(run, "7 Days"), "Gateway B seven-day mismatch warning");
            Assert.Null(FindUsageValue(run, "$7.00"));
            Assert.Null(FindUsageValue(run, "$300.00"));
            Assert.Null(FindButton(run, "Open Connection"));
            Assert.Null(FindText(run, "No daily usage for this period"));
            Assert.Null(FindText(run, "Loading daily costs"));
            await CaptureIfRequestedAsync(run, "02-gateway-b-warning.png",
                FindText(run, "Usage period is not available")!, FindUsagePeriod(run, "7 Days")!);

            Invoke(FindUsagePeriod(run, "30 Days")!);
            await WaitUsagePeriodAsync(run, "30 Days", "$300.00");
            Assert.Null(FindText(run, "Usage period is not available"));
            await CaptureIfRequestedAsync(run, "03-gateway-b-thirty.png",
                FindUsageValue(run, "$300.00")!, FindUsagePeriod(run, "30 Days")!);

            Invoke(FindUsagePeriod(run, "7 Days")!);
            await WaitUiAsync(run, () => FindText(run, "Usage period is not available") is not null,
                "returning to seven days cannot restore Gateway A");
            Assert.Null(FindUsageValue(run, "$7.00"));
            await run.InvokeAsync("app.navigate", new { page = "connection" });
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindText(run, "Usage period is not available") is not null
                && UsagePeriodSelected(run, "7 Days"), "recreated Usage cannot restore Gateway A");
            Assert.Null(FindUsageValue(run, "$7.00"));
            await CaptureIfRequestedAsync(run, "04-gateway-b-reopened.png",
                FindText(run, "Usage period is not available")!, FindUsagePeriod(run, "7 Days")!);

            second.EnableMatchingSevenDays();
            Invoke(FindUsagePeriod(run, "30 Days")!);
            await WaitUsagePeriodAsync(run, "30 Days", "$300.00");
            Invoke(FindUsagePeriod(run, "7 Days")!);
            await WaitUsagePeriodAsync(run, "7 Days", "$70.00");
            Assert.Null(FindText(run, "Usage period is not available"));
            Assert.Null(FindUsageValue(run, "$7.00"));
            await CaptureIfRequestedAsync(run, "05-gateway-b-older-seven.png",
                FindUsageValue(run, "$70.00")!, FindUsagePeriod(run, "7 Days")!);
        }, scenario: GatewayScenario.CreateUsagePeriod(new UsagePeriodFixture(UsagePeriodFixture.GatewayMode.A)),
            secondaryScenario: GatewayScenario.CreateUsagePeriod(second));
    }

    private static AutomationElement? FindUsagePeriod(GatewayFixtureRun run, string name) =>
        FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)));

    private static bool UsagePeriodSelected(GatewayFixtureRun run, string name) =>
        FindUsagePeriod(run, name) is { Current.IsEnabled: true } item
        && ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    private static AutomationElement? FindUsageValue(GatewayFixtureRun run, string value) =>
        FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, value)));

    private static Task WaitUsagePeriodAsync(GatewayFixtureRun run, string period, string total) =>
        WaitUiAsync(run, () => UsagePeriodSelected(run, period) && FindUsageValue(run, total) is not null,
            $"Usage {period} showing {total} with enabled selection");
}
