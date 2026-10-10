using System.Text.Json;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using Xunit.Abstractions;
using Uia = System.Windows.Automation;
using OpenClaw.Connection;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class OnboardingWindowsFlowTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(SetupGatewayRoute.Existing, false)]
    [InlineData(SetupGatewayRoute.Remote, false)]
    [InlineData(SetupGatewayRoute.Existing, true)]
    [InlineData(SetupGatewayRoute.Remote, true)]
    public async Task CommittedGatewayChangeDuringCapabilitiesNeverConnectsToReplacement(
        SetupGatewayRoute route, bool sameId)
    {
        await using var replacement = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateNativeSetup((_, _) => throw new InvalidOperationException("Must not invoke replacement")),
            "replacement-fixture-token");
        await WithWindowAsync(async (window, frame, data) =>
        {
            var registry = new GatewayRegistry(data);
            var committed = registry.AddOrUpdate(new() { Id = "committed-a", Url = "wss://committed.invalid" });
            registry.SetActive(committed.Id);
            registry.Save();
            Assert.True(window.AccessDraft.TryAcceptNativeConnection(route,
                new(true, true, committed.Id, committed.Url, EndpointBinding: GatewayDashboardBinding.Capture(committed))));
            window.NavigateToCapabilities();
            await MountedPageAsync<CapabilitiesPage>(window, frame);
            var other = registry.AddOrUpdate(new()
            {
                Id = sameId ? committed.Id : "replacement-b", Url = replacement.Endpoint.ToString(),
                SharedGatewayToken = "replacement-fixture-token",
            });
            registry.SetActive(other.Id);
            registry.Save();
            Assert.True(window.TryNavigateToWizard());
            var page = await MountedPageAsync<AiSetupPage>(window, frame);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen &&
                Find<Button>(page, "RefreshButton").IsEnabled, "changed committed Gateway refusal");
            Assert.Equal(committed.Id, window.AccessDraft.NativeGatewayId);
            Assert.Equal(0, replacement.ConnectionCount);
            Assert.Empty(replacement.Requests);
        });
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Native)]
    [InlineData(SetupGatewayRoute.ManagedWsl)]
    public async Task Capabilities_ConsentPrecedesNativeInstallationOnly(SetupGatewayRoute route)
    {
        await WithWindowAsync(async (window, frame, data) =>
        {
            window.SelectGatewayRoute(route);
            window.NavigateToCapabilities();
            var page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            var consent = Find<TextBlock>(page, "NativeInstallConsent");
            var next = Find<Button>(page, "ContinueButton");
            var native = route == SetupGatewayRoute.Native;
            Assert.Equal(native ? Visibility.Visible : Visibility.Collapsed, consent.Visibility);
            Assert.Equal(native ? "Set up gateway" : "Next", next.Content);
            Assert.Same(page, frame.Content);
            Assert.False(File.Exists(Path.Combine(data, "gateways.json")));
            Assert.False(window.AccessDraft.WslInspectionComplete);
            if (native)
            {
                Assert.Contains("WinGet", consent.Text);
                Assert.Contains("accept the package and Store source agreements", consent.Text);
                var scale = page.XamlRoot.RasterizationScale;
                window.AppWindow.Resize(new((int)Math.Ceiling(720 * scale), (int)Math.Ceiling(560 * scale)));
                await ui.YieldToRenderAsync();
                await BringIntoViewportAsync(page, consent);
                AssertFullyVisible(consent, PageScroll(page));
                AssertFullyVisible(next, Assert.IsType<Grid>(window.Content));
                Assert.False(consent.IsTextTrimmed);
                await SavePageProofAsync(window, page, "native-winget-consent-before-install");
            }
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task HeroViewport_FineTuneKeepsHeadingFooterAndNativeTogglesVisible(ElementTheme theme)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.NavigateToCapabilities();
            var page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            var root = Assert.IsType<Grid>(window.Content);
            var originalSize = window.AppWindow.Size;
            var scroll = PageScroll(page);
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            AssertFixedPageChrome(root, page);
            var selector = Find<ListView>(page, "ProfileSelector");
            Assert.Equal(3, selector.Items.Count);
            Assert.Equal(ListViewSelectionMode.Single, selector.SelectionMode);
            Assert.True(selector.SingleSelectionFollowsFocus);
            foreach (var item in selector.Items)
            {
                var row = Assert.IsType<ListViewItem>(item);
                await BringIntoViewportAsync(page, row);
                Assert.InRange(row.ActualWidth, selector.ActualWidth - 8, selector.ActualWidth);
                AssertFullyVisible(row, scroll);
                await AssertNativeProfileSelectionAsync(handle, AutomationProperties.GetName(selector),
                    AutomationProperties.GetName(row), select: true, requireVisible: true);
                AssertFixedPageChrome(root, page);
            }
            var full = Assert.IsType<ListViewItem>(selector.Items[2]);
            var editor = Find<SettingsExpander>(page, "FineTuneExpander");
            var label = Find<TextBlock>(page, "CustomProfileText");
            var summary = Find<TextBlock>(page, "SelectedSummary");
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            await ExpandCapabilitiesAsync(page);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Assert.Equal(SetupCapabilityProfile.Full, window.AccessDraft.Profile);
            Capability(page, SetupCapability.Camera).IsOn = false;
            Assert.Equal(-1, selector.SelectedIndex);
            Assert.Empty(selector.SelectedItems);
            Assert.True(editor.IsExpanded);
            Assert.Equal("Custom capabilities", label.Text);
            Assert.False(label.IsTextTrimmed);
            var flow = Assert.IsType<StackPanel>(selector.Parent);
            Assert.Same(flow, editor.Parent);
            Assert.Same(summary, editor.Description);
            root.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync();
            root.UpdateLayout();
            Assert.True(Bounds(full, flow).Bottom <= Bounds(editor, flow).Top,
                $"Full row overlaps Fine-tune: row={Bounds(full, flow)}, editor={Bounds(editor, flow)}.");
            Assert.True(Bounds(label, editor).Top >= 0);
            await BringIntoViewportAsync(page, summary);
            AssertFullyVisible(label, scroll);
            AssertFullyVisible(summary, scroll);
            Assert.False(summary.IsTextTrimmed);
            AssertFixedPageChrome(root, page);
            using (await OnboardingNativeProof.CaptureAsync(window, $"onboarding-viewport-custom-{theme}", output,
                ["PC capabilities", "Custom capabilities", "Back", "Next"], new
                {
                    selected = selector.SelectedIndex, fullRow = Bounds(full, root),
                    editorLabel = Bounds(label, root), viewport = Bounds(scroll, root), size = originalSize,
                })) { }

            var rows = editor.Items;
            Assert.Equal(8, rows.Count);
            foreach (var capability in SetupCapabilityProfiles.Ordered)
            {
                var toggle = Capability(page, capability);
                var card = Assert.Single(rows.Cast<SettingsCard>(), candidate => ReferenceEquals(candidate.Content, toggle));
                Assert.False(string.IsNullOrWhiteSpace(card.Header?.ToString()));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)));
                await BringIntoViewportAsync(page, card);
                AssertFullyVisible(card, scroll);
                AssertFullyVisible(toggle, scroll);
                AssertFixedPageChrome(root, page);
                await AssertNativeVisibleToggleAsync(handle, AutomationProperties.GetName(toggle),
                    AutomationProperties.GetAutomationId(toggle), toggle.IsOn, checkBox: false);
            }
            using (await OnboardingNativeProof.CaptureAsync(window, $"onboarding-viewport-custom-last-toggle-{theme}", output,
                ["PC capabilities", "Back", "Next"], new { viewport = Bounds(scroll, root), lastToggle = Bounds(Capability(page, SetupCapability.Stt), root) })) { }
            Assert.Equal(originalSize, window.AppWindow.Size);
            OnboardingNativeProof.AssertSourceUnchanged();
        }, nativeProof: true, theme: theme);
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task HeroViewport_ReplacementSummaryAndExactDistroConsentScrollWithoutClipping(ElementTheme theme)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            RecordInspection(window.AccessDraft, replacement: true);
            window.NavigateToGatewaySetup();
            var page = await MountedPageAsync<GatewaySetupPage>(window, frame);
            var root = Assert.IsType<Grid>(window.Content);
            var originalSize = window.AppWindow.Size;
            var scroll = PageScroll(page);
            var summary = Find<InfoBar>(page, "ReplacementWarning");
            var consent = Find<CheckBox>(page, "ReplacementConsent");
            var options = Find<StackPanel>(page, "ReplacementOptions");
            Assert.Same(options, summary.Parent);
            Assert.Same(options, consent.Parent);
            Assert.True(Bounds(summary, options).Bottom <= Bounds(consent, options).Top);
            Assert.Equal(window.AccessDraft.ReplacementSummary, summary.Message);
            Assert.False(string.IsNullOrWhiteSpace(summary.Message));
            Assert.Equal(Visibility.Collapsed, Find<InfoBar>(page, "ReviewRequired").Visibility);
            foreach (var text in TestSupport.FindDescendants<TextBlock>(summary))
                Assert.False(text.IsTextTrimmed);
            AssertFixedPageChrome(root, page);
            await BringIntoViewportAsync(page, summary);
            AssertFullyVisible(summary, scroll);
            AssertFixedPageChrome(root, page);
            using (await OnboardingNativeProof.CaptureAsync(window, $"onboarding-viewport-replacement-summary-{theme}", output,
                ["WSL Gateway setup", window.AccessDraft.Config.DistroName, "Back", "Install"],
                new { summary = summary.Message, summaryBounds = Bounds(summary, root), viewport = Bounds(scroll, root), size = originalSize })) { }
            await BringIntoViewportAsync(page, consent);
            AssertFullyVisible(consent, scroll);
            AssertFixedPageChrome(root, page);
            var name = Assert.IsType<string>(consent.Content);
            Assert.Equal($"I confirm replacement of the WSL installation '{window.AccessDraft.Config.DistroName}' and understand its data may be permanently deleted.", name);
            Assert.Contains(window.AccessDraft.Config.DistroName, name);
            await AssertNativeVisibleToggleAsync(WinRT.Interop.WindowNative.GetWindowHandle(window),
                name, automationId: null, isOn: false, checkBox: true);
            foreach (var text in TestSupport.FindDescendants<TextBlock>(consent))
                Assert.False(text.IsTextTrimmed);
            Assert.False(consent.IsChecked);
            Assert.False(window.AccessDraft.ReplacementConfirmed);
            Assert.False(Find<Button>(page, "PrimaryButton").IsEnabled);
            Assert.Empty(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot));
            using (await OnboardingNativeProof.CaptureAsync(window, $"onboarding-viewport-inline-consent-{theme}", output,
                ["WSL Gateway setup", window.AccessDraft.Config.DistroName, "Back", "Install"],
                new { exactAccessibleName = name, consentBounds = Bounds(consent, root), viewport = Bounds(scroll, root) })) { }
            Assert.Same(page, frame.Content);
            Assert.Equal(originalSize, window.AppWindow.Size);
            OnboardingNativeProof.AssertSourceUnchanged();
        }, nativeProof: true, theme: theme);
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task ProfileRows_AreNativeFullWidthSingleSelectionAndKeyboardAccessible()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.NavigateToCapabilities();
            var page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            var selector = Find<ListView>(page, "ProfileSelector");
            Assert.Equal(ListViewSelectionMode.Single, selector.SelectionMode);
            Assert.True(selector.SingleSelectionFollowsFocus);
            Assert.Equal(3, selector.Items.Count);
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var listName = AutomationProperties.GetName(selector);
            Assert.Equal("Capability profile", listName);
            var previousBottom = double.MinValue;
            for (var index = 0; index < selector.Items.Count; index++)
            {
                var row = Assert.IsType<ListViewItem>(selector.Items[index]);
                row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.True(row.IsLoaded);
                Assert.InRange(row.ActualWidth, selector.ActualWidth - 8, selector.ActualWidth);
                var top = row.TransformToVisual(selector).TransformPoint(new Windows.Foundation.Point()).Y;
                Assert.True(top >= previousBottom);
                previousBottom = top + row.ActualHeight;
                var rowName = AutomationProperties.GetName(row);
                Assert.False(string.IsNullOrWhiteSpace(rowName));
                await AssertNativeProfileSelectionAsync(handle, listName, rowName, select: true);
                Assert.True(selector.DispatcherQueue.HasThreadAccess);
                Assert.True(row.IsSelected);
                Assert.Equal(index, selector.SelectedIndex);
                Assert.Single(selector.SelectedItems);
                Assert.Equal((SetupCapabilityProfile)index, window.AccessDraft.Profile);
                AssertCapabilities(page, window.AccessDraft, SetupCapabilityProfiles.Ordered
                    .Where(capability => SetupCapabilityProfiles.Contains((SetupCapabilityProfile)index, capability)).ToArray());
            }
            var first = Assert.IsType<ListViewItem>(selector.Items[0]);
            await AssertNativeProfileSelectionAsync(handle, listName, AutomationProperties.GetName(first), select: true);
            Assert.Equal(0, selector.SelectedIndex);
            FocusOwnedControl(window, first);
            SendKey(0x28);
            await WaitAsync(() => selector.SelectedIndex == 1, "Down-arrow profile selection");
            Assert.Equal(SetupCapabilityProfile.Standard, window.AccessDraft.Profile);
            var standard = Assert.IsType<ListViewItem>(selector.Items[1]);
            Assert.True(standard.IsSelected);
            await AssertNativeProfileSelectionAsync(handle, listName, AutomationProperties.GetName(standard), select: false);
            AssertCapabilities(page, window.AccessDraft,
                [SetupCapability.System, SetupCapability.Canvas, SetupCapability.Screen, SetupCapability.Tts, SetupCapability.Stt]);
            SendKey(0x28);
            await WaitAsync(() => selector.SelectedIndex == 2, "Down-arrow Full selection");
            Assert.Equal(SetupCapabilityProfile.Full, window.AccessDraft.Profile);
            var full = Assert.IsType<ListViewItem>(selector.Items[2]);
            Assert.True(full.IsSelected);
            await AssertNativeProfileSelectionAsync(handle, listName, AutomationProperties.GetName(full), select: false);
            AssertCapabilities(page, window.AccessDraft, SetupCapabilityProfiles.Ordered.ToArray());
            FocusOwnedControl(window, full);
            SendKey(0x20);
            await ui.YieldToRenderAsync();
            Assert.Equal(2, selector.SelectedIndex);
            await AssertNativeProfileSelectionAsync(handle, listName, AutomationProperties.GetName(full), select: false);
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            await ExpandCapabilitiesAsync(page);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Assert.Equal(SetupCapabilityProfile.Full, window.AccessDraft.Profile);
            AssertCapabilities(page, window.AccessDraft, SetupCapabilityProfiles.Ordered.ToArray());
            Assert.True(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            await SavePageProofAsync(window, page, "onboarding-windows-native-fine-tune-inspection");
            selector.SelectedIndex = -1;
            Assert.Equal(2, selector.SelectedIndex);
            Assert.Single(selector.SelectedItems);
            await AssertNativeProfileSelectionAsync(handle, listName, AutomationProperties.GetName(full), select: true);
            Assert.True(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            await SavePageProofAsync(window, page, "onboarding-windows-native-profile-rows");
        });
    }

    [Fact]
    public async Task CustomIntent_PreservesPresetValuesAndEditorAcrossPageRecreation()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.NavigateToCapabilities();
            var page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Find<ListView>(page, "ProfileSelector").SelectedIndex = (int)SetupCapabilityProfile.Standard;
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            await ExpandCapabilitiesAsync(page);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Capability(page, SetupCapability.Camera).IsOn = true;
            Capability(page, SetupCapability.Camera).IsOn = false;
            var summary = Find<TextBlock>(page, "SelectedSummary").Text;
            Assert.DoesNotContain("Camera", summary);
            Assert.True(window.AccessDraft.IsCustomizingCapabilities);
            Assert.Equal(SetupCapabilityProfile.Standard, SetupCapabilityProfiles.Detect(window.AccessDraft.Config.Capabilities));
            Assert.Equal(SetupCapabilityProfile.Custom, window.AccessDraft.Profile);
            Assert.Equal(summary, Find<TextBlock>(page, "SelectedSummary").Text);
            Assert.True(Find<TextBlock>(page, "SelectedSummary").ActualHeight > 0);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(page, "CustomProfileText").Visibility);
            Assert.Equal(-1, Find<ListView>(page, "ProfileSelector").SelectedIndex);

            Invoke(FooterButton(page, next: true));
            var review = await MountedPageAsync<GatewaySetupPage>(window, frame);
            Invoke(FooterButton(review, next: false));
            page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Assert.True(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            Assert.Equal(summary, Find<TextBlock>(page, "SelectedSummary").Text);
            Assert.Equal(-1, Find<ListView>(page, "ProfileSelector").SelectedIndex);
            Assert.Empty(Find<ListView>(page, "ProfileSelector").SelectedItems);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(page, "CustomProfileText").Visibility);
            await SavePageProofAsync(window, page, "onboarding-windows-custom-retained");
            await ExpandCapabilitiesAsync(page);
            Assert.False(Capability(page, SetupCapability.Camera).IsOn);
            Assert.Equal(SetupCapabilityProfile.Custom, window.AccessDraft.Profile);
            await SavePageProofAsync(window, page, "onboarding-windows-custom-expanded");
            Find<ListView>(page, "ProfileSelector").SelectedIndex = (int)SetupCapabilityProfile.Full;
            Assert.False(window.AccessDraft.IsCustomizingCapabilities);
            Assert.True(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(page, "CustomProfileText").Visibility);
            AssertCapabilities(page, window.AccessDraft, SetupCapabilityProfiles.Ordered.ToArray());
        });
    }

    [Theory]
    [InlineData(SetupCapabilityProfile.ReadOnly, "Canvas,Screen")]
    [InlineData(SetupCapabilityProfile.Standard, "System,Canvas,Screen,Tts,Stt")]
    [InlineData(SetupCapabilityProfile.Full, "System,Canvas,Screen,Camera,Location,Browser,Tts,Stt")]
    public async Task Profiles_ChangeExactlyEightCapabilitiesWithoutChangingTransports(
        SetupCapabilityProfile profile, string enabled)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.NavigateToCapabilities();
            var page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Find<ToggleSwitch>(page, "McpToggle").IsOn = true;
            Find<ToggleSwitch>(page, "OllamaToggle").IsOn = true;
            Find<ListView>(page, "ProfileSelector").SelectedIndex = (int)profile;
            Assert.False(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(page, "SelectedSummary").Text));

            var expected = enabled.Split(',').Select(Enum.Parse<SetupCapability>).ToArray();
            AssertCapabilities(page, window.AccessDraft, expected);
            Assert.Equal(profile, window.AccessDraft.Profile);
            Assert.False(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            Assert.True(window.AccessDraft.Config.Settings.EnableNodeMode);
            Assert.True(window.AccessDraft.Config.Settings.EnableMcpServer);
            Assert.True(window.AccessDraft.Config.Settings.NodeOllamaInferenceEnabled);
            Assert.False(window.AccessDraft.Config.LocalAi.Enabled);
            Assert.True(window.AccessDraft.Config.Capabilities.Device);
            Assert.Equal(8, Find<SettingsExpander>(page, "FineTuneExpander").Items.Count);
            Assert.False(Capability(page, SetupCapability.Browser).IsEnabled);
            Assert.Equal(profile == SetupCapabilityProfile.Full, Capability(page, SetupCapability.Browser).IsOn);
            AssertProgress(page, stageCount: 7, current: 2);
            await SavePageProofAsync(window, page, $"onboarding-windows-profile-{profile}");
        });
    }

    [Fact]
    public async Task CustomChoices_SurviveNodeAndMcpGatesAndGatewayAvailabilityChanges()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.NavigateToCapabilities();
            var page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Find<ListView>(page, "ProfileSelector").SelectedIndex = (int)SetupCapabilityProfile.Full;
            await ExpandCapabilitiesAsync(page);
            Capability(page, SetupCapability.Camera).IsOn = false;
            var selected = new[] { SetupCapability.System, SetupCapability.Canvas, SetupCapability.Screen,
                SetupCapability.Location, SetupCapability.Browser, SetupCapability.Tts, SetupCapability.Stt };
            Assert.Equal(SetupCapabilityProfile.Custom, window.AccessDraft.Profile);
            Assert.Equal(-1, Find<ListView>(page, "ProfileSelector").SelectedIndex);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(page, "CustomProfileText").Visibility);

            Find<ToggleSwitch>(page, "OllamaToggle").IsOn = true;
            Find<ToggleSwitch>(page, "NodeModeToggle").IsOn = false;
            Assert.False(Find<ListView>(page, "ProfileSelector").IsEnabled);
            Assert.Equal(Visibility.Visible, Find<InfoBar>(page, "TransportRequired").Visibility);
            Assert.All(SetupCapabilityProfiles.Ordered, capability => Assert.False(Capability(page, capability).IsEnabled));
            AssertCapabilities(page, window.AccessDraft, selected);
            var fineTune = Find<SettingsExpander>(page, "FineTuneExpander");
            Assert.True(fineTune.IsEnabled);
            fineTune.IsExpanded = false;
            await TestSupport.WaitForSettingsExpanderSettledAsync(ui, fineTune, false);
            Assert.False(window.AccessDraft.FineTuneExpanded);
            await ExpandCapabilitiesAsync(page);
            Assert.True(window.AccessDraft.FineTuneExpanded);
            Assert.Equal(SetupCapabilityProfile.Custom, window.AccessDraft.Profile);
            AssertCapabilities(page, window.AccessDraft, selected);

            Find<ToggleSwitch>(page, "McpToggle").IsOn = true;
            Assert.True(Find<ListView>(page, "ProfileSelector").IsEnabled);
            Assert.True(Capability(page, SetupCapability.Screen).IsEnabled);
            Assert.False(Capability(page, SetupCapability.Browser).IsEnabled);
            Assert.True(Capability(page, SetupCapability.Browser).IsOn);
            await SavePageProofAsync(window, page, "onboarding-windows-custom-mcp-only");

            // This is a draft presentation input, not evidence of a connected Gateway.
            window.SelectGatewayRoute(SetupGatewayRoute.Existing, gatewayAvailable: true);
            window.NavigateToCapabilities();
            page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Assert.True(Find<SettingsExpander>(page, "FineTuneExpander").IsExpanded);
            await ExpandCapabilitiesAsync(page);
            Assert.False(Capability(page, SetupCapability.Browser).IsEnabled);
            Find<ToggleSwitch>(page, "NodeModeToggle").IsOn = true;
            Assert.True(Capability(page, SetupCapability.Browser).IsEnabled);
            Assert.DoesNotContain("Requires Node mode", BrowserCard(page).Description.ToString());
            Find<ToggleSwitch>(page, "McpToggle").IsOn = false;
            AssertCapabilities(page, window.AccessDraft, selected);
            Assert.True(Find<ToggleSwitch>(page, "OllamaToggle").IsOn);

            window.SelectGatewayRoute(SetupGatewayRoute.Existing, gatewayAvailable: false);
            window.NavigateToCapabilities();
            page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            await ExpandCapabilitiesAsync(page);
            Assert.False(Capability(page, SetupCapability.Browser).IsEnabled);
            Assert.True(Capability(page, SetupCapability.Browser).IsOn);
            AssertCapabilities(page, window.AccessDraft, selected);
        });
    }

    [Fact]
    public async Task ManagedFlow_ReviewsCapabilitiesWithoutPrivacyPanelAndBackRetainsTheDraft()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            // No WSL detection or installation runs. The review consumes synthetic inspection facts.
            RecordInspection(window.AccessDraft, replacement: true);
            window.NavigateToCapabilities();
            var capabilities = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Find<ListView>(capabilities, "ProfileSelector").SelectedIndex = (int)SetupCapabilityProfile.Full;
            await ExpandCapabilitiesAsync(capabilities);
            Capability(capabilities, SetupCapability.Location).IsOn = false;
            Find<ToggleSwitch>(capabilities, "McpToggle").IsOn = true;
            var beforePrivacy = JsonSerializer.Serialize(window.AccessDraft.Config);

            Assert.Null(capabilities.FindName("WindowsAccess"));
            Assert.Equal(beforePrivacy, JsonSerializer.Serialize(window.AccessDraft.Config));
            AssertProgress(capabilities, stageCount: 7, current: 2);
            await SavePageProofAsync(window, capabilities, "onboarding-capabilities-presets");

            Invoke(FooterButton(capabilities, next: true));
            var review = await MountedPageAsync<GatewaySetupPage>(window, frame);
            AssertProgress(review, stageCount: 7, current: 3);
            AssertReviewReplacement(review);
            Assert.Equal(Visibility.Collapsed, Find<InfoBar>(review, "ReviewRequired").Visibility);
            Assert.True(Find<InfoBar>(review, "ReplacementWarning").IsOpen);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(review, "ReplacementOptions").Visibility);
            Assert.Contains(window.AccessDraft.Config.DistroName, Find<SettingsCard>(review, "DistroCard").Description.ToString());
            Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(review, "ExactCommandsText").Text));
            Assert.False(window.AccessDraft.ReplacementConfirmed);
            await SavePageProofAsync(window, review, "onboarding-windows-review-consent-required");

            Find<ToggleSwitch>(review, "StartupPreferenceToggle").IsOn = false;
            Invoke(FooterButton(review, next: false));
            capabilities = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Assert.Null(capabilities.FindName("WindowsAccess"));
            await ExpandCapabilitiesAsync(capabilities);
            Assert.Equal(SetupCapabilityProfile.Custom, window.AccessDraft.Profile);
            Assert.False(Capability(capabilities, SetupCapability.Location).IsOn);
            Assert.True(Find<ToggleSwitch>(capabilities, "McpToggle").IsOn);
            Assert.True(Capability(capabilities, SetupCapability.Browser).IsOn);
            Assert.False(Capability(capabilities, SetupCapability.Browser).IsEnabled);
            Assert.Equal(beforePrivacy, JsonSerializer.Serialize(window.AccessDraft.Config));

            Invoke(FooterButton(capabilities, next: true));
            review = await MountedPageAsync<GatewaySetupPage>(window, frame);
            Assert.False(Find<ToggleSwitch>(review, "StartupPreferenceToggle").IsOn);
            AssertReviewReplacement(review);
        });
    }

    [Fact]
    public async Task Capabilities_SelectionChangesStayInTheCapabilityDraft()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.NavigateToCapabilities();
            var page = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Assert.Null(page.FindName("WindowsAccess"));
            Find<ListView>(page, "ProfileSelector").SelectedIndex = (int)SetupCapabilityProfile.Full;
            await ExpandCapabilitiesAsync(page);
            Capability(page, SetupCapability.Camera).IsOn = false;
            Find<ToggleSwitch>(page, "NodeModeToggle").IsOn = false;
            Find<ToggleSwitch>(page, "McpToggle").IsOn = true;
            Assert.Equal(SetupCapabilityProfile.Custom, window.AccessDraft.Profile);
            Assert.True(FooterButton(page, next: true).IsEnabled);
        });
    }

    [Fact]
    public async Task ReplacementConsent_IsInlineAndNeverInstallsOrNavigatesOnCheck()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            RecordInspection(window.AccessDraft, replacement: true);
            window.NavigateToGatewaySetup();
            var review = await MountedPageAsync<GatewaySetupPage>(window, frame);
            Assert.True(Find<SettingsCard>(review, "LocalAiCard").IsClickEnabled);
            Assert.False(window.AccessDraft.Config.LocalAi.Enabled);
            Assert.False(window.AccessDraft.Config.Tailscale.Enabled);

            // The inline control stays off, so this fixture never starts the Windows status CLI.
            var tailscale = Find<TailscaleSetupControl>(review, "TailscaleOptions");
            Assert.Equal(Visibility.Visible, tailscale.Visibility);
            Assert.False(Find<ToggleSwitch>(tailscale, "TailscaleToggle").IsOn);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(tailscale, "TailscaleOptions").Visibility);
            Assert.False(window.AccessDraft.TailscaleReady);
            Assert.Same(review, frame.Content);
            await SavePageProofAsync(window, review, "onboarding-windows-tailscale-off");
            AssertReviewReplacement(review);
            var consent = Find<CheckBox>(review, "ReplacementConsent");
            Assert.False(consent.IsChecked);
            Assert.Contains(window.AccessDraft.Config.DistroName, consent.Content.ToString());
            Assert.Equal(window.AccessDraft.ReplacementSummary, Find<InfoBar>(review, "ReplacementWarning").Message);
            Assert.False(window.AccessDraft.CanInstall());
            Assert.Null(window.AccessDraft.Config.ConfirmedDestructiveDistroName);
            consent.IsChecked = true;
            Assert.Same(review, frame.Content);
            Assert.Equal(window.AccessDraft.Config.DistroName, window.AccessDraft.Config.ConfirmedDestructiveDistroName);
            Assert.True(window.AccessDraft.ReplacementConfirmed);
            await SavePageProofAsync(window, review, "onboarding-windows-inline-replacement-consent");
            Assert.True(Find<Button>(review, "PrimaryButton").IsEnabled);
            Assert.Equal("Install", Find<Button>(review, "PrimaryButton").Content);
            Assert.Equal(Visibility.Collapsed, Find<InfoBar>(review, "ReviewRequired").Visibility);
            Assert.True(consent.IsChecked);
            consent.IsChecked = false;
            Assert.Same(review, frame.Content);
            AssertReviewReplacement(review);
            Assert.Null(window.AccessDraft.Config.ConfirmedDestructiveDistroName);
            Assert.False(window.AccessDraft.Config.LocalAi.WslMirroredNetworkingConsent);
            consent.IsChecked = true;
            window.AccessDraft.Config.DistroName = "Different-Target";
            Invoke(Find<Button>(review, "PrimaryButton"));
            Assert.Same(review, frame.Content);
            Assert.False(window.AccessDraft.CanInstall());
            Assert.Equal("Check Gateway", Find<Button>(review, "PrimaryButton").Content);
        });
    }

    [Theory]
    [InlineData(SetupInstallRequirement.WslInspection, "WSL inspection is incomplete", "Check Gateway")]
    [InlineData(SetupInstallRequirement.LocalAi, "Local AI is not ready", "Review Local AI")]
    [InlineData(SetupInstallRequirement.NetworkingConsent, "Local AI needs networking consent", "Review networking")]
    [InlineData(SetupInstallRequirement.Tailscale, "Tailscale prerequisites are incomplete", "Review Tailscale")]
    public async Task Review_ShowsTheActualRequirementAboveInstallationDetails(
        SetupInstallRequirement requirement, string title, string action)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            var draft = window.AccessDraft;
            if (requirement != SetupInstallRequirement.WslInspection)
                RecordInspection(draft, replacement: false);
            if (requirement is SetupInstallRequirement.LocalAi or SetupInstallRequirement.NetworkingConsent)
                draft.SetLocalAiEnabled(true);
            draft.LocalAiReady = requirement == SetupInstallRequirement.NetworkingConsent;
            draft.LocalAiNetworkingConsentRequired = requirement == SetupInstallRequirement.NetworkingConsent;
            var before = JsonSerializer.Serialize(draft.Config);
            window.NavigateToGatewaySetup();
            var page = await MountedPageAsync<GatewaySetupPage>(window, frame);
            Assert.Equal(before, JsonSerializer.Serialize(draft.Config));
            if (requirement == SetupInstallRequirement.Tailscale)
            {
                // Change the draft after mounting the off control to test admission without a real CLI probe.
                draft.Config.Tailscale.Enabled = true;
                before = JsonSerializer.Serialize(draft.Config);
                Invoke(Find<Button>(page, "PrimaryButton"));
            }
            var blocker = Find<InfoBar>(page, "ReviewRequired");
            Assert.Equal(title, blocker.Title);
            Assert.Equal(action, Find<Button>(page, "PrimaryButton").Content);
            Assert.True(Find<Button>(page, "PrimaryButton").IsEnabled);
            Assert.Equal(Visibility.Visible, blocker.Visibility);
            Assert.Same(blocker, Assert.IsType<StackPanel>(blocker.Parent).Children[0]);
            Assert.Equal([requirement], draft.GetInstallRequirements());
            Assert.False(draft.CanInstall());
            Assert.Equal(before, JsonSerializer.Serialize(draft.Config));
            await SavePageProofAsync(window, page, $"onboarding-windows-blocker-{requirement}");
        });
    }

    [Fact]
    public async Task InstallAction_RechecksChangedDraftBeforeStarting()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            RecordInspection(window.AccessDraft, replacement: false);
            window.NavigateToGatewaySetup();
            var page = await MountedPageAsync<GatewaySetupPage>(window, frame);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ReplacementOptions").Visibility);
            Assert.Equal("Install", Find<Button>(page, "PrimaryButton").Content);
            window.AccessDraft.SetLocalAiEnabled(true);
            Invoke(Find<Button>(page, "PrimaryButton"));
            await ui.YieldToRenderAsync();
            Assert.Same(page, frame.Content);
            Assert.Equal("Review Local AI", Find<Button>(page, "PrimaryButton").Content);
            Assert.False(window.AccessDraft.CanInstall());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkingReview_ReturnsToItsOriginAndRetainsRecoveryAndPinnedModel(bool recovery)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            var draft = window.AccessDraft;
            if (!recovery) RecordInspection(draft, replacement: false);
            window.NavigateToGatewaySetup();
            await MountedPageAsync<GatewaySetupPage>(window, frame);
            draft.SetLocalAiEnabled(true);
            draft.LocalAiReady = true;
            draft.LocalAiNetworkingConsentRequired = true;
            draft.Config.LocalAi.SelectedModelId = "mounted-pinned-model";
            window.NavigateToGatewaySetup();
            var review = await MountedPageAsync<GatewaySetupPage>(window, frame);
            Assert.Equal("Review networking", Find<Button>(review, "PrimaryButton").Content);
            Invoke(Find<Button>(review, "PrimaryButton"));
            var detail = await MountedPageAsync<GatewaySetupDetailPage>(window, frame);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(detail, "NetworkingOptions").Visibility);
            Assert.False(draft.Config.LocalAi.WslMirroredNetworkingConsent);
            Find<CheckBox>(detail, "LocalAiNetworkingConsentCheckBox").IsChecked = true;
            Invoke(FooterButton(detail, next: true));
            review = await MountedPageAsync<GatewaySetupPage>(window, frame);
            Assert.Equal("Install", Find<Button>(review, "PrimaryButton").Content);
            Assert.True(draft.Config.LocalAi.WslMirroredNetworkingConsent);
            Assert.Equal(recovery, window.IsLocalAiRecovery);

            window.NavigateToLocalAiSetup();
            detail = await MountedPageAsync<GatewaySetupDetailPage>(window, frame);
            window.NavigateToWslNetworking();
            detail = await MountedPageAsync<GatewaySetupDetailPage>(window, frame);
            Invoke(FooterButton(detail, next: false));
            detail = await MountedPageAsync<GatewaySetupDetailPage>(window, frame);
            var localAi = Find<LocalAiSetupControl>(detail, "LocalAiOptions");
            Assert.Equal(Visibility.Visible, localAi.Visibility);
            Assert.Equal(recovery, window.IsLocalAiRecovery);
            Assert.True(draft.Config.LocalAi.WslMirroredNetworkingConsent);
            if (recovery)
            {
                Assert.Equal("mounted-pinned-model", draft.Config.LocalAi.SelectedModelId);
                Assert.False(Find<ToggleSwitch>(localAi, "LocalAiToggle").IsEnabled);
                Assert.False(draft.WslInspectionComplete);
            }
        }, recovery: recovery);
    }

    [Theory]
    [InlineData(0, "Remote_Click")]
    [InlineData(1, "Mcp_Click")]
    [InlineData(2, "Deferred_Click")]
    [InlineData(3, "Existing_Click")]
    public async Task AdvancedRouteCards_RejectAnotherSiblingsHandler(int index, string wrongHandler)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.NavigateToAdvancedSetup();
            var page = await MountedPageAsync<AdvancedSetupPage>(window, frame);
            var cards = TestSupport.FindDescendants<SettingsCard>(page).ToArray();
            Assert.Equal(4, cards.Length);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                TestSupport.InvokeSettingsCardAction(page, cards[index], wrongHandler));
            Assert.Same(page, frame.Content);
        }, new FailingNativeHost());
    }

    [Theory]
    [InlineData(SetupGatewayRoute.McpOnly, 2, false, true)]
    [InlineData(SetupGatewayRoute.Deferred, 3, true, false)]
    public async Task GatewayFreeRoutes_CompleteWithoutWslOrAiAndPreserveUnrelatedSettings(
        SetupGatewayRoute route, int choiceIndex, bool nodeEnabled, bool mcpEnabled)
    {
        await WithWindowAsync(async (window, frame, data) =>
        {
            var settingsPath = Path.Combine(data, "settings.json");
            File.WriteAllText(settingsPath, """{"MountedFlowSentinel":"keep","NodeCameraEnabled":true}""");
            var registryPath = Path.Combine(data, "gateways.json");
            const string registry = """{"mountedFlowSentinel":"unchanged"}""";
            File.WriteAllText(registryPath, registry);
            List<SetupCompletedEventArgs> completions = [];
            List<Type> visited = [];
            window.SetupCompleted += (_, args) => completions.Add(args);
            frame.Navigated += (_, args) => visited.Add(args.SourcePageType);

            window.NavigateToAdvancedSetup();
            var choices = await MountedPageAsync<AdvancedSetupPage>(window, frame);
            var cards = TestSupport.FindDescendants<SettingsCard>(choices).ToArray();
            Assert.Equal(4, cards.Length);
            await InvokeCardAsync(window, choices, cards[choiceIndex], route + "_Click");
            var capabilities = await MountedPageAsync<CapabilitiesPage>(window, frame);
            Assert.Equal(route, window.AccessDraft.Route);
            Assert.False(window.AccessDraft.GatewayAvailable);
            Assert.Equal(nodeEnabled, Find<ToggleSwitch>(capabilities, "NodeModeToggle").IsOn);
            Assert.Equal(mcpEnabled, Find<ToggleSwitch>(capabilities, "McpToggle").IsOn);
            Find<ListView>(capabilities, "ProfileSelector").SelectedIndex = (int)SetupCapabilityProfile.ReadOnly;
            await ExpandCapabilitiesAsync(capabilities);
            Find<ToggleSwitch>(capabilities, "StartupPreferenceToggle").IsOn = false;
            AssertProgress(capabilities, stageCount: 3, current: 2);
            Assert.False(Capability(capabilities, SetupCapability.Browser).IsEnabled);
            Assert.Null(capabilities.FindName("WindowsAccess"));
            await SavePageProofAsync(window, capabilities, $"onboarding-windows-gateway-free-{route}");
            Invoke(FooterButton(capabilities, next: true));
            await WaitAsync(() => completions.Count == 1, "gateway-free completion event");
            await window.CompleteSetupAsync();
            var completion = Assert.Single(completions);
            Assert.Equal(route, completion.Route);
            Assert.False(completion.EnableAutoStart);
            Assert.Equal([typeof(AdvancedSetupPage), typeof(CapabilitiesPage)], visited);
            Assert.Same(capabilities, frame.Content);

            using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.Equal("keep", saved.RootElement.GetProperty("MountedFlowSentinel").GetString());
            Assert.Equal(nodeEnabled, saved.RootElement.GetProperty("EnableNodeMode").GetBoolean());
            Assert.Equal(mcpEnabled, saved.RootElement.GetProperty("EnableMcpServer").GetBoolean());
            Assert.False(saved.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
            Assert.False(saved.RootElement.GetProperty("AutoStart").GetBoolean());
            Assert.Equal(registry, File.ReadAllText(registryPath));
            Assert.False(window.AccessDraft.WslInspectionComplete);
            Assert.False(window.AccessDraft.Config.LocalAi.Enabled);
            Assert.False(window.AccessDraft.Config.Tailscale.Enabled);
        });
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Existing)]
    [InlineData(SetupGatewayRoute.Remote)]
    public async Task NativeEditor_ProgressAndActionsDoNotOverlapAtNarrowWidth(SetupGatewayRoute route)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            typeof(SetupWindow).GetMethod("NavigateToNativeConnection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [route]);
            var editor = await MountedPageAsync<SetupNativeConnectionPage>(window, frame);
            AssertProgress(editor, stageCount: 5, current: 1);
            AssertNativeConnectionLabels(editor);
            Find<Button>(editor, "CancelButton").Visibility = Visibility.Visible;
            var scale = editor.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new((int)(480 * scale), (int)(820 * scale)));
            await ui.YieldToRenderAsync();
            editor.UpdateLayout();
            var footer = Find<Grid>(editor, "NavigationFooter");
            var dots = Find<SetupProgressIndicator>(editor, "FlowProgress");
            Assert.True(dots.ActualWidth > 0 && dots.ActualHeight > 0);
            var previousRight = 0d;
            foreach (var name in new[] { "BackButton", "CancelButton", "CheckButton", "NextButton" })
            {
                var button = Find<Button>(editor, name);
                Assert.Equal(Visibility.Visible, button.Visibility);
                var bounds = Bounds(button, footer);
                Assert.True(bounds.Width > 0 && bounds.Height > 0);
                Assert.True(bounds.Left >= previousRight - 0.1);
                Assert.True(bounds.Right <= footer.ActualWidth + 0.1);
                Assert.True(Bounds(dots, footer).Bottom <= bounds.Top);
                previousRight = bounds.Right;
            }
        }, new FailingNativeHost());
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Existing, 0)]
    [InlineData(SetupGatewayRoute.Remote, 1)]
    public async Task NativeEditor_FailedCheckAndNextDoNotAdvanceAndCloseDrainsCancellation(
        SetupGatewayRoute route, int choiceIndex)
    {
        var host = new FailingNativeHost();
        await WithWindowAsync(async (window, frame, data) =>
        {
            window.NavigateToAdvancedSetup();
            var choices = await MountedPageAsync<AdvancedSetupPage>(window, frame);
            await InvokeCardAsync(window, choices,
                TestSupport.FindDescendants<SettingsCard>(choices).ElementAt(choiceIndex), route + "_Click");
            var editor = await MountedPageAsync<SetupNativeConnectionPage>(window, frame);
            Assert.Equal(route, window.AccessDraft.Route);
            AssertProgress(editor, stageCount: 5, current: 1);
            AssertNativeConnectionLabels(editor);
            Find<TextBox>(editor, "AddressInput").Text = "wss://mounted-flow.invalid";
            Find<PasswordBox>(editor, "TokenInput").Password = "synthetic-unsent-test-token";
            Invoke(Find<Button>(editor, "CheckButton"));
            await WaitAsync(() => host.Checks.Count == 1 && Find<Button>(editor, "CheckButton").IsEnabled, "fake Check");
            Assert.Empty(host.Connects);
            Assert.False(window.AccessDraft.GatewayAvailable);
            Assert.Same(editor, frame.Content);
            Assert.Equal(InfoBarSeverity.Error, Find<InfoBar>(editor, "ResultBar").Severity);
            Invoke(Find<Button>(editor, "NextButton"));
            await WaitAsync(() => host.Connects.Count == 1 && Find<Button>(editor, "NextButton").IsEnabled, "fake Next");
            Assert.Same(editor, frame.Content);
            Assert.Null(window.AccessDraft.NativeGatewayId);
            Assert.False(File.Exists(Path.Combine(data, "gateways.json")));

            host.HoldCheck = true;
            Invoke(Find<Button>(editor, "CheckButton"));
            await WaitAsync(() => host.Checks.Count == 2, "pending fake Check");
            Assert.False(Find<Button>(editor, "NextButton").IsEnabled);
            var resultBar = Find<InfoBar>(editor, "ResultBar");
            Assert.True(resultBar.IsOpen);
            AssertNativeConnectionText("Checking", resultBar.Message);
            Invoke(Find<Button>(editor, "CancelButton"));
            await WaitAsync(() => Find<Button>(editor, "CheckButton").IsEnabled, "cancelled fake Check");
            Assert.True(resultBar.IsOpen);
            Assert.Equal(InfoBarSeverity.Informational, resultBar.Severity);
            AssertNativeConnectionText("Cancelled", resultBar.Message);
            Assert.Same(editor, frame.Content);
            Assert.False(File.Exists(Path.Combine(data, "gateways.json")));

            Invoke(Find<Button>(editor, "CheckButton"));
            await WaitAsync(() => host.Checks.Count == 3, "pending fake Check before close");
            var cleanup = editor.DisposeAsync().AsTask();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(cleanup, editor.DisposeAsync().AsTask());
            Assert.Equal(2, host.CancelledChecks);
            Assert.True(host.Discards > 0);
            Assert.Equal("", Find<PasswordBox>(editor, "TokenInput").Password);
            Assert.False(window.AccessDraft.GatewayAvailable);
        }, host);
        Assert.Single(host.Connects);
        Assert.All(host.Checks.Concat(host.Connects), request => Assert.Equal("wss://mounted-flow.invalid", request.GatewayUrl));
    }

    [Fact]
    public async Task LegacyWizard_FinalSigtermCompletes_EarlierSigtermStaysAnError()
    {
        const string token = "sigterm-fixture-token";
        var mode = "early";
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateNativeSetup((method, _) =>
            {
                if (method == "wizard.start")
                    return mode == "final" ? FinalStep() : EarlyStep();
                if (method == "wizard.next")
                    return new
                    {
                        sessionId = "sigterm-session",
                        done = true,
                        error = GatewayWizardRestartRecoveryPolicy.HostedWizardTerminationError,
                    };
                if (method == "logs.tail")
                    return new { file = "fixture.log", cursor = 0, size = 0, lines = Array.Empty<string>() };
                return new { ok = true };
            }),
            token);

        await RunAsync("early");
        mode = "final";
        await RunAsync("final");

        async Task RunAsync(string phase)
        {
            var seen = server.Requests.Count;
            await WithWindowAsync(async (window, frame, data) =>
            {
                var registry = new GatewayRegistry(data);
                var record = registry.AddOrUpdate(new()
                {
                    Id = "sigterm-fixture",
                    Url = server.Endpoint.ToString(),
                    FriendlyName = "SIGTERM fixture",
                    SharedGatewayToken = token,
                    IsLocal = false,
                });
                registry.SetActive(record.Id);
                registry.Save();
                // Existing leaves the WSL workspace untouched. The visible result is the
                // completion page, not a Windows node guidance install.
                window.SelectGatewayRoute(SetupGatewayRoute.Existing);
                Assert.Equal(SetupGatewayRoute.Existing, window.AccessDraft.Route);
                Assert.False(OnboardingFlowPolicy.UsesWslWorkspaceFinalization(window.AccessDraft.Route));
                Assert.True(window.TryNavigateToLegacyWizard());
                var wizard = await MountedPageAsync<WizardPage>(window, frame);
                if (phase == "early")
                    await AssertEarlyAsync(wizard, frame);
                else
                    await AssertFinalAsync(frame);
                var methods = string.Join(",", server.Requests.Skip(seen).Select(request => request.Method));
                output.WriteLine($"SIGTERM-{phase} requests={methods}");
                Assert.Contains(server.Requests.Skip(seen), request => request.Method == "wizard.start");
                Assert.Contains(server.Requests.Skip(seen), request => request.Method == "wizard.next");
            });
        }

        async Task AssertEarlyAsync(WizardPage wizard, Frame frame)
        {
            await WaitRendered(frame, () =>
                Find<TextBlock>(wizard, "TitleText").Text == "Channel" &&
                Find<Button>(wizard, "PrimaryButton").IsEnabled &&
                Equals(Find<Button>(wizard, "PrimaryButton").Content, "Yes"),
                "early wizard step");
            output.WriteLine("SIGTERM-early before " + Describe(frame));
            Invoke(Find<Button>(wizard, "PrimaryButton"));
            await WaitRendered(frame, () =>
                frame.Content is WizardPage &&
                Find<TextBlock>(wizard, "ErrorText").Text ==
                    GatewayWizardRestartRecoveryPolicy.HostedWizardTerminationError &&
                Find<TextBlock>(wizard, "StatusText").Text == "Wizard needs attention" &&
                Equals(Find<Button>(wizard, "PrimaryButton").Content, "Start wizard again"),
                "early SIGTERM stays an error");
            Assert.Equal(Visibility.Visible, Find<TextBlock>(wizard, "ErrorText").Visibility);
            output.WriteLine("SIGTERM-early after " + Describe(frame));
        }

        async Task AssertFinalAsync(Frame frame)
        {
            await WaitRendered(frame, () =>
                frame.Content is WizardPage wizard &&
                Find<TextBlock>(wizard, "TitleText").Text == "done" &&
                Find<Button>(wizard, "PrimaryButton").IsEnabled &&
                Equals(Find<Button>(wizard, "PrimaryButton").Content, "Continue"),
                "final wizard step");
            output.WriteLine("SIGTERM-final before " + Describe(frame));
            Invoke(Find<Button>(Assert.IsType<WizardPage>(frame.Content), "PrimaryButton"));
            await WaitRendered(frame, () => frame.Content is CompletePage { IsLoaded: true },
                "final SIGTERM completion");
            var complete = Assert.IsType<CompletePage>(frame.Content);
            Assert.Equal("All set!", Find<TextBlock>(complete, "TitleText").Text);
            Assert.Equal("OpenClaw is ready to go", Find<TextBlock>(complete, "SubtitleText").Text);
            Assert.Equal(Visibility.Collapsed, Find<Border>(complete, "ErrorCard").Visibility);
            output.WriteLine("SIGTERM-final after " + Describe(frame));
        }

        static object EarlyStep() => new
        {
            sessionId = "sigterm-session",
            done = false,
            stepIndex = 0,
            totalSteps = 2,
            step = new
            {
                id = "channel",
                type = "confirm",
                title = "Channel",
                message = "Keep this gateway?",
            },
        };

        static object FinalStep() => new
        {
            sessionId = "sigterm-session",
            done = false,
            stepIndex = 0,
            totalSteps = 1,
            step = new
            {
                id = "done",
                type = "note",
                title = "done",
                message = "The last question is answered.",
            },
        };
    }

    private static Task WaitRendered(Frame frame, Func<bool> ready, string operation) =>
        TestSupport.WaitForRenderedConditionAsync(ready, operation, () => Describe(frame));

    private static string Describe(Frame frame)
    {
        if (frame.Content is not FrameworkElement page)
            return $"page={frame.Content?.GetType().Name ?? "null"}";
        string Text(string name) => page.FindName(name) is TextBlock block ? block.Text : "";
        var primary = page.FindName("PrimaryButton") is Button button
            ? $"{button.Content}|enabled={button.IsEnabled}"
            : "";
        return $"page={page.GetType().Name} title={Text("TitleText")} subtitle={Text("SubtitleText")} " +
            $"status={Text("StatusText")} error={Text("ErrorText")} primary={primary}";
    }

    private static void AssertNativeConnectionText(string suffix, object actual)
    {
        // Resolve expected copy independently, using the same default language context as the page.
        var resources = new ResourceManager(Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.pri"));
        var expected = resources.MainResourceMap.GetValue(
            "Resources/Onboarding_NativeConnection_" + suffix, resources.CreateResourceContext()).ValueAsString;
        var label = Assert.IsType<string>(actual);
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.DoesNotContain("Onboarding_", label);
        Assert.Equal(expected, label);
    }

    private static void AssertNativeConnectionLabels(SetupNativeConnectionPage editor)
    {
        AssertNativeConnectionText("Title", Find<TextBlock>(editor, "TitleText").Text);
        AssertNativeConnectionText("Description", Find<TextBlock>(editor, "DescriptionText").Text);
        foreach (var suffix in new[] { "Back", "Cancel", "Check", "Next" })
            AssertNativeConnectionText(suffix, Find<Button>(editor, suffix + "Button").Content);
        foreach (var suffix in new[] { "Address", "Code", "Token", "SshEnabled", "SshHost", "SshUser", "SshPort", "RemotePort", "LocalPort" })
        {
            AssertNativeConnectionText(suffix, Find<SettingsCard>(editor, suffix + "Card").Header);
            var input = Assert.IsAssignableFrom<FrameworkElement>(
                editor.FindName(suffix == "SshEnabled" ? suffix : suffix + "Input"));
            AssertNativeConnectionText(suffix, AutomationProperties.GetName(input));
        }
        var ssh = Find<SettingsExpander>(editor, "SshExpander");
        AssertNativeConnectionText("Ssh", ssh.Header);
        AssertNativeConnectionText("SshDescription", ssh.Description);
    }

    private async Task WithWindowAsync(
        Func<SetupWindow, Frame, string, Task> assertion, ISetupNativeConnectionHost? host = null, bool recovery = false,
        bool nativeProof = false, ElementTheme theme = ElementTheme.Light)
    {
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT for mounted onboarding tests.");
        OnboardingNativeProof.AssertIsolatedRoots();
        if (nativeProof) OnboardingNativeProof.RequireProofDirectory();
        foreach (var variable in new[] { "OPENCLAW_SETUP_PREVIEW_PAGE", "OPENCLAW_SETUP_TAILSCALE", "OPENCLAW_SETUP_TAILSCALE_TRUST_AUTH", "OPENCLAW_SETUP_TAILSCALE_AUTH_KEY" })
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)), $"Clear {variable} before running.");

        var directory = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")!,
            "mounted-flow", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(directory, "data");
        Directory.CreateDirectory(data);
        var configPath = Path.Combine(directory, "setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new SetupConfig
        {
            DistroName = "MountedFlow-NoRealDistro",
            GatewayUrl = "wss://mounted-flow.invalid",
            Settings = new() { EnableNodeMode = true, EnableMcpServer = false, NodeOllamaInferenceEnabled = false },
        }));
        try
        {
            await ui.ResetContainerAsync();
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Null(SetupWindow.Active);
                var resources = LoadProgressResources(repo);
                Application.Current.Resources.MergedDictionaries.Add(resources);
                SetupWindow? window = null;
                Frame? frame = null;
                IDisposable? navigation = null;
                try
                {
                    window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath, dataDir: data,
                        localDataDir: Path.Combine(directory, "local-data"), commandLineArgs: [],
                        distroNameOverride: "MountedFlow-NoRealDistro", nativeConnectionHost: host,
                        startAtLocalAiRecoveryReview: recovery, localAiRecoveryModelId: recovery ? "mounted-pinned-model" : null));
                    var root = Assert.IsType<Grid>(window.Content);
                    frame = Find<Frame>(root, "RootFrame");
                    navigation = OnboardingNativeProof.TrackNavigation(frame);
                    window.Activate();
                    if (!ui.IsSlow && !nativeProof)
                        window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                    if (nativeProof) OnboardingNativeProof.ActivateOwned(window);
                    await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                    if (!recovery)
                    {
                        var welcome = await MountedPageAsync<SecurityNoticePage>(window, frame);
                        AssertProgress(welcome, stageCount: 7, current: 0);
                        Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(welcome, "WelcomeDescription").Text));
                        Assert.DoesNotContain("Onboarding_", Find<TextBlock>(welcome, "WelcomeDescription").Text);
                    }
                    var acquired = SetupRunLock.TryAcquire(data, out var competingLock, out _);
                    competingLock?.Dispose();
                    Assert.False(acquired, "A mounted setup window must retain its run lock.");
                    Assert.Equal(recovery, window.AccessDraft.Config.LocalAi.Enabled);
                    Assert.False(window.AccessDraft.Config.Tailscale.Enabled);
                    await assertion(window, frame, data);
                }
                finally
                {
                    try
                    {
                        if (frame?.Content is IAsyncDisposable pageLifetime)
                            await pageLifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                        frame?.Navigate(typeof(Page));
                    }
                    finally
                    {
                        if (window is not null)
                        {
                            window.Close();
                            await window.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(10));
                            Assert.True(window.IsClosed);
                            Assert.Null(window.AccessDraft.NativeConnectionRequest.SharedToken);
                            Assert.Null(SetupWindow.Active);
                        }
                        Application.Current.Resources.MergedDictionaries.Remove(resources);
                        navigation?.Dispose();
                        await ui.YieldToRenderAsync();
                    }
                }
                Assert.True(SetupRunLock.TryAcquire(data, out var releasedLock, out var error), error);
                releasedLock!.Dispose();
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<T> MountedPageAsync<T>(SetupWindow window, Frame frame) where T : Page
    {
        await WaitAsync(() => frame.Content is T, $"{typeof(T).Name} navigation");
        var page = Assert.IsType<T>(frame.Content);
        await WaitAsync(() => page.IsLoaded, $"{typeof(T).Name} Loaded");
        var root = Assert.IsType<Grid>(window.Content);
        foreach (var mascot in TestSupport.FindDescendants<OnboardingMascot>(root))
            mascot.IsAnimationEnabled = false;
        root.UpdateLayout();
        await ui.YieldToRenderAsync();
        Assert.Same(window, SetupWindow.Active);
        Assert.Same(root.XamlRoot, page.XamlRoot);
        Assert.True(page.ActualWidth >= 600 && page.ActualHeight >= 600, "Expected a full mounted setup page.");
        Assert.Empty(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot));
        return page;
    }

    private async Task SavePageProofAsync(SetupWindow window, Page page, string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
            return;
        var root = Assert.IsType<Grid>(window.Content);
        var scroll = Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>());
        root.UpdateLayout();
        Assert.True(scroll.ViewportHeight > 0);
        var originalOffset = scroll.VerticalOffset;
        var offset = 0d;
        var index = 0;
        try
        {
            // Capture every viewport with the page heading and footer, not a cropped content-only image.
            do
            {
                scroll.ChangeView(null, offset, null, disableAnimation: true);
                await WaitAsync(() => Math.Abs(scroll.VerticalOffset - offset) < 1, "proof viewport");
                root.UpdateLayout();
                await ui.YieldToRenderAsync();
                await OnboardingArtworkRenderingTests.SaveProofAsync(root, $"{name}-{++index:D2}", output);
                if (offset >= scroll.ScrollableHeight) break;
                offset = Math.Min(offset + scroll.ViewportHeight, scroll.ScrollableHeight);
            } while (index < 12);
            Assert.True(offset >= scroll.ScrollableHeight, "Proof must include the complete scrollable state.");
        }
        finally
        {
            scroll.ChangeView(null, originalOffset, null, disableAnimation: true);
            await ui.YieldToRenderAsync();
        }
    }

    private static Task WaitAsync(Func<bool> predicate, string operation) =>
        TestSupport.WaitForRenderedConditionAsync(predicate, operation);

    private static ScrollViewer PageScroll(Page page) =>
        Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>());

    private static Windows.Foundation.Rect Bounds(FrameworkElement element, FrameworkElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void AssertFullyVisible(FrameworkElement element, FrameworkElement viewport) =>
        OnboardingNativeProof.AssertFullyVisible(element, viewport);

    private void AssertFixedPageChrome(Grid root, Page page)
    {
        var grid = Assert.IsType<Grid>(page.Content);
        var header = Assert.Single(grid.Children.OfType<StackPanel>(), panel => Grid.GetRow(panel) == 0);
        var footer = Assert.Single(grid.Children.OfType<Grid>(), panel => Grid.GetRow(panel) == 2);
        var scroll = PageScroll(page);
        Assert.Same(grid, header.Parent);
        Assert.Same(grid, footer.Parent);
        Assert.DoesNotContain(header, TestSupport.FindDescendants<StackPanel>(scroll));
        Assert.DoesNotContain(footer, TestSupport.FindDescendants<Grid>(scroll));
        var hero = Find<OnboardingMascot>(page, "MascotHero");
        OnboardingNativeProof.AssertHeroLayout(hero, output);
        var heading = Assert.Single(header.Children.OfType<TextBlock>());
        Assert.False(string.IsNullOrWhiteSpace(heading.Text));
        Assert.False(heading.IsTextTrimmed);
        foreach (var element in new FrameworkElement[] { header, hero, heading, footer, scroll })
            AssertFullyVisible(element, root);
        foreach (var button in footer.Children.OfType<Button>())
            AssertFullyVisible(button, root);
        Assert.True(Bounds(header, root).Bottom <= Bounds(scroll, root).Top);
        Assert.True(Bounds(scroll, root).Bottom <= Bounds(footer, root).Top);
        Assert.True(scroll.ViewportHeight > 0);
    }

    private async Task BringIntoViewportAsync(Page page, FrameworkElement element, double alignment = 0.5)
    {
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = alignment });
        page.UpdateLayout();
        await ui.YieldToRenderAsync();
        await OnboardingNativeProof.NextCompositionAsync();
        // Check the settled layout once. Never poll visibility until an oversized/clipped row passes.
        page.UpdateLayout();
    }

    private static Task AssertNativeVisibleToggleAsync(IntPtr handle, string name, string? automationId,
        bool isOn, bool checkBox) => Task.Run(() =>
    {
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        Assert.NotEqual(0u, GetWindowThreadProcessId(handle, out var processId));
        Assert.Equal((uint)Environment.ProcessId, processId);
        var window = Uia.AutomationElement.FromHandle(handle);
        Assert.Equal(Environment.ProcessId, window.Current.ProcessId);
        Uia.Condition condition = automationId is null
            ? new Uia.AndCondition(new Uia.PropertyCondition(Uia.AutomationElement.NameProperty, name),
                new Uia.PropertyCondition(Uia.AutomationElement.ControlTypeProperty, Uia.ControlType.CheckBox))
            : new Uia.PropertyCondition(Uia.AutomationElement.AutomationIdProperty, automationId);
        var element = Assert.Single(window.FindAll(Uia.TreeScope.Descendants, condition).Cast<Uia.AutomationElement>());
        Assert.Equal(name, element.Current.Name);
        Assert.False(element.Current.IsOffscreen);
        Assert.False(element.Current.BoundingRectangle.IsEmpty);
        Assert.True(window.Current.BoundingRectangle.Contains(element.Current.BoundingRectangle));
        if (checkBox) Assert.Equal(Uia.ControlType.CheckBox, element.Current.ControlType);
        else Assert.Equal("ToggleSwitch", element.Current.ClassName);
        Assert.True(element.TryGetCurrentPattern(Uia.TogglePattern.Pattern, out var pattern));
        Assert.Equal(isOn ? Uia.ToggleState.On : Uia.ToggleState.Off, Assert.IsType<Uia.TogglePattern>(pattern).Current.ToggleState);
    });

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name));

    private static ToggleSwitch Capability(CapabilitiesPage page, SetupCapability capability)
    {
        var cards = Find<SettingsExpander>(page, "FineTuneExpander").Items.Select(item => Assert.IsType<SettingsCard>(item)).ToArray();
        Assert.Equal(8, cards.Length);
        return Assert.Single(cards.Select(card => Assert.IsType<ToggleSwitch>(card.Content)),
            toggle => AutomationProperties.GetAutomationId(toggle) == $"SetupCapability{capability}");
    }

    private static SettingsCard BrowserCard(CapabilitiesPage page) =>
        Assert.IsType<SettingsCard>(Find<SettingsExpander>(page, "FineTuneExpander").Items[(int)SetupCapability.Browser]);

    private async Task ExpandCapabilitiesAsync(CapabilitiesPage page)
    {
        var expander = Find<SettingsExpander>(page, "FineTuneExpander");
        Assert.Equal("Fine-tune", AutomationProperties.GetName(expander));
        expander.IsExpanded = true;
        page.UpdateLayout();
        await TestSupport.WaitForSettingsExpanderSettledAsync(ui, expander, true);
        var rows = expander.Items;
        Assert.Equal(8, rows.Count);
        foreach (var item in rows)
        {
            var card = Assert.IsType<SettingsCard>(item);
            card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await WaitAsync(() => card.IsLoaded, "capability row realization");
            await ui.YieldToRenderAsync();
            Assert.True(Assert.IsType<ToggleSwitch>(card.Content).IsLoaded);
            Assert.True(card.ActualWidth > 0);
        }
    }

    private static void AssertReviewReplacement(GatewaySetupPage page)
    {
        var primary = Find<Button>(page, "PrimaryButton");
        Assert.False(primary.IsEnabled);
        Assert.Equal("Install", primary.Content);
        Assert.False(Find<CheckBox>(page, "ReplacementConsent").IsChecked);
        Assert.Equal(Visibility.Collapsed, Find<InfoBar>(page, "ReviewRequired").Visibility);
        Assert.True(Find<InfoBar>(page, "ReplacementWarning").IsOpen);
        Assert.Equal("This replacement can permanently delete WSL data", Find<InfoBar>(page, "ReplacementWarning").Title);
    }

    private static void AssertCapabilities(CapabilitiesPage page, SetupAccessDraft draft, SetupCapability[] enabled)
    {
        foreach (var capability in SetupCapabilityProfiles.Ordered)
        {
            Assert.Equal(enabled.Contains(capability), draft.GetCapability(capability));
            Assert.Equal(enabled.Contains(capability), Capability(page, capability).IsOn);
        }
        Assert.True(draft.Config.Capabilities.Device);
    }

    private static void AssertProgress(Page page, int stageCount, int current)
    {
        var progress = Find<SetupProgressIndicator>(page, "FlowProgress");
        Assert.Equal(Visibility.Visible, progress.Visibility);
        Assert.Equal(stageCount, progress.Children.Count);
        Assert.Equal(20, Assert.IsType<Border>(progress.Children[current]).Width);
        Assert.Single(progress.Children.Cast<Border>(), dot => dot.Width == 20);
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(progress)));
    }

    private static Button FooterButton(Page page, bool next)
    {
        var footer = Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<Grid>(),
            grid => Grid.GetRow(grid) == 2);
        return Assert.Single(footer.Children.OfType<Button>(), button => Grid.GetColumn(button) == (next ? 1 : 0));
    }

    private static void Invoke(FrameworkElement element)
    {
        Assert.True(Assert.IsAssignableFrom<Control>(element).IsEnabled);
        var peer = FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static Task InvokeCardAsync(SetupWindow window, Page page, SettingsCard card, string handler)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
            return OnboardingNativeProof.InvokeSettingsCardAsync(window, card);
        TestSupport.InvokeSettingsCardAction(page, card, handler == "McpOnly_Click" ? "Mcp_Click" : handler);
        return Task.CompletedTask;
    }

    private static void FocusOwnedControl(SetupWindow window, Control control)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Activate();
        SetForegroundWindow(handle);
        Assert.Equal(handle, GetForegroundWindow());
        Assert.True(control.Focus(FocusState.Keyboard));
        Assert.Same(control, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(control.XamlRoot));
    }

    private static Task AssertNativeProfileSelectionAsync(IntPtr handle, string listName, string rowName, bool select,
        bool requireVisible = false) =>
        Task.Run(() =>
        {
            // The container peer has no SelectionItem pattern. Query the real UIA tree on MTA
            // so ListView exposes its data peer while the owning XAML dispatcher stays responsive.
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            Assert.NotEqual(0u, GetWindowThreadProcessId(handle, out var processId));
            Assert.Equal((uint)Environment.ProcessId, processId);
            var ownedWindow = Uia.AutomationElement.FromHandle(handle);
            Assert.Equal(Environment.ProcessId, ownedWindow.Current.ProcessId);
            var lists = ownedWindow.FindAll(Uia.TreeScope.Descendants, new Uia.AndCondition(
                new Uia.PropertyCondition(Uia.AutomationElement.ControlTypeProperty, Uia.ControlType.List),
                new Uia.PropertyCondition(Uia.AutomationElement.NameProperty, listName)));
            var list = Assert.Single(lists.Cast<Uia.AutomationElement>());
            Assert.True(list.TryGetCurrentPattern(Uia.SelectionPattern.Pattern, out var selectionPattern));
            var selection = Assert.IsType<Uia.SelectionPattern>(selectionPattern);
            Assert.False(selection.Current.CanSelectMultiple);
            var rows = list.FindAll(Uia.TreeScope.Descendants, new Uia.AndCondition(
                new Uia.PropertyCondition(Uia.AutomationElement.ControlTypeProperty, Uia.ControlType.ListItem),
                new Uia.PropertyCondition(Uia.AutomationElement.NameProperty, rowName)));
            var row = Assert.Single(rows.Cast<Uia.AutomationElement>());
            if (requireVisible)
            {
                Assert.False(row.Current.IsOffscreen);
                Assert.False(row.Current.BoundingRectangle.IsEmpty);
                Assert.True(ownedWindow.Current.BoundingRectangle.Contains(row.Current.BoundingRectangle));
            }
            Assert.True(row.TryGetCurrentPattern(Uia.SelectionItemPattern.Pattern, out var itemPattern));
            var itemSelection = Assert.IsType<Uia.SelectionItemPattern>(itemPattern);
            Assert.Equal(list.GetRuntimeId(), itemSelection.Current.SelectionContainer.GetRuntimeId());
            if (select) itemSelection.Select();
            Assert.True(itemSelection.Current.IsSelected);
            var selectedRow = Assert.Single(selection.Current.GetSelection());
            Assert.Equal(row.GetRuntimeId(), selectedRow.GetRuntimeId());
            Assert.Equal(rowName, selectedRow.Current.Name);
        });

    private static void SendKey(ushort key)
    {
        var input = new[]
        {
            new NativeInput { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key } },
            new NativeInput { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key, Flags = 2 } },
        };
        Assert.Equal(2u, SendInput(2, input, Marshal.SizeOf<NativeInput>()));
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private static void RecordInspection(SetupAccessDraft draft, bool replacement) =>
        draft.RecordWslInspection(new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: false, LocalGatewayId: null, LocalGatewayUrl: null,
            HasDistro: replacement, HasDistroDataDirectory: replacement, DistroIsAppOwned: false,
            DistroName: replacement ? draft.Config.DistroName : null, HasIdentityFiles: false,
            PreservedGatewayCount: 0, PreservedGatewayNames: []));

    internal static ResourceDictionary LoadProgressResources(string repo)
    {
        // TestApp is not the product App. Import its exact indicator tokens without inventing test brushes.
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var app = XDocument.Load(Path.Combine(repo, "src", "OpenClaw.Tray.WinUI", "App.xaml"));
        var themes = app.Descendants(xaml + "ResourceDictionary.ThemeDictionaries").Single();
        var dictionary = new XElement(xaml + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x),
            new XElement(xaml + "ResourceDictionary.ThemeDictionaries",
                themes.Elements().Select(theme => new XElement(xaml + "ResourceDictionary",
                    new XAttribute(x + "Key", theme.Attribute(x + "Key")!.Value),
                    theme.Elements().Where(resource => resource.Attribute(x + "Key")?.Value is
                        "SetupIndicatorAccentBrush" or "SetupInactiveDotBrush").Select(resource => new XElement(resource))))));
        return Assert.IsType<ResourceDictionary>(XamlReader.Load(dictionary.ToString()));
    }

    private sealed class FailingNativeHost : ISetupNativeConnectionHost
    {
        public List<SetupNativeConnectionRequest> Checks { get; } = [];
        public List<SetupNativeConnectionRequest> Connects { get; } = [];
        public bool HoldCheck { get; set; }
        public int CancelledChecks { get; private set; }
        public int Discards { get; private set; }

        public async Task<SetupNativeConnectionResult> CheckAsync(
            SetupNativeConnectionRequest request, CancellationToken cancellationToken)
        {
            Checks.Add(request);
            if (HoldCheck)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { CancelledChecks++; throw; }
            }
            return new(false, Error: "Synthetic check failure. No Gateway was contacted.");
        }

        public Task<SetupNativeConnectionResult> ConnectAsync(
            SetupNativeConnectionRequest request, CancellationToken cancellationToken)
        {
            Connects.Add(request);
            return Task.FromResult(new SetupNativeConnectionResult(false,
                Error: "Synthetic connection failure. Nothing was committed."));
        }

        public Task DiscardCheckAsync()
        {
            Discards++;
            return Task.CompletedTask;
        }
    }
}
