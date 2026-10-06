using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayDetailShellTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("local", ElementTheme.Light, 1120, 720)]
    [InlineData("local", ElementTheme.Dark, 1120, 720)]
    [InlineData("local", ElementTheme.Light, 720, 560)]
    [InlineData("local", ElementTheme.Dark, 720, 560)]
    [InlineData("tailscale", ElementTheme.Light, 1120, 720)]
    [InlineData("tailscale", ElementTheme.Dark, 1120, 720)]
    [InlineData("tailscale", ElementTheme.Light, 720, 560)]
    [InlineData("tailscale", ElementTheme.Dark, 720, 560)]
    [InlineData("networking", ElementTheme.Light, 1120, 720)]
    [InlineData("networking", ElementTheme.Dark, 1120, 720)]
    [InlineData("networking", ElementTheme.Light, 720, 560)]
    [InlineData("networking", ElementTheme.Dark, 720, 560)]
    public async Task DetailShell_MountsExistingRouteAndRetainsDraftFocusAndFixedActions(
        string detail, ElementTheme theme, int width, int height)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("gateway-detail-shell-");
        using var preview = new EnvironmentScope("OPENCLAW_SETUP_PREVIEW_PAGE", "capabilities-review-consent");
        var configPath = temp.Combine("setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new SetupConfig { DistroName = "DetailShell-NoRealDistro" }));
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
                SeedHardware(window);
                var draft = window.AccessDraft;
                draft.SetLocalAiEnabled(detail == "local");
                window.AppWindow.Resize(new(1800, 1400));
                window.AppWindow.Move(new(-32000, -32000));
                switch (detail)
                {
                    case "local": window.NavigateToLocalAiSetup(); break;
                    case "tailscale": window.NavigateToTailscaleSetup(); break;
                    case "networking": window.NavigateToWslNetworking(returnToReview: true); break;
                }
                window.Activate();
                var frame = Assert.IsType<Frame>(Assert.IsType<Grid>(window.Content).FindName("RootFrame"));
                await TestSupport.WaitForRenderedConditionAsync(
                    () => frame.Content is GatewaySetupDetailPage { IsLoaded: true }, "detail mounted");
                var page = Assert.IsType<GatewaySetupDetailPage>(frame.Content);
                Assert.IsType<OnboardingMascot>(page.FindName("MascotHero")).IsAnimationEnabled = false;
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(window.Content), theme);
                page.Width = width;
                page.Height = height;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                var shell = Assert.IsType<SetupPageShell>(page.Content);
                Assert.Equal(width >= 960 && height >= 600, shell.IsWideLayout);
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                var title = Assert.IsType<TextBlock>(page.FindName("DetailTitle"));
                Assert.Equal(detail switch { "local" => "Local AI", "tailscale" => "Tailscale Serve", _ => "WSL networking consent" }, title.Text);
                Assert.Equal(40, title.FontSize);
                Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));
                Assert.Equal("Step 4 of 7", AutomationProperties.GetName(
                    Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress"))));
                var back = Assert.IsType<Button>(page.FindName("BackButton"));
                var primary = Assert.IsType<Button>(page.FindName("PrimaryButton"));
                Assert.Equal("Return to review", primary.Content);
                FrameworkElement target = detail switch
                {
                    "local" => Assert.IsType<LocalAiSetupControl>(page.FindName("LocalAiOptions")),
                    "tailscale" => Assert.IsType<TailscaleSetupControl>(page.FindName("TailscaleOptions")),
                    _ => Assert.IsType<StackPanel>(page.FindName("NetworkingOptions")),
                };
                Assert.Equal(Visibility.Visible, target.Visibility);
                if (detail == "local")
                {
                    var selector = Assert.IsType<ComboBox>(target.FindName("LocalAiModelSelector"));
                    await TestSupport.WaitForRenderedConditionAsync(() => selector.Items.Count > 0, "injected Local AI catalog");
                    Assert.NotNull(draft.Config.LocalAi.SelectedModelId);
                    target = selector;
                }
                if (detail == "networking")
                {
                    var consent = Assert.IsType<CheckBox>(page.FindName("LocalAiNetworkingConsentCheckBox"));
                    Assert.False(consent.IsChecked);
                    consent.IsChecked = true;
                    Assert.True(draft.Config.LocalAi.WslMirroredNetworkingConsent);
                    Assert.Same(page, frame.Content);
                    target = consent;
                }
                var snapshot = JsonSerializer.Serialize(draft.Config);
                page.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                if (width < 960)
                    Assert.True(shell.Scroller.ScrollableHeight > 0, "Compact details must expose a scrollable body.");
                target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await ui.YieldToRenderAsync();
                page.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                page.UpdateLayout();
                OnboardingNativeProof.AssertFullyVisible(target, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(back, shell);
                OnboardingNativeProof.AssertFullyVisible(primary, shell);
                Assert.True(primary.Focus(FocusState.Keyboard));
                var mascot = page.FindName("MascotHero");
                page.Width = width == 1120 ? 720 : 1120;
                page.Height = height == 720 ? 560 : 720;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.Same(mascot, page.FindName("MascotHero"));
                Assert.Same(primary, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.Equal(snapshot, JsonSerializer.Serialize(draft.Config));
                Assert.Same(page, frame.Content);
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

    private static void SeedHardware(SetupWindow window)
    {
        var field = typeof(SetupWindow).GetField("_localAiHardwareProbe", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = Activator.CreateInstance(field.FieldType,
            new Func<HostHardwareInfo>(() => OnboardingSetupGalleryData.Hardware("eligible")))!;
        field.SetValue(window, cache);
    }
}
