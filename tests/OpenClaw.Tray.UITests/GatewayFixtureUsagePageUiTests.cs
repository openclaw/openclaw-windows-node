using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsagePageKeepsSelectedPeriodAndTerminatesFailedRefresh()
    {
        const string costFailure = "Synthetic cost unavailable";
        var failCost = false;
        var scenario = GatewayScenario.CreateUsage((method, parameters) =>
        {
            if (method == "usage.status")
                return new
                {
                    updatedAt = 1_787_227_200_000L,
                    providers = new[]
                    {
                        new
                        {
                            provider = "fixture", displayName = "Fixture usage provider",
                            windows = new[] { new { label = "Daily", usedPercent = 42.0 } }
                        }
                    }
                };
            if (method == "usage")
                return new { requestCount = 42 };
            if (Volatile.Read(ref failCost))
                throw new FixtureRequestException("FIXTURE_ERROR", costFailure);
            var days = parameters.GetProperty("days").GetInt32();
            return new
            {
                updatedAt = 1_787_227_200_000L, days,
                totals = new { totalTokens = 1200, totalCost = days == 7 ? 0.07 : 0.30 },
                daily = new[] { new { date = "2026-08-20", totalTokens = 1200, totalCost = 0.01 } }
            };
        });
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindById(run, "UsageTotalCost")?.Current.Name == "$0.07" &&
                FindText(run, "Fixture usage provider") is { Current.IsOffscreen: false },
                "selected seven-day cost and provider status");
            Assert.True(((SelectionItemPattern)FindById(run, "UsagePeriod7Days")!
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            CaptureUsagePage(run, "usage-7-days.png", FindById(run, "UsageTotalCost")!);

            var thirty = FindById(run, "UsagePeriod30Days")!;
            ScrollAncestorToBottom(thirty);
            ((SelectionItemPattern)thirty.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await WaitUiAsync(run, () => FindById(run, "UsageTotalCost")?.Current.Name == "$0.30" &&
                FindById(run, "UsagePeriodSelector")?.Current.IsEnabled == true, "selected thirty-day cost");
            Assert.True(((SelectionItemPattern)FindById(run, "UsagePeriod30Days")!
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            CaptureUsagePage(run, "usage-30-days.png", FindById(run, "UsagePeriod30Days")!);

            Volatile.Write(ref failCost, true);
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindText(run, costFailure) is { Current.IsOffscreen: false } &&
                FindById(run, "UsagePeriodSelector")?.Current.IsEnabled == true, "failed cost request is terminal and visible");
            Assert.Null(FindText(run, "Loading daily costs..."));
            CaptureUsagePage(run, "usage-cost-error.png", FindText(run, costFailure)!);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "usage-page-proof.json"),
                JsonSerializer.Serialize(new
                {
                    scenario = scenario.Name, scenario.Sha256,
                    proof = "Synthetic Gateway socket and mounted native Usage page, not live Gateway/package proof",
                    selectedSevenDayCost = "$0.07", selectedThirtyDayCost = "$0.30",
                    provider = "Fixture usage provider", costFailure, failedRefreshTerminates = true
                }, new JsonSerializerOptions { WriteIndented = true }));
        }, scenario: scenario);
    }

    private static void CaptureUsagePage(OpenClaw.GatewayFixtureHost.GatewayFixtureRun run,
        string name, AutomationElement visibleState)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1") return;
        var window = AppWindows(run).Single(candidate => candidate.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "UsagePageMarker")) is not null);
        OwnedWindowCapture.Save(run, window, name, visibleState);
    }
}
