using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Lobsterdex_CollectsARealVisitAndReopensWithVisitsOff()
    {
        const string expanderId = "SettingsLobsterdex";
        const string toggleId = "SettingsPageShowLobsterVisits";
        const string empty = "No lobsters recorded yet. Look for visitors when you start a new session.";
        const uint shinyScheduledVisitSeed = 1004;
        const string legacy = """{"visits":8,"shoos":2}""";
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")!;

        await WithAppAsync(async run =>
        {
            var path = Path.Combine(run.Profile.DataDirectory, "lobsterdex.json");
            var counters = Path.Combine(run.Profile.DataDirectory, "lobster-visits.json");

            await OpenLobsterdexAsync(run);
            await WaitUiAsync(run, () => FindById(run, "SettingsLobsterdexStatus") is { } status
                && status.Current.Name == empty, "empty collection even with legacy visit counts");
            Assert.False(File.Exists(path));
            Assert.Equal(legacy, await File.ReadAllTextAsync(counters));
            await ScrollToLobsterdexElementAsync(run, "SettingsLobsterdexStatus");
            CaptureSettings(run, "lobsterdex-empty.png");

            await run.InvokeAsync("app.navigate", new { page = "chat" });
            Invoke(await WaitForElementByIdAsync(run, "WorkspaceSessionsAdd", "native new conversation button"));
            // The decorative sprite is intentionally absent from the UIA control view.
            // Wait for the real display path to persist the sighting, then capture its composer.
            await run.WaitForAsync(() => Task.FromResult(File.Exists(path)), "first displayed sighting persisted");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, FindHub(run), "lobsterdex-first-arrival.png");
            var stored = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(stored);
            var sighting = Assert.Single(document.RootElement.EnumerateObject());
            var name = sighting.Value.GetProperty("name").GetString();
            var firstSeen = sighting.Value.GetProperty("firstSeenAt").GetDateTimeOffset();
            Assert.Equal(firstSeen, sighting.Value.GetProperty("shinySeenAt").GetDateTimeOffset());
            Assert.Equal(3, sighting.Value.EnumerateObject().Count());
            // The product's anniversary wardrobe replaces any rolled palette with Retro on Nov 24.
            Assert.Equal(firstSeen is { Month: 11, Day: 24 } ? "retro" : "crimson", sighting.Name);
            await run.WaitForAsync(async () =>
            {
                using var familiar = JsonDocument.Parse(await File.ReadAllTextAsync(counters));
                return familiar.RootElement.GetProperty("visits").GetInt32() == 9
                    && familiar.RootElement.GetProperty("shoos").GetInt32() == 2;
            }, "ordinary arrival updates the existing familiarity counters independently");

            await OpenLobsterdexAsync(run);
            await AssertSightingAsync(run, "lobsterdex-first-sighting.png");

            var toggle = await WaitForElementByIdAsync(run, toggleId, "existing Lobster visits toggle");
            ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            await run.WaitForAsync(async () =>
            {
                using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run.Profile.DataDirectory, "settings.json")));
                return settings.RootElement.TryGetProperty("ShowLobsterVisits", out var enabled) && !enabled.GetBoolean();
            }, "visits disabled and saved");

            var firstProcess = run.AppProcessId;
            await run.RestartAppAsync(app);
            Assert.NotEqual(firstProcess, run.AppProcessId);
            await OpenLobsterdexAsync(run);
            await AssertSightingAsync(run, "lobsterdex-reopened.png");
            Assert.Equal(stored, await File.ReadAllTextAsync(path));

            async Task AssertSightingAsync(GatewayFixtureRun fixture, string capture)
            {
                var rowId = "LobsterdexSighting:" + sighting.Name;
                await ScrollToLobsterdexElementAsync(fixture, rowId);
                var row = await WaitForElementByIdAsync(fixture, rowId, "accessible name of the first visitor");
                Assert.Equal(ControlType.Text, row.Current.ControlType);
                Assert.Equal(name, row.Current.Name);
                await WaitUiAsync(fixture, () => LobsterdexTextIsVisible(fixture, "First seen ")
                    && LobsterdexTextIsVisible(fixture, "First shiny sighting: "), "accessible first visit and shiny date");
                CaptureSettings(fixture, capture);
                output.WriteLine($"{capture}: {sighting.Name}, {row.Current.Name}, first and shiny sighting {firstSeen:O}");
            }
        }, allowSessionMutations: true, configureProfile: profile =>
        {
            profile.LobsterVisitSeed = shinyScheduledVisitSeed;
            File.WriteAllText(Path.Combine(profile.DataDirectory, "lobster-visits.json"), legacy);
        });

        async Task OpenLobsterdexAsync(GatewayFixtureRun run)
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await ScrollToLobsterdexElementAsync(run, expanderId);
            var expander = await WaitForElementByIdAsync(run, expanderId, "Lobsterdex disclosure");
            Assert.Equal("Lobsterdex", expander.Current.Name);
            var disclosure = (ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (disclosure.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                disclosure.Expand();
        }
    }

    private static async Task ScrollToLobsterdexElementAsync(GatewayFixtureRun run, string id)
    {
        await WaitForElementByIdAsync(run, id, id);
        var viewport = await WaitForElementByIdAsync(run, "GeneralSettingsScroll", "General settings scrolling surface");
        var scroll = (ScrollPattern)viewport.GetCurrentPattern(ScrollPattern.Pattern);
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        await WaitUiAsync(run, () =>
        {
            var target = FindById(run, id);
            if (target is null)
                return false;
            var bounds = target.Current.BoundingRectangle;
            var visible = viewport.Current.BoundingRectangle;
            // Leave room beneath the name for its two date labels and for capture clipping at the desktop edge.
            if (!target.Current.IsOffscreen && !bounds.IsEmpty && bounds.Top >= visible.Top
                && bounds.Bottom <= visible.Top + visible.Height * .72)
                return true;
            if (scroll.Current.VerticallyScrollable && scroll.Current.VerticalScrollPercent < 100)
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
            return false;
        }, $"{id} fully visible in General settings");
    }

    private static bool LobsterdexTextIsVisible(GatewayFixtureRun run, string prefix) =>
        LobsterdexSettingsWindow(run).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Any(element => !element.Current.IsOffscreen
                && element.Current.Name.StartsWith(prefix, StringComparison.Ordinal));

    private static AutomationElement LobsterdexSettingsWindow(GatewayFixtureRun run) =>
        AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "GeneralSettingsScroll")) is not null);

    private static void CaptureSettings(GatewayFixtureRun run, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
            OwnedWindowCapture.Save(run, LobsterdexSettingsWindow(run), name);
    }
}
