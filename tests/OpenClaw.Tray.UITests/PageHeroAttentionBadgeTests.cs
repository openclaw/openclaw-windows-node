using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "ProductionResources")]
public sealed class PageHeroAttentionBadgeTests(UIThreadFixture ui)
{
    private static FrameworkElement? Find(DependencyObject root, string id)
    {
        var matches = FindDescendants<FrameworkElement>(root)
            .Where(element => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == id).ToArray();
        return matches.Length == 0 ? null : Assert.Single(matches);
    }

    [Fact]
    public async Task AttentionBadge_DoesNotRemountOnRefreshAndRemountsAfterRemoval()
    {
        RequireProductionResources();
        await ui.ResetContainerAsync();
        var probe = new BadgeProbe();
        ReactorHostControl? renderer = null;
        Border? surface = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl { RequestedTheme = ElementTheme.Light };
                renderer.Mount(_ => Component<BadgeLifecycleView, BadgeLifecycleProps>(new(probe)));
                surface = new Border { Width = 480, Height = 220, Child = renderer };
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "BadgeLifecycleStatus") is not null && probe.Mounts == 1,
                "initial attention badge mount"));

            await ui.RunOnUIAsync(() => probe.Refresh!());
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "BadgeLifecycleRevision") is TextBlock { Text: "1" },
                "unrelated render refresh"));
            Assert.Equal(1, probe.Mounts);
            Assert.Equal(0, probe.Unmounts);

            await ui.RunOnUIAsync(() => probe.Toggle!());
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "BadgeLifecycleStatus") is null && probe.Unmounts == 1,
                "attention badge removal"));

            await ui.RunOnUIAsync(() => probe.Toggle!());
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "BadgeLifecycleStatus") is not null && probe.Mounts == 2,
                "attention badge reappearance"));
            Assert.Equal(1, probe.Unmounts);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) ui.Container.Children.Remove(surface);
                renderer?.Dispose();
            });
        }
    }

    private void RequireProductionResources() =>
        Assert.True(ui.UsesProductionResources,
            $"Run Category=ProductionResources in its own test process with {TestAppResourceLoader.ProfileVariable}=Production.");

    private sealed class BadgeProbe
    {
        public int Mounts { get; set; }
        public int Unmounts { get; set; }
        public Action? Refresh { get; set; }
        public Action? Toggle { get; set; }
    }

    private sealed record BadgeLifecycleProps(BadgeProbe Probe);

    private sealed class BadgeLifecycleView : Component<BadgeLifecycleProps>
    {
        public override Element Render()
        {
            var (revision, setRevision) = UseState(0);
            var (visible, setVisible) = UseState(true);
            Props.Probe.Refresh = () => setRevision(revision + 1);
            Props.Probe.Toggle = () => setVisible(!visible);
            var badge = PageHero.AttentionBadge("BadgeLifecycleStatus", "Failing", "synthetic failure",
                    "\uE783", PageHeroTone.Critical, reducedMotion: false, reconciliationKey: "status-slot")
                .OnMount(_ => Props.Probe.Mounts++)
                .OnUnmount(_ => Props.Probe.Unmounts++);
            return VStack(4,
                TextBlock(revision.ToString()).AutomationId("BadgeLifecycleRevision"),
                Button("Refresh", () => setRevision(revision + 1)),
                Button(visible ? "Remove" : "Add", () => setVisible(!visible)),
                visible ? badge : Empty());
        }
    }
}
