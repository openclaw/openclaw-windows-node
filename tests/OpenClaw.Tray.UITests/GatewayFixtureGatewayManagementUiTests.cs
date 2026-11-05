using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiTheory]
    [InlineData("native-only", false, false, "Windows native")]
    [InlineData("native-with-wsl", true, false, "Windows native")]
    [InlineData("wsl-only", true, true, "WSL")]
    [Trait("Category", "GatewayFixture")]
    public async Task GatewayManagement_UsesSavedOwnershipWithoutSetupState(
        string scenario, bool hasWsl, bool activeWsl, string expectedTopology)
    {
        await WithAppAsync(async run =>
        {
            Assert.False(File.Exists(Path.Combine(run.Profile.SetupDirectory, "setup-state.json")));
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false },
                "Settings page mounted");
            var window = Assert.Single(AppWindows(run), candidate => candidate.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsPageMarker")) is not null);
            var setup = FindById(run, "LocalGatewaySetupCard")!;
            ScrollSettingsSectionIntoView(setup);
            await WaitUiAsync(run, () => !setup.Current.IsOffscreen, "Local Gateway setup card visible");

            var asserted = new List<AutomationElement> { setup };
            // Onboarding remains an active-WSL action. Coexistence exposes removal,
            // not onboarding of the currently selected native Gateway.
            var onboard = FindById(run, "OpenClawOnboardCard");
            if (activeWsl)
            {
                Assert.NotNull(onboard);
                Assert.False(onboard.Current.IsOffscreen);
                asserted.Add(onboard);
            }
            else
            {
                Assert.Null(onboard);
            }
            if (hasWsl)
            {
                var removal = FindById(run, "LocalGatewayExpander");
                Assert.NotNull(removal);
                Assert.False(removal.Current.IsOffscreen);
                Assert.True(FindById(run, "RemoveLocalGatewayButton")!.Current.IsEnabled);
                asserted.Add(removal);
            }
            else
            {
                Assert.Null(FindById(run, "LocalGatewayExpander"));
                Assert.Null(FindById(run, "RemoveLocalGatewayButton"));
                asserted.Add(FindText(run, "Developer")!);
            }

            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, window, $"{scenario}-settings.png", asserted.ToArray());
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, $"{scenario}-ownership.json"),
                JsonSerializer.Serialize(new
                {
                    scenario, hasSetupState = false, hasWsl, activeWsl, expectedTopology,
                    run.AppProcessId, run.Profile.GatewayId,
                    boundary = "Synthetic saved ownership, MCP only. No native package or WSL runtime."
                }));
            await OwnedTrayTooltipCapture.AssertAndCaptureAsync(run, window, expectedTopology, $"{scenario}-tooltip.png");
            Assert.Empty(run.Gateway.Requests);
        }, connectGateway: false, configureProfile: profile =>
        {
            var registry = new GatewayRegistry(profile.DataDirectory);
            registry.Load();
            var distro = $"OpenClawFixture-{profile.RunId[..8]}";
            var active = registry.GetById(profile.GatewayId)!;
            registry.AddOrUpdate(active with
            {
                IsLocal = true,
                FriendlyName = $"Local ({distro})",
                NativePackageFamilyName = activeWsl ? null : $"OpenClaw.Fixture_{profile.RunId[..8]}",
                SetupManagedDistroName = activeWsl ? distro : null
            });
            if (hasWsl && !activeWsl)
                registry.AddOrUpdate(new GatewayRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Url = profile.GatewayEndpoint.AbsoluteUri,
                    FriendlyName = $"Local ({distro})",
                    IsLocal = true,
                    SetupManagedDistroName = distro
                });
            registry.Save();
        });
    }

    private static void ScrollSettingsSectionIntoView(AutomationElement section)
    {
        for (var parent = TreeWalker.ControlViewWalker.GetParent(section); parent is not null;
            parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (!parent.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
                continue;
            var scroll = (ScrollPattern)pattern;
            if (!scroll.Current.VerticallyScrollable)
                return;
            var viewport = parent.Current.BoundingRectangle;
            Assert.InRange(scroll.Current.VerticalViewSize, 0.01, 99.99);
            var travel = viewport.Height * (100 / scroll.Current.VerticalViewSize - 1);
            var offset = travel * scroll.Current.VerticalScrollPercent / 100
                + section.Current.BoundingRectangle.Top - viewport.Top - 32;
            scroll.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(offset / travel * 100, 0, 100));
            return;
        }
        throw new InvalidOperationException("The mounted Settings section has no accessible scroll owner.");
    }
}
