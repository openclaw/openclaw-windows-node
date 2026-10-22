using System.Text.Json;
using System.Xml.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SetupCapabilitiesPresentationTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData("en-us")]
    [InlineData("fr-fr")]
    [InlineData("nl-nl")]
    [InlineData("pt-br")]
    [InlineData("zh-cn")]
    [InlineData("zh-tw")]
    public void PresentationCopy_IsPresentInEverySupportedLocale(string locale)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
        var strings = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"))
            .Descendants("data").ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);
        foreach (var key in new[] { "Onboarding_V2_CapabilitiesTitle.Text", "Onboarding_Capabilities_Speech.Text",
                     "Onboarding_V3_ProfileHelp.Text" })
        {
            Assert.False(string.IsNullOrWhiteSpace(strings[key]));
            Assert.DoesNotContain("\u2014", strings[key]);
        }
        if (locale == "en-us")
        {
            Assert.Equal("What can your assistant use?", strings["Onboarding_V2_CapabilitiesTitle.Text"]);
            Assert.Equal("You choose what I can use.", strings["Onboarding_Capabilities_Speech.Text"]);
            Assert.Contains("Fine-tune", strings["Onboarding_V3_ProfileHelp.Text"]);
        }
    }

    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    public async Task ProductionCapabilities_KeepProfilesToolsAndFooterReachable(
        ElementTheme theme, int width, int height)
    {
        await WithCapabilitiesAsync(SetupGatewayRoute.ManagedWsl, async (window, frame, page) =>
        {
            window.AppWindow.Resize(new(1800, 1400));
            page.Width = width;
            page.Height = height;
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(window.Content), theme);
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            Assert.Equal(width >= 960 && height >= 600, shell.IsWideLayout);
            var heading = Find<TextBlock>(page, "CapabilitiesTitle");
            Assert.Equal("What can your assistant use?", heading.Text);
            Assert.Equal(40, heading.FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(heading));
            Assert.Equal("Step 1 of 4", AutomationProperties.GetName(Find<SetupStepIndicator>(page, "FlowProgress")));
            var next = Find<Button>(page, "ContinueButton");
            var back = Find<Button>(page, "BackButton");
            Assert.Equal("Next", next.Content);
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(page, "NativeInstallConsent").Visibility);
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            var expander = Find<SettingsExpander>(page, "FineTuneExpander");
            expander.IsExpanded = true;
            await TestSupport.WaitForSettingsExpanderSettledAsync(ui, expander, true);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Assert.Equal(8, expander.Items.Count);
            Assert.True(next.Focus(FocusState.Keyboard));
            page.Width = width == 1120 ? 720 : 1120;
            page.Height = height == 720 ? 560 : 720;
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            Assert.Same(next, FocusManager.GetFocusedElement(page.XamlRoot));
            Assert.Same(expander, page.FindName("FineTuneExpander"));
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            page.Width = width;
            page.Height = height;
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            var selector = Find<ListView>(page, "ProfileSelector");
            var targets = selector.Items.Cast<FrameworkElement>()
                .Concat(expander.Items.Cast<FrameworkElement>())
                .Concat(new[] { "NodeModeToggle", "McpToggle", "OllamaToggle" }
                    .Select(name => Find<ToggleSwitch>(page, name)));
            foreach (var target in targets)
            {
                await BringIntoViewAsync(page, shell.Scroller, target);
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                OnboardingNativeProof.AssertFullyVisible(target, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(back, shell);
                OnboardingNativeProof.AssertFullyVisible(next, shell);
            }
            foreach (var item in expander.Items.Cast<SettingsCard>())
            {
                Assert.Equal(0, Assert.IsType<SolidColorBrush>(item.Background).Color.A);
                Assert.Equal(new CornerRadius(0), item.CornerRadius);
                var toggle = Assert.IsType<ToggleSwitch>(item.Content);
                Assert.StartsWith("SetupCapability", AutomationProperties.GetAutomationId(toggle));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)));
            }
            Assert.True(shell.Scroller.ViewportHeight >= 100);
            output.WriteLine($"Measured capabilities viewport={shell.ActualWidth}x{shell.ActualHeight}; theme={theme}; wide={shell.IsWideLayout}; tools={expander.Items.Count}; horizontalOverflow={shell.Scroller.ScrollableWidth}.");
            Assert.Same(page, frame.Content);
        });
    }

    [Theory]
    [InlineData(SetupCapabilityProfile.ReadOnly)]
    [InlineData(SetupCapabilityProfile.Standard)]
    [InlineData(SetupCapabilityProfile.Full)]
    public async Task Presets_ChangeOnlyTheirEightCapabilityFlags(SetupCapabilityProfile profile)
    {
        await WithCapabilitiesAsync(SetupGatewayRoute.ManagedWsl, (window, _, page) =>
        {
            var node = Find<ToggleSwitch>(page, "NodeModeToggle").IsOn;
            var mcp = Find<ToggleSwitch>(page, "McpToggle").IsOn;
            var ollama = Find<ToggleSwitch>(page, "OllamaToggle").IsOn;
            Find<ListView>(page, "ProfileSelector").SelectedIndex = (int)profile;
            SetupCapability[] expected = profile switch
            {
                SetupCapabilityProfile.ReadOnly => [SetupCapability.Canvas, SetupCapability.Screen],
                SetupCapabilityProfile.Standard => [SetupCapability.System, SetupCapability.Canvas, SetupCapability.Screen,
                    SetupCapability.Tts, SetupCapability.Stt],
                _ => [SetupCapability.System, SetupCapability.Canvas, SetupCapability.Screen, SetupCapability.Camera,
                    SetupCapability.Location, SetupCapability.Browser, SetupCapability.Tts, SetupCapability.Stt],
            };
            foreach (var capability in SetupCapabilityProfiles.Ordered)
                Assert.Equal(expected.Contains(capability), window.AccessDraft.GetCapability(capability));
            Assert.True(window.AccessDraft.Config.Capabilities.Device);
            Assert.Equal(node, Find<ToggleSwitch>(page, "NodeModeToggle").IsOn);
            Assert.Equal(mcp, Find<ToggleSwitch>(page, "McpToggle").IsOn);
            Assert.Equal(ollama, Find<ToggleSwitch>(page, "OllamaToggle").IsOn);
            Assert.False(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task CustomIntentAndTransportChoices_SurvivePageRecreationWithoutWrites()
    {
        await WithCapabilitiesAsync(SetupGatewayRoute.ManagedWsl, async (window, frame, page) =>
        {
            Find<ListView>(page, "ProfileSelector").SelectedIndex = (int)SetupCapabilityProfile.Standard;
            var expander = Find<SettingsExpander>(page, "FineTuneExpander");
            expander.IsExpanded = true;
            await TestSupport.WaitForSettingsExpanderSettledAsync(ui, expander, true);
            var system = Capability(page, SetupCapability.System);
            system.IsOn = false;
            system.IsOn = true;
            Assert.Equal(SetupCapabilityProfile.Custom, window.AccessDraft.Profile);
            Assert.Equal(-1, Find<ListView>(page, "ProfileSelector").SelectedIndex);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(page, "CustomProfileText").Visibility);
            Find<ToggleSwitch>(page, "OllamaToggle").IsOn = true;
            var flags = JsonSerializer.Serialize(window.AccessDraft.Config.Capabilities);
            Find<ToggleSwitch>(page, "NodeModeToggle").IsOn = false;
            Find<ToggleSwitch>(page, "McpToggle").IsOn = false;
            Assert.False(Find<ListView>(page, "ProfileSelector").IsEnabled);
            Assert.True(expander.IsEnabled);
            Assert.Equal(Visibility.Visible, Find<InfoBar>(page, "TransportRequired").Visibility);
            Assert.All(expander.Items.Cast<SettingsCard>(), card => Assert.False(Assert.IsType<ToggleSwitch>(card.Content).IsEnabled));
            Assert.Equal(flags, JsonSerializer.Serialize(window.AccessDraft.Config.Capabilities));
            Find<ToggleSwitch>(page, "McpToggle").IsOn = true;
            Assert.True(Find<ListView>(page, "ProfileSelector").IsEnabled);
            Assert.False(Capability(page, SetupCapability.Browser).IsEnabled);
            Assert.True(Find<ToggleSwitch>(page, "OllamaToggle").IsOn);
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            window.NavigateToSecurityNotice(back: true);
            await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is SecurityNoticePage { IsLoaded: true }, "return");
            window.NavigateToCapabilities();
            await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is CapabilitiesPage { IsLoaded: true }, "capabilities recreated");
            var returned = Assert.IsType<CapabilitiesPage>(frame.Content);
            Assert.NotSame(page, returned);
            Assert.True(Find<SettingsExpander>(returned, "FineTuneExpander").IsExpanded);
            Assert.Equal(-1, Find<ListView>(returned, "ProfileSelector").SelectedIndex);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
        });
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Native, true)]
    [InlineData(SetupGatewayRoute.ManagedWsl, false)]
    public async Task NativeDisclosure_StaysExplicitAndReachableWithoutStartingInstallation(SetupGatewayRoute route, bool visible)
    {
        await WithCapabilitiesAsync(route, async (window, _, page) =>
        {
            page.Width = 720;
            page.Height = 560;
            var consent = Find<TextBlock>(page, "NativeInstallConsent");
            var next = Find<Button>(page, "ContinueButton");
            Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, consent.Visibility);
            Assert.Equal(visible ? "Set up gateway" : "Next", next.Content);
            if (visible)
            {
                Assert.Contains("WinGet", consent.Text);
                Assert.Contains("package and Store source agreements", consent.Text);
                var shell = Assert.IsType<SetupPageShell>(page.Content);
                await BringIntoViewAsync(page, shell.Scroller, consent);
                OnboardingNativeProof.AssertFullyVisible(consent, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(next, shell);
            }
            Assert.False(window.AccessDraft.WslInspectionComplete);
        });
    }

    private async Task BringIntoViewAsync(Page page, ScrollViewer scroll, FrameworkElement target)
    {
        page.UpdateLayout();
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
        await ui.YieldToRenderAsync();
        await OnboardingNativeProof.NextCompositionAsync();
        page.UpdateLayout();
        var bounds = target.TransformToVisual(scroll).TransformBounds(new(0, 0, target.ActualWidth, target.ActualHeight));
        var offset = Math.Clamp(scroll.VerticalOffset + bounds.Top + (bounds.Height - scroll.ViewportHeight) / 2,
            0, scroll.ScrollableHeight);
        scroll.ChangeView(null, offset, null, disableAnimation: true);
        await TestSupport.WaitForRenderedConditionAsync(() => Math.Abs(scroll.VerticalOffset - offset) < 1, "capability viewport");
        await ui.YieldToRenderAsync();
    }

    private async Task WithCapabilitiesAsync(SetupGatewayRoute route, Func<SetupWindow, Frame, CapabilitiesPage, Task> assertion)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("capabilities-presentation-");
        var config = temp.Combine("config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: config,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"),
                    commandLineArgs: [], startupRegistrationAllowed: false));
                window.SelectGatewayRoute(route);
                window.NavigateToCapabilities();
                var frame = Find<Frame>(Assert.IsType<Grid>(window.Content), "RootFrame");
                window.Activate();
                window.AppWindow.Move(new(-32000, -32000));
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is CapabilitiesPage { IsLoaded: true }, "capabilities mounted");
                var page = Assert.IsType<CapabilitiesPage>(frame.Content);
                Find<OnboardingMascot>(page, "MascotHero").IsAnimationEnabled = false;
                await assertion(window, frame, page);
                Assert.False(File.Exists(temp.Combine("data", "settings.json")));
                Assert.False(File.Exists(temp.Combine("data", "gateways.json")));
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));
    private static ToggleSwitch Capability(CapabilitiesPage page, SetupCapability capability) =>
        Assert.Single(Find<SettingsExpander>(page, "FineTuneExpander").Items.Cast<SettingsCard>()
            .Select(card => Assert.IsType<ToggleSwitch>(card.Content)),
            toggle => AutomationProperties.GetAutomationId(toggle) == $"SetupCapability{capability}");
}
