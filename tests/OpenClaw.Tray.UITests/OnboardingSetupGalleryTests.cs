using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using Xunit.Abstractions;
using static OpenClaw.Tray.UITests.OnboardingSetupGalleryData;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Static native gallery, not installation or animation proof. Every scene mounts production
/// pages in a run-locked SetupWindow. Run only after the parent freezes sources AND binaries.
/// </summary>
[Collection(UICollection.Name)]
public sealed class OnboardingSetupGalleryTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task FollowupProof_CapabilitiesAndWelcome(ElementTheme theme)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        _directory = OnboardingNativeProof.RequireProofDirectory();
        OnboardingNativeProof.AssertSourceUnchanged();
        foreach (var scene in new[]
        {
            new Scene("followup-welcome-available", "welcome", "available"),
            new Scene("followup-welcome-unavailable", "welcome", "unavailable"),
            new Scene("followup-capabilities", "capabilities", "Standard"),
            new Scene("followup-capabilities-fine-tune", "capabilities", "Standard-fine-tune"),
        })
        {
            var result = new SceneResult(scene, theme);
            _results.Add(result);
            await CaptureSceneAsync(result);
            Assert.True(result.AllViewportsCaptured);
        }
        OnboardingNativeProof.AssertSourceUnchanged();
        _sourceVerifiedAfterCapture = true;
        foreach (var result in _results) result.Status = "captured";
        WriteManifest();
    }

    private const string PreviewVariable = "OPENCLAW_SETUP_PREVIEW_PAGE";
    private readonly string _run = $"setup-gallery-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
    private readonly List<SceneResult> _results = [];
    private string _directory = "";
    private bool _sourceVerifiedAfterCapture;
    private string? _failure;
    private bool _manualForegroundPending;

    [Theory]
    [InlineData(false, ElementTheme.Light, 480)]
    [InlineData(false, ElementTheme.Dark, 480)]
    [InlineData(false, ElementTheme.Light, 760)]
    [InlineData(false, ElementTheme.Dark, 760)]
    [InlineData(true, ElementTheme.Light, 480)]
    [InlineData(true, ElementTheme.Dark, 480)]
    [InlineData(true, ElementTheme.Light, 760)]
    [InlineData(true, ElementTheme.Dark, 760)]
    public async Task NativePackagePages_KeepSharedHeroProgressAndActionsVisible(
        bool wizard, ElementTheme theme, int width)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var preview = new PreviewScope();
            preview.Set(wizard ? "wizard" : "native");
            var resources = LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var originalSize = ui.TestWindow.AppWindow.Size;
            var scale = ui.Container.XamlRoot.RasterizationScale;
            ui.TestWindow.AppWindow.Resize(new((int)((width + 40) * scale), (int)(880 * scale)));
            var root = new Grid { Width = width, Height = 800 };
            var frame = new Frame();
            root.Children.Add(frame);
            ui.Container.Children.Add(root);
            try
            {
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                Assert.True(frame.Navigate(wizard ? typeof(WizardPage) : typeof(NativeGatewaySetupPage),
                    new SetupConfig()));
                var page = Assert.IsAssignableFrom<Page>(frame.Content);
                await WaitAsync(() => page.IsLoaded, "native package preview Loaded");
                var mascot = Find<OnboardingMascot>(page, wizard ? "MascotHero" : "ProgressMascot");
                mascot.IsAnimationEnabled = false;
                var progress = Find<SetupProgressIndicator>(page, "FlowProgress");
                progress.Update(OnboardingFlowPolicy.GetStages(SetupGatewayRoute.Native, new SetupConfig(), includeReadyChoice: !wizard),
                    wizard ? OnboardingStage.AiSetup : OnboardingStage.Install);
                Assert.Equal(wizard ? 5 : 6, progress.Children.Count);
                Assert.Equal(20, Assert.IsType<Border>(progress.Children[wizard ? 4 : 3]).Width);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(progress)));
                string[] actions;
                if (wizard)
                {
                    Find<DropDownButton>(page, "MoreOptionsButton").Visibility = Visibility.Visible;
                    Find<Button>(page, "SecondaryButton").Visibility = Visibility.Visible;
                    actions = ["MoreOptionsButton", "SecondaryButton", "PrimaryButton"];
                }
                else
                {
                    Assert.Null(typeof(NativeGatewaySetupPage).GetField("_operation",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
                    Assert.Contains("Preview only", Find<TextBlock>(page, "StatusText").Text);
                    var rows = Find<StackPanel>(page, "StepsPanel").Children;
                    Assert.Equal(4, rows.Count);
                    foreach (var row in rows)
                    {
                        var card = Assert.IsType<SettingsCard>(row);
                        Assert.False(card.IsClickEnabled);
                        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(card.Header)));
                        Assert.IsType<SetupPhaseStatus>(card.Content);
                    }
                    Find<Button>(page, "RetryButton").Visibility = Visibility.Visible;
                    actions = ["BackButton", "RetryButton"];
                }
                root.UpdateLayout();
                await ui.YieldToRenderAsync();
                var footer = Find<Grid>(page, "NavigationFooter");
                var progressBounds = progress.TransformToVisual(footer).TransformBounds(
                    new(0, 0, progress.ActualWidth, progress.ActualHeight));
                Assert.True(progressBounds.Width > 0 && progressBounds.Height > 0);
                var previousRight = 0d;
                foreach (var name in actions)
                {
                    var action = Assert.IsAssignableFrom<FrameworkElement>(page.FindName(name));
                    var bounds = action.TransformToVisual(footer).TransformBounds(
                        new(0, 0, action.ActualWidth, action.ActualHeight));
                    Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{name}: {bounds}");
                    if (!wizard)
                    {
                        Assert.InRange(bounds.Width, 100, footer.ActualWidth / 2 - 8);
                        Assert.Equal(name == "BackButton" ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                            action.HorizontalAlignment);
                    }
                    Assert.True(bounds.Left >= previousRight - 0.1);
                    Assert.True(bounds.Right <= footer.ActualWidth + 0.1);
                    Assert.True(progressBounds.Bottom <= bounds.Top);
                    previousRight = bounds.Right;
                }
                OnboardingNativeProof.AssertFullyVisible(mascot, root);
                OnboardingNativeProof.AssertFullyVisible(footer, root);
                await OnboardingArtworkRenderingTests.SaveProofAsync(root,
                    $"native-package-{(wizard ? "wizard" : "install")}-{theme}-{width}", output);
            }
            finally
            {
                frame.Navigate(typeof(Page));
                ui.Container.Children.Clear();
                await ui.YieldToRenderAsync();
                ui.TestWindow.AppWindow.Resize(originalSize);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    public async Task WelcomeAvailability_ClearsStaleGeneralCardBeforeInjectedRechecks()
    {
        using var temp = new TempDirectory("welcome-badge-recheck-");
        var config = new SetupConfig();
        var configPath = temp.Combine("config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"), commandLineArgs: []));
                SeedWsl(window, "Ready");
                SeedHardware(window, Task.FromResult(Hardware("eligible")));
                var frame = Find<Frame>(Assert.IsType<Grid>(window.Content), "RootFrame");
                window.NavigateToWelcome();
                window.Activate();
                var page = await MountedAsync<WelcomePage>(frame);
                var panel = Find<SettingsCard>(page, "LocalAiAvailabilityPanel");
                var choice = Find<ListViewItem>(page, "InstallChoice");
                await WaitAsync(() => panel.Visibility == Visibility.Visible, "eligible injected GPU");
                Assert.Contains("Your PC supports Local AI", AutomationProperties.GetName(panel));
                Assert.DoesNotContain("supports Local AI", AutomationProperties.GetName(choice));
                Assert.False(panel.IsClickEnabled);
                Assert.Same(Find<ListView>(page, "GatewayChoiceSelector").Parent, panel.Parent);
                var before = JsonSerializer.Serialize(window.AccessDraft.Config);
                var pending = new TaskCompletionSource<HostHardwareInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                SeedHardware(window, pending.Task);
                var detect = typeof(WelcomePage).GetMethod("DetectLocalAiAvailabilityAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var stale = Assert.IsAssignableFrom<Task>(detect.Invoke(page, null));
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.Empty(AutomationProperties.GetName(panel));
                Assert.DoesNotContain("supports Local AI", AutomationProperties.GetName(choice));
                Assert.Empty(Find<TextBlock>(page, "LocalAiAvailabilityText").Text);
                SeedHardware(window, Task.FromResult(Hardware("unsupported")));
                await Assert.IsAssignableFrom<Task>(detect.Invoke(page, null));
                pending.SetResult(Hardware("eligible"));
                await stale;
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.DoesNotContain("supports Local AI", AutomationProperties.GetName(choice));
                SeedHardware(window, Task.FromResult(Hardware("unknown")));
                await Assert.IsAssignableFrom<Task>(detect.Invoke(page, null));
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task ApprovedMock_FivePagesAndProviderPopup_LightAndDark()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        _directory = OnboardingNativeProof.RequireProofDirectory();
        string[] ids = ["01-welcome-trust", "05-capabilities-standard", "07-review-replacement-unchecked",
            "09-progress-gateway", "12-ai-providers", "13-provider-confirm"];
        OnboardingNativeProof.AssertSourceUnchanged();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (var id in ids)
        {
            var scene = new SceneResult(Assert.Single(RequiredScenes, item => item.Id == id), theme);
            _results.Add(scene);
            await CaptureSceneAsync(scene);
            Assert.NotEmpty(scene.Frames);
            Assert.True(scene.AllViewportsCaptured);
        }
        OnboardingNativeProof.AssertSourceUnchanged();
        output.WriteLine("Focused approved-mock comparison only. High contrast and real Windows text scaling require separate authorized proof.");
    }

    [Fact]
    public async Task SemanticHeaders_RejectDecorativeHiddenAndWrongPageContent()
    {
        Assert.All(RequiredScenes, scene => Assert.False(string.IsNullOrWhiteSpace(HeaderFor(scene).Text)));
        await ui.RunOnUIAsync(async () =>
        {
            var header = new StackPanel();
            var decorative = new StackPanel();
            AutomationProperties.SetAccessibilityView(decorative, AccessibilityView.Raw);
            decorative.Children.Add(new TextBlock { Text = "z" });
            header.Children.Add(decorative);
            var hidden = new TextBlock { Text = "Invisible title", Opacity = 0 };
            header.Children.Add(hidden);
            var expected = new TextBlock { Text = "Expected title" };
            header.Children.Add(expected);
            ui.Container.Children.Add(header);
            try
            {
                await WaitAsync(() => header.IsLoaded, "header regression Loaded");
                header.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                Assert.False(IsVisible(hidden));
                Assert.False(IsVisible((TextBlock)decorative.Children[0]));
                Assert.Same(expected, SelectDirectHeader([header], "Expected title"));
                Assert.ThrowsAny<Exception>(() => SelectDirectHeader([header], "Wrong page"));
            }
            finally { ui.Container.Children.Clear(); }
        });
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task OwnedWindows_KeepProductDpiAcrossSharedCaptureAndAsyncGalleryCreation()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        _directory = OnboardingNativeProof.RequireProofDirectory();
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var size = ui.TestWindow.AppWindow.Size;
            var position = ui.TestWindow.AppWindow.Position;
            var background = ui.Container.Background;
            var padding = ui.Container.Padding;
            var resources = LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var frame = new Frame();
            using var navigation = OnboardingNativeProof.TrackNavigation(frame);
            try
            {
                ui.Container.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
                ui.Container.Padding = new Thickness(0);
                ui.Container.Children.Add(frame);
                frame.Navigate(typeof(SecurityNoticePage), new SetupConfig());
                var scale = ui.Container.XamlRoot.RasterizationScale;
                ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    NativeProofLayout.PhysicalPixels(720, scale), NativeProofLayout.PhysicalPixels(860, scale)));
                var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(ui.TestWindow.AppWindow.Id,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
                ui.TestWindow.AppWindow.Move(new Windows.Graphics.PointInt32(work.X + 32, work.Y + 32));
                OnboardingNativeProof.ActivateOwned(ui.TestWindow);
                ui.Container.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(ui.TestWindow);
                OnboardingNativeProof.AssertProductDpi(ui.TestWindow);
                using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, $"{_run}-shared-fixture-dpi",
                    output, ["Welcome to OpenClaw", "Continue"])) { }
                await OnboardingNativeProof.AssertNativeLabelAfterWpfLoadAsync(handle, "Welcome to OpenClaw");
                await ui.YieldToRenderAsync();
                await ui.RunOnUIAsync(async () =>
                {
                    Assert.Equal(handle, WinRT.Interop.WindowNative.GetWindowHandle(ui.TestWindow));
                    OnboardingNativeProof.AssertProductDpi(ui.TestWindow);
                    OnboardingNativeProof.TraceDpiContext("Regression: same HWND after MTA WPF/UIA and next callback", ui.TestWindow);
                    using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, $"{_run}-shared-fixture-after-uia",
                        output, ["Welcome to OpenClaw", "Continue"])) { }
                });
            }
            finally
            {
                frame.Navigate(typeof(Page));
                ui.Container.Children.Clear();
                ui.Container.Background = background;
                ui.Container.Padding = padding;
                ui.TestWindow.AppWindow.Resize(size);
                ui.TestWindow.AppWindow.Move(position);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
        await ui.YieldToRenderAsync();
        var scene = new SceneResult(RequiredScenes[0], ElementTheme.Light);
        _results.Add(scene);
        try
        {
            await CaptureSceneAsync(scene);
            Assert.NotEmpty(scene.Frames);
            Assert.True(scene.AllViewportsCaptured);
            OnboardingNativeProof.AssertSourceUnchanged();
        }
        finally { await ui.RunOnUIAsync(() => OnboardingNativeProof.WriteDpiTrace(output)); }
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task OwnedWindowDpi_PreflightCapturesActualWelcomeWithoutVirtualization()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        _directory = OnboardingNativeProof.RequireProofDirectory();
        var scene = new SceneResult(RequiredScenes[0], ElementTheme.Light);
        _results.Add(scene);
        OnboardingNativeProof.AssertSourceUnchanged();
        await CaptureSceneAsync(scene);
        Assert.NotEmpty(scene.Frames);
        Assert.True(scene.AllViewportsCaptured);
        OnboardingNativeProof.AssertSourceUnchanged();
        output.WriteLine("Focused DPI preflight only. This single scene is not a completed gallery.");
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task CompleteNativeGallery_AllRequiredScenesAndScrollViewports_LightAndDark()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        _manualForegroundPending = Environment.GetEnvironmentVariable("OPENCLAW_UI_GALLERY_MANUAL_FOCUS") == "1";
        _directory = OnboardingNativeProof.RequireProofDirectory();
        Directory.CreateDirectory(_directory);
        Assert.False(File.Exists(Path.Combine(_directory, _run + ".json")), "Never overwrite a previous gallery manifest.");
        Assert.Equal(RequiredScenes.Count, RequiredScenes.Select(scene => scene.Id).Distinct().Count());
        Assert.All(Enum.GetValues<LocalAiOnboardingState>(), state =>
            Assert.Contains(RequiredScenes, scene => scene.Family == "ai" && scene.State == state.ToString()));
        Assert.Contains(RequiredScenes, scene => scene.State == "unsupported-hidden");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var scene in RequiredScenes)
                _results.Add(new(scene, theme));
        WriteManifest();
        try
        {
            OnboardingNativeProof.AssertSourceUnchanged();
            foreach (var result in _results)
            {
                result.Status = "running";
                WriteManifest();
                var elapsed = Stopwatch.StartNew();
                try
                {
                    await CaptureSceneAsync(result);
                    Assert.NotEmpty(result.Frames);
                    Assert.True(result.AllViewportsCaptured);
                    result.Status = "captured-awaiting-final-source-check";
                }
                catch (Exception error)
                {
                    result.Status = "failed";
                    result.Failure = error.ToString();
                    result.AllViewportsCaptured = false;
                    throw; // A foreground/occlusion failure never admits later input.
                }
                finally
                {
                    result.Seconds = elapsed.Elapsed.TotalSeconds;
                    WriteManifest();
                }
            }
            OnboardingNativeProof.AssertSourceUnchanged();
            _sourceVerifiedAfterCapture = true;
            Assert.All(_results, result => Assert.Equal("captured-awaiting-final-source-check", result.Status));
            foreach (var result in _results) result.Status = "captured";
        }
        catch (Exception error)
        {
            _failure = error.ToString();
            throw;
        }
        finally
        {
            WriteManifest();
            output.WriteLine($"gallery-manifest={Path.Combine(_directory, _run + ".json")}");
            output.WriteLine($"required={_results.Count}; captured={_results.Count(result => result.Status == "captured")}; " +
                $"frames={_results.Sum(result => result.Frames.Count)}; sourceVerifiedAfterCapture={_sourceVerifiedAfterCapture}");
        }
    }

    private async Task CaptureSceneAsync(SceneResult result)
    {
        foreach (var variable in new[] { PreviewVariable, "OPENCLAW_SETUP_TAILSCALE",
            "OPENCLAW_SETUP_TAILSCALE_TRUST_AUTH", "OPENCLAW_SETUP_TAILSCALE_AUTH_KEY" })
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)), $"Clear {variable} before gallery.");
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT.");
        var work = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")!,
            _run, $"{result.Scene.Id}-{result.Theme}");
        Directory.CreateDirectory(work);
        var configPath = Path.Combine(work, "setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new SetupConfig
        {
            DistroName = "Gallery-NoRealDistro",
            GatewayUrl = "wss://gallery.invalid",
            Settings = new() { EnableNodeMode = true, EnableMcpServer = false, NodeOllamaInferenceEnabled = false },
        }));
        try
        {
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Null(SetupWindow.Active);
                // The wrapper's slow mode shows the otherwise empty fixture window.
                // Manual admission must present only the actual setup window to click.
                if (_manualForegroundPending)
                    ui.TestWindow.AppWindow.Hide();
                var resources = LoadProgressResources(repo);
                Application.Current.Resources.MergedDictionaries.Add(resources);
                SetupWindow? window = null;
                Frame? frame = null;
                IDisposable? navigation = null;
                var native = new NativeHost();
                var local = new LocalHost(Enum.TryParse<LocalAiOnboardingState>(result.Scene.State, out var state)
                    ? state : LocalAiOnboardingState.SetUp, freshUnsupported: result.Scene.State == "unsupported-hidden");
                using var preview = new PreviewScope();
                var pendingHardware = new TaskCompletionSource<HostHardwareInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                object? pendingWsl = null;
                try
                {
                    window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath, dataDir: Path.Combine(work, "data"),
                        localDataDir: Path.Combine(work, "local"), commandLineArgs: [],
                        nativeConnectionHost: native, localAiHost: local));
                    var root = Assert.IsType<Grid>(window.Content);
                    frame = Find<Frame>(root, "RootFrame");
                    navigation = OnboardingNativeProof.TrackNavigation(frame);
                    Assert.IsType<SecurityNoticePage>(frame.Content);
                    Assert.NotNull(Field(window, "_setupLock"));
                    Assert.False(window.AccessDraft.Config.Tailscale.Enabled);
                    // Seed both caches BEFORE any page with Loaded/OnNavigatedTo inspection is mounted.
                    SeedHardware(window, Task.FromResult(Hardware("eligible")));
                    SeedWsl(window, "Ready");
                    if (_manualForegroundPending)
                        window.AppWindow.Show(activateWindow: false);
                    else
                        OnboardingNativeProof.ActivateOwned(window);
                    await WaitAsync(() => root.IsLoaded, "gallery window Loaded");
                    var scale = root.XamlRoot.RasterizationScale;
                    window.AppWindow.Resize(new(NativeProofLayout.PhysicalPixels(720, scale),
                        NativeProofLayout.PhysicalPixels(820, scale)));
                    var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,
                        Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
                    var size = window.AppWindow.Size;
                    Assert.True(size.Width + 16 <= area.Width && size.Height + 16 <= area.Height,
                        "The complete standard gallery window must fit the current work area.");
                    window.AppWindow.Move(new(area.X + (area.Width - size.Width) / 2,
                        area.Y + (area.Height - size.Height) / 2));
                    await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, result.Theme);
                    if (_manualForegroundPending)
                    {
                        await OnboardingNativeProof.AwaitManualForegroundAdmissionAsync(window, output);
                        _manualForegroundPending = false;
                    }
                    OnboardingNativeProof.ActivateOwned(window);

                    var draft = window.AccessDraft;
                    var config = draft.Config;
                    var scene = result.Scene;
                    switch (scene.Family)
                    {
                        case "security":
                            await MountedAsync<SecurityNoticePage>(frame);
                            if (scene.State == "halo")
                                SetHalo(Find<OnboardingMascot>((Page)frame.Content, "MascotHero"));
                            break;
                        case "welcome":
                            window.NavigateToWelcome();
                            var welcome = await MountedAsync<WelcomePage>(frame);
                            await WaitAsync(() => Find<SettingsCard>(welcome, "LocalAiAvailabilityPanel").Visibility == Visibility.Visible,
                                "synthetic hardware availability");
                            if (scene.State is "available" or "unavailable")
                            {
                                await WaitAsync(() => !Find<ProgressRing>(welcome, "NativeCheckProgress").IsActive,
                                    "read-only native capability probe completed");
                                // Render both production visual states without changing host capabilities.
                                var available = scene.State == "available";
                                var selector = Find<ListView>(welcome, "GatewayChoiceSelector");
                                var nativeChoice = Find<ListViewItem>(welcome, "NativeChoice");
                                nativeChoice.IsEnabled = available;
                                selector.Items.Remove(nativeChoice);
                                selector.Items.Insert(available ? 0 : selector.Items.Count, nativeChoice);
                                Find<RecommendedBadge>(welcome, "WslRecommendedBadge").Visibility =
                                    available ? Visibility.Collapsed : Visibility.Visible;
                                Find<Border>(welcome, "NativeSupportCard").Visibility =
                                    available ? Visibility.Collapsed : Visibility.Visible;
                                Find<StackPanel>(welcome, "NativeSupportStatusPanel").Visibility =
                                    available ? Visibility.Collapsed : Visibility.Visible;
                                Find<TextBlock>(welcome, "NativeSupportStatus").Text =
                                    available ? "" : "Synthetic unavailable host. Windows update required.";
                                Assert.True(VisualStateManager.GoToState(welcome,
                                    available ? "NativeRecommendedState" : "WslRecommendedState", false));
                                result.Facts.Add("Synthetic availability projection using production visual states, not host eligibility proof.");
                            }
                            if (scene.State == "existing")
                                Find<ListView>(welcome, "GatewayChoiceSelector").SelectedItem =
                                    Find<ListViewItem>(welcome, "ConnectChoice");
                            if (scene.State is "checking" or "blocked" or "failure")
                            {
                                pendingWsl = SeedPendingWsl(window);
                                Invoke(window, Find<Button>(welcome, "NextButton"));
                                await WaitAsync(() => Find<ProgressRing>(welcome, "InstallCheckProgress").IsActive, "synthetic WSL checking");
                                if (scene.State != "checking")
                                {
                                    FinishWsl(pendingWsl, fail: scene.State == "failure");
                                    pendingWsl = null;
                                    await WaitAsync(() => Find<InfoBar>(welcome, "ReadinessError").IsOpen &&
                                        Find<Button>(welcome, "NextButton").IsEnabled, "synthetic WSL rejection");
                                }
                            }
                            break;
                        case "advanced":
                            frame.Navigate(typeof(AdvancedSetupPage), scene.State == "unavailable");
                            break;
                        case "connection":
                            window.AccessDraft.NativeConnectionRequest = new("");
                            InvokePrivate(window, "NavigateToNativeConnection",
                                scene.State.StartsWith("remote", StringComparison.Ordinal) ? SetupGatewayRoute.Remote : SetupGatewayRoute.Existing);
                            var connection = await MountedAsync<SetupNativeConnectionPage>(frame);
                            if (scene.State.EndsWith("ssh", StringComparison.Ordinal))
                            {
                                Find<ToggleSwitch>(connection, "SshEnabled").IsOn = true;
                                Find<TextBox>(connection, "SshHostInput").Text = "gallery.invalid";
                                Find<TextBox>(connection, "SshUserInput").Text = "synthetic-user";
                                var ssh = Find<SettingsExpander>(connection, "SshExpander");
                                ssh.IsExpanded = true;
                                await TestSupport.WaitForSettingsExpanderSettledAsync(ui, ssh, true);
                            }
                            else if (!scene.State.EndsWith("empty", StringComparison.Ordinal))
                            {
                                Find<TextBox>(connection, "AddressInput").Text = "wss://gallery.invalid";
                                Invoke(window, Find<Button>(connection, scene.State.EndsWith("check", StringComparison.Ordinal) ? "CheckButton" : "NextButton"));
                                await WaitAsync(() => Find<InfoBar>(connection, "ResultBar").Severity == InfoBarSeverity.Error,
                                    "fake native host failure");
                                Assert.Single(native.Calls);
                            }
                            break;
                        case "capabilities":
                            draft.ApplyProfile(Enum.TryParse<SetupCapabilityProfile>(scene.State.Split('-')[0], out var profile) &&
                                profile != SetupCapabilityProfile.Custom ? profile : SetupCapabilityProfile.Standard);
                            if (scene.State == "Custom") draft.SetCapability(SetupCapability.Camera, true);
                            draft.FineTuneExpanded = scene.State.Contains("fine-tune", StringComparison.Ordinal) ||
                                scene.State is "Custom" or "off" or "browser";
                            if (scene.State == "off") { draft.SetNodeMode(false); draft.SetMcpServer(false); }
                            if (scene.State != "browser") draft.SelectRoute(SetupGatewayRoute.ManagedWsl, gatewayAvailable: true);
                            window.NavigateToCapabilities();
                            var capabilities = await MountedAsync<CapabilitiesPage>(frame);
                            Assert.DoesNotContain(TestSupport.FindDescendants<TextBlock>(capabilities),
                                text => text.Text == "Choose what your agent can do");
                            Assert.Equal(3, Find<ListView>(capabilities, "ProfileSelector").Items.Count);
                            if (draft.FineTuneExpanded)
                                await TestSupport.WaitForSettingsExpanderSettledAsync(ui,
                                    Find<SettingsExpander>(capabilities, "FineTuneExpander"), true);
                            Assert.Equal(scene.State == "Custom" ? -1 : (int)draft.Profile,
                                Find<ListView>(capabilities, "ProfileSelector").SelectedIndex);
                            Assert.Null(capabilities.FindName("WindowsAccess"));
                            break;
                        case "review":
                            if (scene.State != "inspection") RecordInspection(draft, scene.State is "replacement" or "confirmed");
                            if (scene.State == "confirmed") draft.ConfirmReplacement(true);
                            if (scene.State is "local-ai" or "networking")
                            {
                                draft.SetLocalAiEnabled(true);
                                draft.LocalAiReady = scene.State == "networking";
                                draft.LocalAiNetworkingConsentRequired = scene.State == "networking";
                            }
                            window.NavigateToGatewaySetup();
                            var review = await MountedAsync<GatewaySetupPage>(frame);
                            if (scene.State == "tailscale")
                            {
                                // Admission projection only: the mounted inline control remains off, with no real probe.
                                config.Tailscale.Enabled = true;
                                Invoke(window, Find<Button>(review, "PrimaryButton"));
                            }
                            if (scene.State == "commands")
                            {
                                var exact = Assert.Single(TestSupport.FindDescendants<SettingsExpander>(review));
                                exact.IsExpanded = true;
                                await TestSupport.WaitForSettingsExpanderSettledAsync(ui, exact, true);
                            }
                            if (scene.State == "confirmed") Assert.True(Find<CheckBox>(review, "ReplacementConsent").IsChecked);
                            break;
                        case "tailscale":
                            Assert.False(config.Tailscale.Enabled);
                            window.NavigateToTailscaleSetup();
                            break;
                        case "local-review":
                            preview.Set("capabilities-review-consent");
                            SeedHardware(window, scene.State == "checking"
                                ? pendingHardware.Task : Task.FromResult(Hardware(scene.State)));
                            draft.SetLocalAiEnabled(scene.State != "off");
                            window.NavigateToLocalAiSetup();
                            var detail = await MountedAsync<GatewaySetupDetailPage>(frame);
                            var options = Find<LocalAiSetupControl>(detail, "LocalAiOptions");
                            Assert.True((bool)Field(options, "_forceLocalAiNetworkingConsent")!);
                            if (scene.State is "eligible" or "models" or "busy")
                            {
                                await WaitAsync(() => Find<ComboBox>(options, "LocalAiModelSelector").Items.Count > 0, "synthetic model catalog");
                                Assert.NotNull(config.LocalAi.SelectedModelId);
                            }
                            break;
                        case "networking":
                            window.NavigateToWslNetworking();
                            var networking = await MountedAsync<GatewaySetupDetailPage>(frame);
                            Find<CheckBox>(networking, "LocalAiNetworkingConsentCheckBox").IsChecked = scene.State == "checked";
                            Assert.Equal(scene.State == "checked", config.LocalAi.WslMirroredNetworkingConsent);
                            break;
                        case "preview":
                            preview.Set(scene.State);
                            config.LocalAi.Enabled = scene.State == "progress-local-ai";
                            frame.Navigate(scene.State.StartsWith("wizard", StringComparison.Ordinal)
                                ? typeof(WizardPage) : typeof(ProgressPage), config);
                            if (frame.Content is ProgressPage progress)
                            {
                                Assert.Null(Field(progress, "_pipeline"));
                                Assert.Null(Field(progress, "_runCts"));
                                Assert.Null(Field(progress, "_logger"));
                                Assert.True(((Task)Field(progress, "_pipelineTask")!).IsCompletedSuccessfully);
                                if (scene.State != "milestone")
                                {
                                    await MountedAsync<ProgressPage>(frame);
                                    Assert.Single(TestSupport.FindDescendants<Expander>(progress)).IsExpanded = true;
                                }
                            }
                            else
                            {
                                Assert.Null(Field(frame.Content, "_client"));
                                Assert.Null(Field(frame.Content, "_consoleTail"));
                            }
                            result.Facts.Add("Verified Debug preview returns before StartPipeline/StartWizard; no client, pipeline, logger or console tail.");
                            break;
                        case "ai":
                        case "ai-return":
                        case "dialog":
                            await PrepareAiAsync(window, frame, result, local, preview);
                            break;
                        case "complete":
                            config.LocalAi.Enabled = scene.State == "local-ai";
                            frame.Navigate(typeof(CompletePage), new CompletePageArgs(
                                scene.State is "success" or "local-ai", TimeSpan.FromMinutes(3), null,
                                "Synthetic setup failure. No installation was attempted.",
                                ReviewSummary: SetupReviewSummaryBuilder.Build(config,
                                    (string)Field(window, "_dataDir")!, (string)Field(window, "_localDataDir")!),
                                CanRetryGatewayFallback: scene.State == "fallback", GatewayFallbackVersion: "synthetic-version")
                            { RequiresRestart = scene.State == "restart" });
                            break;
                        case "branch":
                            var mcp = scene.State.StartsWith("mcp", StringComparison.Ordinal);
                            window.SelectGatewayRoute(mcp ? SetupGatewayRoute.McpOnly : SetupGatewayRoute.Deferred);
                            draft.SetNodeMode(!mcp);
                            draft.SetMcpServer(mcp);
                            draft.ApplyProfile(SetupCapabilityProfile.Standard);
                            window.NavigateToCapabilities();
                            if (scene.State.EndsWith("finish", StringComparison.Ordinal))
                            {
                                await MountedAsync<CapabilitiesPage>(frame);
                                Assert.Null(Assert.IsType<CapabilitiesPage>(frame.Content).FindName("WindowsAccess"));
                                Assert.Equal(OnboardingStage.Capabilities,
                                    OnboardingFlowPolicy.GetStages(draft.Route, config)[^1]);
                                Assert.Equal(3, OnboardingFlowPolicy.GetStages(draft.Route, config).Count);
                                result.Facts.Add("Actual last setup page for this branch. Next exits into the host; not invoked. No invented completion screen or persisted settings.");
                            }
                            break;
                        default:
                            throw new InvalidOperationException($"Required scene has no implementation: {scene.Id}");
                    }

                    var page = await MountedAsync<Page>(frame, exact: false);
                    result.Page = page.GetType().FullName!;
                    result.Facts.Add("All Gateway, WSL readiness, GPU, model, credential and runtime inputs are synthetic; no install, download, provider submission, privacy grant or restart.");
                    result.Facts.Add("Mascot animation disabled per instance for static gallery; no motion claim or Windows preference changes.");
                    if (scene.Family == "dialog")
                        await CaptureDialogAsync(window, page, result);
                    else
                        await CaptureViewportsAsync(window, page, result);
                    if (scene.State == "models" && scene.Family == "local-review")
                        await CaptureModelChoicesAsync(window, page, result);
                    Assert.NotNull(Field(window, "_setupLock"));
                    Assert.False(File.Exists(Path.Combine((string)Field(window, "_dataDir")!, "settings.json")),
                        "Gallery must not commit settings.");
                }
                finally
                {
                    try
                    {
                        // Unload first so completing a pending synthetic probe cannot update a later scene.
                        if (frame?.Content is IAsyncDisposable lifetime)
                            await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                        frame?.Navigate(typeof(Page));
                        if (pendingWsl is not null) FinishWsl(pendingWsl);
                        pendingHardware.TrySetResult(Hardware("unsupported"));
                    }
                    finally
                    {
                        if (window is not null)
                        {
                            window.Close();
                            await window.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(10));
                            Assert.Null(SetupWindow.Active);
                        }
                        Application.Current.Resources.MergedDictionaries.Remove(resources);
                        navigation?.Dispose();
                    }
                }
            });
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private async Task PrepareAiAsync(SetupWindow window, Frame frame, SceneResult result, LocalHost local, PreviewScope preview)
    {
        var state = result.Scene.State;
        var transport = new Transport(state);
        var draft = window.AccessDraft;
        string? reviewedModel = null;
        bool? reviewedScreenCapability = null;
        if (result.Scene.Family == "ai-return")
        {
            preview.Set("capabilities-review-consent");
            draft.SetLocalAiEnabled(true);
            window.NavigateToLocalAiSetup();
            var review = await MountedAsync<GatewaySetupDetailPage>(frame);
            await WaitAsync(() => draft.Config.LocalAi.SelectedModelId is not null, "model draft review");
            reviewedModel = draft.Config.LocalAi.SelectedModelId;
            reviewedScreenCapability = draft.GetCapability(SetupCapability.Screen);
            preview.Clear();
            result.Facts.Add($"Same-window review draft retained: model={reviewedModel}; screenCapability={reviewedScreenCapability}. Navigation composition only, not a Gateway workflow claim.");
        }
        var expected = result.Scene.Family == "ai-return" && state == "verification" ? local.Snapshot.ModelRef : null;
        var completions = 0;
        frame.Navigate(typeof(AiSetupPage), new AiSetupPageArgs(draft.Config,
            (string)Field(window, "_dataDir")!, (string)Field(window, "_localDataDir")!,
            () => throw new InvalidOperationException("Gallery must not start the compatibility wizard."),
            () => { completions++; return Task.CompletedTask; },
            ExpectedConfiguredModelRef: expected, TransportFactory: () => transport, LocalAiHost: local,
            ReviewLocalAi: _ => throw new InvalidOperationException("Use gallery's inert mounted review route."),
            ExpectedGatewayId: expected is null ? null : "gallery"));
        var page = await MountedAsync<AiSetupPage>(frame);
        await WaitAsync(() => expected is not null || state == "discovery-failed"
            ? Find<InfoBar>(page, "ErrorBar").IsOpen
            : state == "classic" ? Find<Button>(page, "LegacyButton").Visibility == Visibility.Visible
            : Find<StackPanel>(page, "ChoicePanel").Visibility == Visibility.Visible, "fake AI discovery/verification");
        if (state is "more" or "providers")
        {
            var more = Find<SettingsExpander>(page, "MoreExpander");
            more.IsExpanded = true;
            await TestSupport.WaitForSettingsExpanderSettledAsync(ui, more, true);
        }
        if (state == "api-key")
        {
            await OnboardingNativeProof.InvokeSettingsCardAsync(window, Find<SettingsCard>(page, "ApiKeysButton"));
            await WaitAsync(() => Find<StackPanel>(page, "ApiKeyForm").Visibility == Visibility.Visible,
                "API Keys card opens the separate manual form");
            Assert.Null(page.FindName("ApiKeysChoices"));
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ApiKeyForm").Visibility);
            Assert.Equal("Connect with an API key or token", Find<TextBlock>(page, "ApiKeyFormHeading").Text);
            Assert.False(Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
            Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex = 0;
            var key = Find<PasswordBox>(page, "ApiKeyInput");
            key.ApplyTemplate();
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync();
            key.Password = "synthetic-gallery-never-submitted";
            await WaitAsync(() => Find<Button>(page, "ApiKeyConnectButton").IsEnabled, "explicit inline Connect readiness");
        }
        if (state == "unsupported-hidden")
        {
            Assert.False(local.Snapshot.ShowLocalChoice);
            Assert.False(local.Snapshot.HasInstallationEvidence);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "CandidatesSection").Visibility);
            result.Facts.Add("Confirmed unsupported fresh device: Local AI and its empty container hidden; Gateway candidates retained.");
        }
        if (state is "providers" or "other-local")
        {
            var panel = Find<StackPanel>(page, "ChoicePanel");
            var prepare = Find<StackPanel>(page, "PrepareSection");
            var providers = Find<StackPanel>(page, "SignInSection");
            Assert.True(panel.Children.IndexOf(prepare) < panel.Children.IndexOf(providers));
            Assert.Equal("Set up a local model", Find<TextBlock>(page, "PrepareHeading").Text);
            Assert.Null(page.FindName("LocalAiHeading"));
            result.Facts.Add("Detected Local AI and Gateway model choices precede Gateway preparation, provider cards, API Keys and the separate manual form.");
        }
        if (state == "catalog")
            Assert.Null(page.FindName("CatalogPreference"));
        if (result.Scene.Family == "ai-return")
        {
            Assert.NotNull(reviewedModel);
            Assert.Equal(reviewedModel, draft.Config.LocalAi.SelectedModelId);
            Assert.Equal(reviewedScreenCapability, draft.GetCapability(SetupCapability.Screen));
            result.Facts.Add(expected is null ? "Returned to actual provider choices with retained model draft."
                : $"Exact-model verification requested for {expected}; fake response is unavailable. No completion or activation.");
        }
        Assert.Equal(0, completions);
        Assert.All(transport.Calls, call => Assert.Contains(call, new[] { "openclaw.setup.detect", "openclaw.setup.verify" }));
        result.Facts.Add("Fake RPC calls: " + string.Join(", ", transport.Calls));
    }

    private async Task CaptureDialogAsync(SetupWindow window, Page page, SceneResult result)
    {
        var state = result.Scene.State;
        var type = state is "password" ? "text" : state is "error" or "cancelling" ? "note" : state;
        var dialog = new ProviderSetupDialog();
        var step = new GatewayAiSetupWizardStep
        {
            Id = "gallery-prompt", Type = type, Title = "Provider input",
            Message = "Synthetic provider prompt. No provider was contacted.",
            Sensitive = state == "password", DeviceCode = new("TEST-CODE", 5, "Synthetic device code"),
            ExternalUrl = "https://gallery.invalid/signin",
            Options = [new(JsonSerializer.SerializeToElement("one"), "First synthetic choice"),
                new(JsonSerializer.SerializeToElement("two"), "Second synthetic choice")],
            InitialValue = type == "confirm" ? JsonSerializer.SerializeToElement(true)
                : type == "multiselect" ? JsonSerializer.SerializeToElement(new[] { "one" })
                : type == "select" ? JsonSerializer.SerializeToElement("one") : null,
        };
        dialog.Update(step, GatewayAiSetupPhase.Running, type == "progress", true, state == "cancelling",
            "Provider setup", state == "error" ? "Synthetic provider error. Nothing was submitted." : null);
        var showing = dialog.ShowOwnedAsync(page.XamlRoot, result.Theme);
        try
        {
            var content = Assert.IsType<ScrollViewer>(dialog.Content);
            await WaitAsync(() => content.IsLoaded, "actual provider dialog");
            if (state == "password") Find<PasswordBox>(dialog, "SecretInput").Password = "synthetic-secret";
            if (state == "text") Find<TextBox>(dialog, "TextInput").Text = "synthetic-text";
            await CaptureViewportsAsync(window, page, result, content, dialog);
            Assert.Contains("TEST-CODE", result.OcrLabels);
        }
        finally
        {
            await dialog.CloseAsync();
            await showing;
            Assert.Empty(Find<PasswordBox>(dialog, "SecretInput").Password);
            Assert.Empty(Find<TextBox>(dialog, "TextInput").Text);
        }
    }

    private async Task CaptureModelChoicesAsync(SetupWindow window, Page page, SceneResult result)
    {
        var picker = Find<ComboBox>(Find<LocalAiSetupControl>(page, "LocalAiOptions"), "LocalAiModelSelector");
        picker.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        page.UpdateLayout();
        await OnboardingNativeProof.NextCompositionAsync();
        OnboardingNativeProof.AssertFullyVisible(picker, Assert.IsType<Grid>(window.Content));
        picker.IsDropDownOpen = true;
        try
        {
            await WaitAsync(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot).Count > 0, "model choices popup");
            var popup = Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot));
            var content = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            await WaitAsync(() => content.IsLoaded && content.ActualHeight > 0, "model choices layout");
            OnboardingNativeProof.AssertFullyVisible(content, Assert.IsType<Grid>(window.Content));
            var scroller = Assert.Single(TestSupport.FindDescendants<ScrollViewer>(content), IsVisible);
            await CaptureViewportsAsync(window, page, result, scroller);
            result.Facts.Add($"Actual model picker popup captured with {picker.Items.Count} catalog entries. No model was downloaded.");
        }
        finally { picker.IsDropDownOpen = false; }
    }

    private async Task CaptureViewportsAsync(SetupWindow window, Page page, SceneResult result,
        ScrollViewer? dialogScroll = null, ProviderSetupDialog? dialog = null)
    {
        var root = Assert.IsType<Grid>(window.Content);
        root.UpdateLayout();
        await OnboardingNativeProof.NextCompositionAsync();
        var grid = Assert.IsType<Grid>(page.Content);
        var outer = dialogScroll ?? grid.Children.OfType<ScrollViewer>().SingleOrDefault(IsVisible);
        if (outer is null)
            await CaptureAsync(window, page, result, "page", 0, 0, null, dialog);
        else
        {
            await SweepAsync(outer, "page");
            // Nested log/list viewports are separate scroll surfaces, not a reason to truncate the outer page.
            var nested = TestSupport.FindDescendants<ScrollViewer>(outer)
                .Where(scroll => IsVisible(scroll) && (scroll.ScrollableHeight > 0 || scroll.ScrollableWidth > 0)).ToArray();
            for (var i = 0; i < nested.Length; i++)
            {
                var scroll = nested[i];
                scroll.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                root.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                await SweepAsync(scroll, $"nested-{i + 1:D2}-{scroll.Name}");
            }
        }
        result.AllViewportsCaptured = true;

        async Task SweepAsync(ScrollViewer scroll, string surface)
        {
            Assert.True(scroll.ViewportHeight > 0 && scroll.ViewportWidth > 0);
            var initialX = scroll.HorizontalOffset;
            var initialY = scroll.VerticalOffset;
            var count = 0;
            try
            {
                var y = 0d;
                while (true)
                {
                    var x = 0d;
                    while (true)
                    {
                        Assert.True(++count <= 48, "Viewport safety bound reached; gallery is incomplete.");
                        scroll.ChangeView(x, y, null, disableAnimation: true);
                        await WaitAsync(() => NativeProofLayout.PhysicalEdge(scroll.VerticalOffset, root.XamlRoot.RasterizationScale) ==
                                NativeProofLayout.PhysicalEdge(y, root.XamlRoot.RasterizationScale) &&
                            NativeProofLayout.PhysicalEdge(scroll.HorizontalOffset, root.XamlRoot.RasterizationScale) ==
                                NativeProofLayout.PhysicalEdge(x, root.XamlRoot.RasterizationScale), "gallery scroll position");
                        root.UpdateLayout();
                        await CaptureAsync(window, page, result, surface, x, y, scroll, dialog);
                        if (x >= scroll.ScrollableWidth) break;
                        x = Math.Min(x + scroll.ViewportWidth * 0.85, scroll.ScrollableWidth);
                    }
                    if (y >= scroll.ScrollableHeight) break;
                    y = Math.Min(y + scroll.ViewportHeight * 0.85, scroll.ScrollableHeight);
                }
            }
            finally { scroll.ChangeView(initialX, initialY, null, disableAnimation: true); }
        }
    }

    private async Task CaptureAsync(SetupWindow window, Page page, SceneResult result, string surface,
        double x, double y, ScrollViewer? scroll, ProviderSetupDialog? dialog)
    {
        var root = Assert.IsType<Grid>(window.Content);
        var grid = Assert.IsType<Grid>(page.Content);
        var labels = new List<string>();
        if (dialog is not null)
        {
            labels.Add("Provider input");
            labels.Add("Refresh");
            var code = Find<TextBlock>(dialog, "DeviceCode");
            if (scroll is not null && IsFullyVisible(code, scroll)) labels.Add("TEST-CODE");
            var status = Find<TextBlock>(dialog, "DialogStatus");
            if (scroll is not null && IsFullyVisible(status, scroll)) labels.Add(status.Text);
        }
        else
        {
            var contract = HeaderFor(result.Scene);
            Assert.Equal(contract.PageType, page.GetType());
            var heading = ResolveHeader(page, contract);
            Assert.Equal(contract.Text, heading.Text);
            Assert.False(heading.IsTextTrimmed);
            Assert.True(IsVisible(heading), $"Expected visible semantic header '{contract.Text}'.");
            var headerBounds = heading.TransformToVisual(root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, heading.ActualWidth, heading.ActualHeight));
            var headerInViewport = NativeProofLayout.ContainsPhysicalEdges(
                (0, 0, root.ActualWidth, root.ActualHeight),
                (headerBounds.Left, headerBounds.Top, headerBounds.Right, headerBounds.Bottom),
                root.XamlRoot.RasterizationScale);
            if (headerInViewport)
            {
                OnboardingNativeProof.AssertFullyVisible(heading, root);
                if (result.Scene.Id.StartsWith("followup-welcome-", StringComparison.Ordinal))
                {
                    await OnboardingNativeProof.AssertNativeLabelAfterWpfLoadAsync(
                        WinRT.Interop.WindowNative.GetWindowHandle(window), contract.Text);
                    OnboardingNativeProof.AssertProductDpi(window);
                    labels.Add("Install a local native gateway");
                    labels.Add("Connect to an existing Gateway");
                    labels.Add("Your PC supports Local AI");
                }
                else
                    labels.Add(contract.Text);
            }
            else
                Assert.Contains(contract.Text, result.OcrLabels); // A scrolling header must already be proven in its first viewport.
            var footerRow = grid.RowDefinitions.Count - 1;
            foreach (var chrome in grid.Children.OfType<FrameworkElement>().Where(element => IsVisible(element) &&
                element is not ScrollViewer && (Grid.GetRow(element) == 0 || Grid.GetRow(element) == footerRow)))
            {
                OnboardingNativeProof.AssertFullyVisible(chrome, root);
                foreach (var button in TestSupport.FindDescendants<Button>(chrome).Where(IsVisible))
                    if (button.Content is string label && !string.IsNullOrWhiteSpace(label))
                    {
                        OnboardingNativeProof.AssertFullyVisible(button, root);
                        labels.Add(label);
                    }
            }
        }
        Assert.NotEmpty(labels);
        var file = $"{_run}-{_results.IndexOf(result) + 1:D3}-{result.Scene.Id}-{result.Theme}-v{result.Frames.Count + 1:D2}";
        var path = Path.Combine(_directory, file + ".png");
        Assert.False(File.Exists(path), "Never overwrite an existing gallery frame.");
        Assert.False(File.Exists(Path.ChangeExtension(path, ".json")));
        var facts = new
        {
            sceneId = result.Scene.Id, result.Scene.State, theme = result.Theme.ToString(), result.Page,
            classification = result.Classification, surface, horizontalOffset = x, verticalOffset = y,
            viewportWidth = scroll?.ViewportWidth, viewportHeight = scroll?.ViewportHeight,
            scrollableWidth = scroll?.ScrollableWidth, scrollableHeight = scroll?.ScrollableHeight,
            animationDisabledByTest = true, syntheticSecretsOnly = true, result.Facts,
        };
        using (await OnboardingNativeProof.CaptureAsync(window, file, output, labels.Distinct().ToArray(), facts,
            requiredContent: dialog?.Content as FrameworkElement ?? page)) { }
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
        result.Frames.Add(new(Path.GetFileName(path), Path.GetFileName(Path.ChangeExtension(path, ".json")),
            metadata.RootElement.Clone()));
        result.OcrLabels.UnionWith(labels);
        WriteManifest();
    }

    private async Task<T> MountedAsync<T>(Frame frame, bool exact = true) where T : Page
    {
        var page = exact ? Assert.IsType<T>(frame.Content) : Assert.IsAssignableFrom<T>(frame.Content);
        await WaitAsync(() => page.IsLoaded, $"{page.GetType().Name} Loaded");
        foreach (var mascot in TestSupport.FindDescendants<OnboardingMascot>(page)) mascot.IsAnimationEnabled = false;
        page.UpdateLayout();
        await OnboardingNativeProof.NextCompositionAsync();
        return page;
    }

    private void WriteManifest()
    {
        var missing = _results.Where(result => !result.AllViewportsCaptured || result.Frames.Count == 0)
            .Select(result => $"{result.Scene.Id}/{result.Theme}").ToArray();
        var manualReview = _results.SelectMany(result => result.Frames)
            .Where(frame => frame.Proof.TryGetProperty("machineOcrPassed", out var passed) && !passed.GetBoolean())
            .Select(frame => new { frame.Image, frame.Metadata }).ToArray();
        var manifest = new
        {
            runId = _run, generatedUtc = DateTimeOffset.UtcNow,
            status = _sourceVerifiedAfterCapture && missing.Length == 0 ? "candidate-coverage-complete; parent-review-required" : "incomplete-or-unverified",
            acceptedForDesktop = false,
            machineOcrUnverifiedFrames = manualReview,
            sourceFreeze = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_FREEZE_DIR"),
            sourceManifestSha256 = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_MANIFEST_SHA256"),
            sourceVerifiedAfterCapture = _sourceVerifiedAfterCapture,
            failure = _failure,
            requiredSceneIds = _results.Select(result => $"{result.Scene.Id}/{result.Theme}"),
            missingRequiredScenes = missing,
            excludedClaims = new[] { "Installation success", "real Gateway connectivity", "provider authentication",
                "hardware readiness", "model download", "privacy grants", "live mascot motion" },
            unavailableStates = new[] { "Tailscale enabled status: no injected probe seam; enabling would invoke the real CLI. Off detail and review blocker are captured.",
                "Live motion is a separate parent-owned test. Static halo is not animation proof." },
            scenes = _results.Select(result => new
            {
                result.Scene.Id, result.Scene.Family, result.Scene.State, theme = result.Theme.ToString(),
                result.Page, result.Classification, result.Status, result.Seconds, result.Failure,
                result.AllViewportsCaptured, result.Facts, frames = result.Frames,
            }),
        };
        File.WriteAllText(Path.Combine(_directory, _run + ".json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name));

    private static Task WaitAsync(Func<bool> predicate, string operation) =>
        TestSupport.WaitForRenderedConditionAsync(predicate, operation);

    private static bool IsVisible(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement uiElement &&
                (uiElement.Visibility != Visibility.Visible || uiElement.Opacity <= 0 ||
                 AutomationProperties.GetAccessibilityView(uiElement) == AccessibilityView.Raw) ||
                parent is OnboardingMascot) return false;
        return true;
    }

    private static TextBlock ResolveHeader(Page page, HeaderContract contract)
    {
        if (contract.ControlName is { } name)
            return Find<TextBlock>(page, name);
        var grid = Assert.IsType<Grid>(page.Content);
        if (contract.ContainerName is { } containerName)
        {
            var container = Find<StackPanel>(page, containerName);
            return SelectDirectHeader(container.Children.OfType<StackPanel>().Prepend(container), contract.Text);
        }
        return SelectDirectHeader(grid.Children.OfType<StackPanel>().Where(panel => Grid.GetRow(panel) == 0), contract.Text);
    }

    private static TextBlock SelectDirectHeader(IEnumerable<StackPanel> containers, string expected) =>
        Assert.Single(containers.SelectMany(container => container.Children.OfType<TextBlock>()),
            text => text.Text == expected && IsVisible(text));

    private static bool IsFullyVisible(FrameworkElement element, ScrollViewer scroll)
    {
        if (!IsVisible(element)) return false;
        var bounds = element.TransformToVisual(scroll).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        return NativeProofLayout.ContainsPhysicalEdges((0, 0, scroll.ViewportWidth, scroll.ViewportHeight),
            (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), scroll.XamlRoot.RasterizationScale);
    }

    private static void Invoke(Window window, Button button)
    {
        OnboardingNativeProof.InvokeButton(window, button);
    }

    private static void RecordInspection(SetupAccessDraft draft, bool replacement) =>
        draft.RecordWslInspection(new ExistingConfigDetector.ExistingConfig(false, null, null,
            replacement, replacement, false, replacement ? draft.Config.DistroName : null, false, 0, []));

    private static object? Field(object target, string name) =>
        RequiredField(target, name).GetValue(target);
    private static void SetField(object target, string name, object value) =>
        RequiredField(target, name).SetValue(target, value);
    private static void SeedHardware(SetupWindow window, Task<HostHardwareInfo> hardware)
    {
        var field = RequiredField(window, "_localAiHardwareProbe");
        var cache = Activator.CreateInstance(field.FieldType, new Func<HostHardwareInfo>(() =>
            hardware.IsCompletedSuccessfully ? hardware.Result
                : throw new InvalidOperationException("The fixture hardware probe has not completed.")))!;
        SetField(cache, "_probeTask", hardware);
        field.SetValue(window, cache);
    }
    private static FieldInfo RequiredField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Inert gallery seam disappeared: {target.GetType().Name}.{name}");
    private static void InvokePrivate(object target, string name, params object[] args) =>
        (target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing gallery navigation seam: {name}")).Invoke(target, args);

    private static object WslResult(string kind)
    {
        var assembly = typeof(SetupAccessDraft).Assembly;
        var result = assembly.GetType("OpenClaw.SetupEngine.WslViabilityResult", throwOnError: true)!;
        var enumType = assembly.GetType("OpenClaw.SetupEngine.WslViabilityKind", throwOnError: true)!;
        return Activator.CreateInstance(result, Enum.Parse(enumType, kind),
            "Synthetic WSL readiness result.", "No WSL command was run.")!;
    }

    private static void SeedWsl(SetupWindow window, string kind)
    {
        var result = WslResult(kind);
        var task = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result.GetType()).Invoke(null, [result])!;
        SetField(Field(window, "_wslViabilityProbe")!, "_inspectionTask", task);
    }

    private static object SeedPendingWsl(SetupWindow window)
    {
        var source = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(WslResult("Ready").GetType()),
            TaskCreationOptions.RunContinuationsAsynchronously)!;
        SetField(Field(window, "_wslViabilityProbe")!, "_inspectionTask", source.GetType().GetProperty("Task")!.GetValue(source)!);
        return source;
    }

    private static void FinishWsl(object source, bool fail = false)
    {
        if (fail)
            source.GetType().GetMethod("SetException", [typeof(Exception)])!.Invoke(source,
                [new InvalidOperationException("Synthetic WSL inspection failure. No WSL command was run.")]);
        else
            source.GetType().GetMethod("SetResult")!.Invoke(source, [WslResult("EnvironmentBlocked")]);
    }

    private static void SetHalo(OnboardingMascot mascot)
    {
        Assert.NotNull(Field(mascot, "_heroGlow"));
        Assert.False(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast,
            "Static coral halo is unavailable in high contrast; do not override the Windows preference.");
        Assert.True(Assert.IsType<Microsoft.UI.Composition.SpriteVisual>(Field(Field(mascot, "_heroGlow")!, "_visual")).IsVisible);
    }

    private static ResourceDictionary LoadProgressResources(string repo)
    {
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

    private sealed class PreviewScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(PreviewVariable);
        internal void Set(string page)
        {
            Environment.SetEnvironmentVariable(PreviewVariable, page);
            var type = typeof(SetupWindow).Assembly.GetType("OpenClaw.SetupEngine.UI.SetupPreview", throwOnError: true)!;
            Assert.Equal(page, type.GetProperty("RequestedPage")!.GetValue(null));
            Assert.Equal(true, type.GetProperty("IsActive")!.GetValue(null));
        }
        internal void Clear() => Environment.SetEnvironmentVariable(PreviewVariable, null);
        public void Dispose() => Environment.SetEnvironmentVariable(PreviewVariable, _previous);
    }

    private sealed class SceneResult(Scene scene, ElementTheme theme)
    {
        internal Scene Scene { get; } = scene;
        internal ElementTheme Theme { get; } = theme;
        internal string Page { get; set; } = "";
        internal string Classification { get; set; } = "Synthetic inputs on actual production UI";
        internal string Status { get; set; } = "not-attempted";
        internal string? Failure { get; set; }
        internal double Seconds { get; set; }
        internal bool AllViewportsCaptured { get; set; }
        internal List<string> Facts { get; } = [];
        internal HashSet<string> OcrLabels { get; } = [];
        internal List<GalleryFrame> Frames { get; } = [];
    }

    private sealed record GalleryFrame(string Image, string Metadata, JsonElement Proof);
}
