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
            if (FindById(run, "AppNotificationInfoBar") is { Current.IsOffscreen: false } notification)
            {
                var close = notification.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"));
                Assert.NotNull(close);
                Invoke(close);
                await WaitUiAsync(run, () => FindById(run, "AppNotificationInfoBar") is not
                    { Current.IsOffscreen: false }, "notification dismissed before Settings capture");
            }
            // WinUI Borders do not expose automation peers. Observe their real
            // heading and action controls instead of the decorative containers.
            var heading = window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, "Local Gateway")));
            Assert.NotNull(heading);
            var setup = FindById(run, "OpenLocalGatewaySetupButton");
            Assert.NotNull(setup);
            if (hasWsl)
            {
                await WaitUiAsync(run, () => FindById(run, "RemoveLocalGatewayButton") is not null,
                    "managed WSL removal action loaded");
            }
            var sectionEnd = hasWsl ? FindById(run, "RemoveLocalGatewayButton") : setup;
            Assert.NotNull(sectionEnd);
            await ScrollSettingsSectionIntoViewAsync(run, heading, sectionEnd);
            await WaitUiAsync(run, () => !heading.Current.IsOffscreen && !setup.Current.IsOffscreen,
                "Local Gateway heading and setup action visible");
            var asserted = new List<AutomationElement> { heading, setup };
            // Onboarding remains an active-WSL action. Coexistence exposes removal,
            // not onboarding of the currently selected native Gateway.
            var onboard = FindById(run, "OpenClawOnboardButton");
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
                var removal = FindById(run, "RemoveLocalGatewayButton");
                Assert.NotNull(removal);
                Assert.False(removal.Current.IsOffscreen);
                Assert.True(removal.Current.IsEnabled);
                asserted.Add(removal);
            }
            else
            {
                Assert.Null(FindById(run, "RemoveLocalGatewayButton"));
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

    private static async Task ScrollSettingsSectionIntoViewAsync(
        GatewayFixtureRun run, AutomationElement sectionStart, AutomationElement sectionEnd)
    {
        for (var parent = TreeWalker.ControlViewWalker.GetParent(sectionEnd); parent is not null;
            parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (!parent.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
                continue;
            var scroll = (ScrollPattern)pattern;
            if (!scroll.Current.VerticallyScrollable)
                return;
            var scrollOwner = parent;
            // Use the final Gateway action as the scroll anchor rather than an
            // unrelated following section, accounting for the mounted WSL cards.
            await WaitUiAsync(run, () =>
            {
                var viewport = scrollOwner.Current.BoundingRectangle;
                var end = sectionEnd.Current.BoundingRectangle;
                var headingHeight = sectionStart.Current.BoundingRectangle.Height;
                // UIA clips element bounds: a one-pixel sliver is not a visible action.
                if (!sectionEnd.Current.IsOffscreen && headingHeight > 0 && end.Height >= headingHeight
                    && end.Top >= viewport.Top && end.Bottom + headingHeight <= viewport.Bottom)
                    return true;
                Assert.True(scroll.Current.VerticalScrollPercent < 100,
                    "Reached the end of Settings without revealing the complete Gateway section.");
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                return false;
            }, "complete Local Gateway section visible");
            return;
        }
        throw new InvalidOperationException("The mounted Settings section has no accessible scroll owner.");
    }
}
