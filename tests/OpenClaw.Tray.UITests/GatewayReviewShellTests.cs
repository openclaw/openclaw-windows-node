using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayReviewShellTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720, true)]
    [InlineData(ElementTheme.Dark, 1120, 720, true)]
    [InlineData(ElementTheme.Light, 720, 560, false)]
    [InlineData(ElementTheme.Dark, 720, 560, false)]
    public async Task ReviewShell_KeepsConsentCommandsAndActionsReachable(
        ElementTheme theme, int width, int height, bool wide)
    {
        await WithReviewAsync(async (window, frame, page) =>
        {
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(window.Content), theme);
            page.Width = width;
            page.Height = height;
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            Assert.Equal(wide, shell.IsWideLayout);
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            var title = Assert.IsType<TextBlock>(page.FindName("ReviewTitle"));
            Assert.Equal("Ready to install", title.Text);
            Assert.Equal(40, title.FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));
            var primary = Assert.IsType<Button>(page.FindName("PrimaryButton"));
            var back = Assert.IsType<Button>(page.FindName("BackButton"));
            var consent = Assert.IsType<CheckBox>(page.FindName("ReplacementConsent"));
            var commands = Assert.IsType<SettingsExpander>(page.FindName("ExactCommandsExpander"));
            Assert.False(primary.IsEnabled);
            Assert.False(consent.IsChecked);
            Assert.Contains("ReviewShell-NoRealDistro", consent.Content.ToString());
            var rail = Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress"));
            Assert.Equal("Step 2 of 4", AutomationProperties.GetName(rail));
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            commands.IsExpanded = true;
            await TestSupport.WaitForSettingsExpanderSettledAsync(ui, commands, true);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            foreach (var target in new FrameworkElement[]
                     { consent, Assert.IsType<TextBlock>(page.FindName("ExactCommandsText")) })
            {
                page.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await ui.YieldToRenderAsync();
                page.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                page.UpdateLayout();
                OnboardingNativeProof.AssertFullyVisible(target, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(back, shell);
                OnboardingNativeProof.AssertFullyVisible(primary, shell);
            }
            consent.IsChecked = true;
            Assert.True(primary.IsEnabled);
            Assert.True(window.AccessDraft.ReplacementConfirmed);
            Assert.Same(page, frame.Content);
            Assert.True(consent.Focus(FocusState.Keyboard));
            var mascot = page.FindName("MascotHero");
            page.Width = wide ? 720 : 1120;
            page.Height = wide ? 560 : 720;
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            Assert.Same(mascot, page.FindName("MascotHero"));
            Assert.Same(consent, FocusManager.GetFocusedElement(page.XamlRoot));
            Assert.True(consent.IsChecked);
            Assert.True(commands.IsExpanded);
            Assert.True(window.AccessDraft.ReplacementConfirmed);
            consent.IsChecked = false;
            Assert.False(primary.IsEnabled);
            Assert.False(window.AccessDraft.ReplacementConfirmed);
        });
    }

    private async Task WithReviewAsync(Func<SetupWindow, Frame, GatewaySetupPage, Task> assertion)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("gateway-review-shell-");
        var configPath = temp.Combine("setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new SetupConfig { DistroName = "ReviewShell-NoRealDistro" }));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"),
                    commandLineArgs: [], startupRegistrationAllowed: false));
                var draft = window.AccessDraft;
                draft.RecordWslInspection(new ExistingConfigDetector.ExistingConfig(
                    HasLocalGateway: false, LocalGatewayId: null, LocalGatewayUrl: null,
                    HasDistro: true, HasDistroDataDirectory: true, DistroIsAppOwned: false,
                    DistroName: draft.Config.DistroName, HasIdentityFiles: false,
                    PreservedGatewayCount: 0, PreservedGatewayNames: []));
                window.AppWindow.Resize(new(1800, 1400));
                window.AppWindow.Move(new(-32000, -32000));
                window.NavigateToGatewaySetup();
                window.Activate();
                var frame = Assert.IsType<Frame>(Assert.IsType<Grid>(window.Content).FindName("RootFrame"));
                await TestSupport.WaitForRenderedConditionAsync(
                    () => frame.Content is GatewaySetupPage { IsLoaded: true }, "review mounted");
                var page = Assert.IsType<GatewaySetupPage>(frame.Content);
                Assert.IsType<OnboardingMascot>(page.FindName("MascotHero")).IsAnimationEnabled = false;
                await assertion(window, frame, page);
                Assert.False(File.Exists(temp.Combine("data", "gateways.json")));
                Assert.False(File.Exists(temp.Combine("data", "settings.json")));
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
