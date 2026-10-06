using System.Text.Json;
using System.Xml.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
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
public sealed class SetupAdvancedRoutesPresentationTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private static readonly string[] CardNames = ["ExistingRouteCard", "RemoteRouteCard", "McpRouteCard", "DeferredRouteCard"];

    [Theory]
    [InlineData("en-us")]
    [InlineData("fr-fr")]
    [InlineData("nl-nl")]
    [InlineData("pt-br")]
    [InlineData("zh-cn")]
    [InlineData("zh-tw")]
    public void AdvancedCopy_PreservesExistingRoutesAndLocalizesNewPresentation(string locale)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
        var strings = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"))
            .Descendants("data").ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);
        foreach (var key in new[] { "Onboarding_Advanced_Speech.Text", "Onboarding_Advanced_Help.Text",
                     "Onboarding_V2_AlternativesTitle.Text", "Onboarding_V2_ExistingRoute.Header",
                     "Onboarding_V2_RemoteRoute.Header", "Onboarding_V2_McpRoute.Header", "Onboarding_V2_DeferredRoute.Header" })
        {
            Assert.False(string.IsNullOrWhiteSpace(strings[key]));
            Assert.DoesNotContain("\u2014", strings[key]);
        }
        Assert.Contains("Gateway", strings["Onboarding_Advanced_Help.Text"]);
    }

    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    public async Task ProductionRoutes_KeepActionsFocusErrorAndBackReachable(ElementTheme theme, int width, int height)
    {
        await WithAdvancedAsync(async (window, frame, page) =>
        {
            window.AppWindow.Resize(new(1800, 1400));
            page.Width = width;
            page.Height = height;
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(window.Content), theme);
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            Assert.Equal(width >= 960 && height >= 600, shell.IsWideLayout);
            var heading = Find<TextBlock>(page, "AdvancedTitle");
            Assert.Equal("Choose another setup route", heading.Text);
            Assert.Equal(40, heading.FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(heading));
            Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress"));
            var cards = CardNames.Select(name => Find<SettingsCard>(page, name)).ToArray();
            Assert.Equal(4, TestSupport.FindDescendants<SettingsCard>(page).Count());
            var back = Find<Button>(page, "BackButton");
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            Assert.True(back.Focus(FocusState.Keyboard));
            page.Width = width == 1120 ? 720 : 1120;
            page.Height = height == 720 ? 560 : 720;
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            Assert.Same(back, FocusManager.GetFocusedElement(page.XamlRoot));
            Assert.All(cards, card => Assert.Same(card, page.FindName(card.Name)));
            page.Width = width;
            page.Height = height;
            foreach (var card in cards)
            {
                await BringIntoViewAsync(page, card);
                Assert.True(card.IsClickEnabled);
                Assert.Equal(0, Assert.IsType<SolidColorBrush>(card.Background).Color.A);
                Assert.Equal(new CornerRadius(0), card.CornerRadius);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(card)));
                Assert.True(card.Focus(FocusState.Keyboard));
                OnboardingNativeProof.AssertFullyVisible(card, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(back, shell);
            }
            TestSupport.InvokeSettingsCardAction(page, cards[0], "Existing_Click");
            var error = Find<InfoBar>(page, "HostUnavailable");
            Assert.True(error.IsOpen);
            Assert.Same(page, frame.Content);
            await BringIntoViewAsync(page, error);
            OnboardingNativeProof.AssertFullyVisible(error, shell.Scroller);
            OnboardingNativeProof.AssertFullyVisible(back, shell);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            Assert.True(shell.Scroller.ViewportHeight >= 100);
            output.WriteLine($"Measured alternatives viewport={shell.ActualWidth}x{shell.ActualHeight}; theme={theme}; wide={shell.IsWideLayout}; actions={cards.Length}; horizontalOverflow={shell.Scroller.ScrollableWidth}.");
        });
    }

    [Theory]
    [InlineData("ExistingRouteCard", "Existing_Click", SetupGatewayRoute.Existing, false)]
    [InlineData("RemoteRouteCard", "Remote_Click", SetupGatewayRoute.Remote, false)]
    [InlineData("McpRouteCard", "Mcp_Click", SetupGatewayRoute.McpOnly, true)]
    [InlineData("DeferredRouteCard", "Deferred_Click", SetupGatewayRoute.Deferred, true)]
    public async Task ExistingActions_SelectOnlyTheirRouteAndKeepCapabilityDraft(
        string cardName, string handler, SetupGatewayRoute route, bool capabilities)
    {
        await WithAdvancedAsync(async (window, frame, page) =>
        {
            window.AccessDraft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
            window.AccessDraft.SetOllamaSharing(true);
            var flags = JsonSerializer.Serialize(window.AccessDraft.Config.Capabilities);
            TestSupport.InvokeSettingsCardAction(page, Find<SettingsCard>(page, cardName), handler);
            Assert.Equal(route, window.AccessDraft.Route);
            if (capabilities)
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is CapabilitiesPage { IsLoaded: true }, "capability review");
            else
            {
                Assert.Same(page, frame.Content);
                Assert.True(Find<InfoBar>(page, "HostUnavailable").IsOpen);
            }
            Assert.Equal(flags, JsonSerializer.Serialize(window.AccessDraft.Config.Capabilities));
            Assert.True(window.AccessDraft.Config.Settings.NodeOllamaInferenceEnabled);
            Assert.Equal(route != SetupGatewayRoute.McpOnly, window.AccessDraft.Config.Settings.EnableNodeMode);
            Assert.Equal(route == SetupGatewayRoute.McpOnly, window.AccessDraft.Config.Settings.EnableMcpServer == true);
            Assert.False(window.AccessDraft.WslInspectionComplete);
            Assert.False(window.AccessDraft.Config.LocalAi.Enabled);
            Assert.False(window.AccessDraft.Config.Tailscale.Enabled);
        });
    }

    [Theory]
    [InlineData("ExistingRouteCard", "Existing_Click", SetupGatewayRoute.Existing)]
    [InlineData("RemoteRouteCard", "Remote_Click", SetupGatewayRoute.Remote)]
    public async Task NativeHostRoutes_OpenEditorWithoutCallingCheckOrConnect(string cardName, string handler, SetupGatewayRoute route)
    {
        var host = new InertNativeHost();
        await WithAdvancedAsync(async (window, frame, page) =>
        {
            TestSupport.InvokeSettingsCardAction(page, Find<SettingsCard>(page, cardName), handler);
            await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is SetupNativeConnectionPage { IsLoaded: true }, "connection editor");
            Assert.Equal(route, window.AccessDraft.Route);
            Assert.Equal(0, host.Calls);
        }, host);
    }

    [Fact]
    public async Task ExplicitClassicFallback_AndBackKeepTheDraft()
    {
        var classicRequests = 0;
        await WithAdvancedAsync(async (window, frame, page) =>
        {
            window.AccessDraft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            var classic = Find<HyperlinkButton>(page, "ClassicSettingsLink");
            Assert.Equal(Visibility.Visible, classic.Visibility);
            Invoke(classic);
            Assert.Equal(1, classicRequests);
            Assert.Same(page, frame.Content);
            Invoke(Find<Button>(page, "BackButton"));
            await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is WelcomePage { IsLoaded: true }, "gateway chooser");
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
        }, classic: () => classicRequests++);
    }

    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task NativeAdvancedRoutes_AxeAndVisibleStates(ElementTheme theme, int width, int height)
    {
        OnboardingNativeProof.RequireProofDirectory();
        OnboardingNativeProof.AssertSourceUnchanged();
        await WithAdvancedAsync(async (window, _, page) =>
        {
            var root = Assert.IsType<Grid>(window.Content);
            var scale = root.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new((int)Math.Ceiling((width + 40) * scale), (int)Math.Ceiling((height + 80) * scale)));
            page.Width = width;
            page.Height = height;
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            foreach (var name in CardNames)
            {
                var card = Find<SettingsCard>(page, name);
                await BringIntoViewAsync(page, card);
                OnboardingNativeProof.AssertFullyVisible(card, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(Find<Button>(page, "BackButton"), shell);
                using (await OnboardingNativeProof.CaptureAsync(window, $"advanced-{theme}-{width}-{name}", output,
                    [Assert.IsType<string>(card.Header), "Back"], requiredContent: card)) { }
            }
            await OnboardingNativeProof.InvokeSettingsCardAsync(window, Find<SettingsCard>(page, "ExistingRouteCard"));
            var error = Find<InfoBar>(page, "HostUnavailable");
            Assert.True(error.IsOpen);
            await BringIntoViewAsync(page, error);
            using (await OnboardingNativeProof.CaptureAsync(window, $"advanced-{theme}-{width}-host-unavailable", output,
                [error.Title, "Back"], requiredContent: error)) { }
            await Task.Run(() =>
            {
                AxeHelper.Initialize(Environment.ProcessId);
                AxeHelper.AssertNoAccessibilityErrors(handle, context: $"Advanced setup {theme} {width}x{height}");
            });
        }, nativeProof: true);
        OnboardingNativeProof.AssertSourceUnchanged();
    }

    private async Task BringIntoViewAsync(Page page, FrameworkElement target)
    {
        page.UpdateLayout();
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
        await ui.YieldToRenderAsync();
        await OnboardingNativeProof.NextCompositionAsync();
        page.UpdateLayout();
    }

    private async Task WithAdvancedAsync(Func<SetupWindow, Frame, AdvancedSetupPage, Task> assertion,
        ISetupNativeConnectionHost? host = null, Action? classic = null, bool nativeProof = false)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("advanced-routes-");
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
                    commandLineArgs: [], startupRegistrationAllowed: false, nativeConnectionHost: host));
                if (classic is not null) window.AdvancedSetupRequested += (_, _) => classic();
                window.NavigateToAdvancedSetup();
                var frame = Find<Frame>(Assert.IsType<Grid>(window.Content), "RootFrame");
                window.Activate();
                if (nativeProof) OnboardingNativeProof.ActivateOwned(window);
                else window.AppWindow.Move(new(-32000, -32000));
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is AdvancedSetupPage { IsLoaded: true }, "alternative routes mounted");
                var page = Assert.IsType<AdvancedSetupPage>(frame.Content);
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
    private static void Invoke(Control control) =>
        Assert.IsAssignableFrom<IInvokeProvider>(FrameworkElementAutomationPeer.CreatePeerForElement(control)
            .GetPattern(PatternInterface.Invoke)).Invoke();
    private sealed class InertNativeHost : ISetupNativeConnectionHost
    {
        public int Calls { get; private set; }
        public Task<SetupNativeConnectionResult> CheckAsync(SetupNativeConnectionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SetupNativeConnectionResult(false, Error: "Unexpected fixture check. No Gateway was contacted."));
        }
        public Task<SetupNativeConnectionResult> ConnectAsync(SetupNativeConnectionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SetupNativeConnectionResult(false, Error: "Unexpected fixture connection. Nothing was committed."));
        }
    }
}
