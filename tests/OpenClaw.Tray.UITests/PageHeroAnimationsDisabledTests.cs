// Native OS-preference proof. Run through Invoke-CiTest.ps1 -AnimationPreference Disabled.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Presentation;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

internal sealed class AnimationPreferenceDisabledFactAttribute : FactAttribute
{
    public AnimationPreferenceDisabledFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_TEST_ANIMATION_PREFERENCE") != "Disabled")
            Skip = "Run with Invoke-CiTest.ps1 -AnimationPreference Disabled. The required CI proof selects this case separately.";
    }
}

/// <summary>Proves the production preference gate without bypassing it or changing Windows settings in the testhost.</summary>
[Collection(UICollection.Name)]
public sealed class PageHeroAnimationsDisabledTests(UIThreadFixture ui)
{
    [AnimationPreferenceDisabledFact]
    [Trait("Category", "AnimationPreferenceDisabled")]
    public async Task DisabledPreference_SuppressesMotionAndHighlightButStillFocuses()
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        Border? target = null;
        Brush? original = null;
        long? backgroundCallback = null;
        var backgroundChanged = false;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
                    "The parent must establish the real disabled animation preference before starting this process.");
                var button = new Button { Content = "Focus target" };
                original = new SolidColorBrush(Microsoft.UI.Colors.SteelBlue);
                target = new Border { Background = original, Child = button };
                ui.Container.Children.Add(target);
                ui.Container.UpdateLayout();
                backgroundCallback = target.RegisterPropertyChangedCallback(Border.BackgroundProperty,
                    (_, _) => backgroundChanged |= !ReferenceEquals(target.Background, original));
                PageHero.RevealAndHighlight(target, reducedMotion: false);
                Assert.Same(original, target.Background);
                Assert.Same(button, FocusManager.GetFocusedElement(target.XamlRoot));
            });
            await ui.RunOnUIAsync(async () =>
            {
                await OnboardingNativeProof.NextCompositionAsync();
                Assert.False(backgroundChanged, "Reveal changed the background while Windows animations were disabled.");
                Assert.Same(original, target!.Background);
                renderer = new ReactorHostControl();
                renderer.Mount(_ => PageHero.WithMotion(Factories.VStack(Factories.TextBlock("No motion"))
                    .AutomationId("DisabledMotion"), reducedMotion: false));
                ui.Container.Children.Add(renderer);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<StackPanel>(renderer!).Any(panel =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(panel) == "DisabledMotion"),
                "disabled-motion container mounted"));
            await ui.RunOnUIAsync(() =>
            {
                var panel = Assert.Single(FindDescendants<StackPanel>(renderer!), element =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == "DisabledMotion");
                Assert.Empty(panel.Transitions);
                Assert.Empty(panel.ChildrenTransitions);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (target is not null)
                {
                    if (backgroundCallback is { } token)
                        target.UnregisterPropertyChangedCallback(Border.BackgroundProperty, token);
                    PageHero.RevealAndHighlight(target, reducedMotion: true);
                }
                ui.Container.Children.Clear();
                renderer?.Dispose();
            });
        }
    }
}
