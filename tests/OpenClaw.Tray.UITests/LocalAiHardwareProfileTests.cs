using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Adapters;
using OpenClawTray.Services;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "NativeOnboardingProof")]
public sealed class LocalAiHardwareProfileTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(LocalAiRuntimeState.Stopped, ElementTheme.Light)]
    [InlineData(LocalAiRuntimeState.Stopped, ElementTheme.Dark)]
    [InlineData(LocalAiRuntimeState.Healthy, ElementTheme.Light)]
    [InlineData(LocalAiRuntimeState.Healthy, ElementTheme.Dark)]
    public async Task LocalAiPage_NativeSelectionExplainsDisabledStartAndRestart(
        LocalAiRuntimeState state, ElementTheme theme)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var originalSize = ui.TestWindow.AppWindow.Size;
            var originalPosition = ui.TestWindow.AppWindow.Position;
            FitWindow(ui.TestWindow);
            var now = DateTimeOffset.UtcNow;
            var runtime = new InertRuntime
            {
                RequiresNativeSelection = true,
                Snapshot = new(state, state == LocalAiRuntimeState.Healthy
                    ? LocalAiOwnership.CompanionManaged : LocalAiOwnership.None,
                    new Uri("http://127.0.0.1:18080"), "fixture", LocalModelCatalog.Qwen35BModelId,
                    new(LocalAiModelAvailabilityState.Verified, now, new string('a', 64), 1),
                    state == LocalAiRuntimeState.Healthy ? 1234 : null,
                    state == LocalAiRuntimeState.Healthy ? now : null,
                    null, now) { GatewayRouteRequiresResolution = false },
            };
            using var viewModel = new LocalAiPageViewModel(runtime, new InertGateway(),
                new InertCommands(), new WinUIDispatcher(ui.Dispatcher), new HardwareProbe(8));
            var page = new LocalAiPage { DataContext = viewModel };
            ui.Container.Children.Add(page);
            try
            {
                viewModel.Activate(null);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(ui.Container, theme);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => page.IsLoaded && viewModel.IsAvailabilityKnown, "Local AI selection guidance");
                var guidance = Assert.IsType<TextBlock>(page.FindName("NativeSelectionGuidanceText"));
                var start = Assert.IsType<Button>(page.FindName("StartButton"));
                var restart = Assert.IsType<Button>(page.FindName("RestartButton"));
                Assert.Equal(Visibility.Visible, guidance.Visibility);
                Assert.Contains("Use Local AI", guidance.Text);
                Assert.Contains("WSL Gateway", guidance.Text);
                Assert.False(start.IsEnabled);
                Assert.False(restart.IsEnabled);
                Assert.Equal(state == LocalAiRuntimeState.Healthy,
                    Assert.IsType<Button>(page.FindName("StopButton")).IsEnabled);
                OnboardingNativeProof.ActivateOwned(ui.TestWindow);
                await OnboardingNativeProof.AssertNativeLabelAfterWpfLoadAsync(
                    WinRT.Interop.WindowNative.GetWindowHandle(ui.TestWindow), guidance.Text);
                // Verify the full visible text through UIA; OCR anchors avoid the capital-I/l ambiguity.
                using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow,
                    $"local-ai-native-selection-{state}-{theme}", output,
                    ["Before starting or restarting", "native Gateway"],
                    new { state = state.ToString(), theme = theme.ToString(), syntheticRuntime = true,
                        startEnabled = start.IsEnabled, restartEnabled = restart.IsEnabled },
                    requiredContent: page)) { }

                runtime.SetNativeSelectionRequired(false);
                Assert.Equal(Visibility.Collapsed, guidance.Visibility);
                Assert.Equal(state == LocalAiRuntimeState.Stopped, start.IsEnabled);
                Assert.Equal(state == LocalAiRuntimeState.Healthy, restart.IsEnabled);
            }
            finally
            {
                ui.Container.Children.Clear();
                ui.TestWindow.AppWindow.Resize(originalSize);
                ui.TestWindow.AppWindow.Move(originalPosition);
            }
        });
    }

#if LOCAL_AI_BASELINE
    private const bool IsBaseline = true;
#else
    private const bool IsBaseline = false;
#endif

    [Theory]
    [InlineData(8, ElementTheme.Light)]
    [InlineData(8, ElementTheme.Dark)]
    [InlineData(6, ElementTheme.Light)]
    [InlineData(6, ElementTheme.Dark)]
    [InlineData(0, ElementTheme.Light)]
    [InlineData(0, ElementTheme.Dark)]
    public async Task LocalAiPage_RendersHardwareFallbackOrUnavailable(int vramGiB, ElementTheme theme)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var originalSize = ui.TestWindow.AppWindow.Size;
            var originalPosition = ui.TestWindow.AppWindow.Position;
            FitWindow(ui.TestWindow);
            using var viewModel = new LocalAiPageViewModel(new InertRuntime(), new InertGateway(),
                new InertCommands(), new WinUIDispatcher(ui.Dispatcher), new HardwareProbe(vramGiB));
            var page = new LocalAiPage { DataContext = viewModel };
            ui.Container.Children.Add(page);
            try
            {
                viewModel.Activate(null);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(ui.Container, theme);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => page.IsLoaded && viewModel.IsAvailabilityKnown, "Local AI hardware eligibility");
                var fits = !IsBaseline && vramGiB == 8;
                var fallback = page.FindName("LocalAiProfileSelectionText") as TextBlock;
                var unavailable = Assert.IsType<InfoBar>(page.FindName("LocalAiUnavailableInfoBar"));
                Assert.Equal(fits ? Visibility.Collapsed : Visibility.Visible, unavailable.Visibility);
                Assert.Equal(fits, viewModel.CanRetrySetup);
                if (fits)
                {
                    Assert.NotNull(fallback);
                    Assert.Equal(Visibility.Visible, fallback.Visibility);
                    Assert.Contains("Setup can use", fallback.Text);
                    Assert.Contains("Bonsai 2", fallback.Text);
                    Assert.Contains("8,192", fallback.Text);
                }
                else if (fallback is not null)
                    Assert.Equal(Visibility.Collapsed, fallback.Visibility);

                await CaptureAsync(ui.TestWindow, page, "settings", vramGiB, theme,
                    fits ? fallback!.Text : unavailable.Title);
            }
            finally
            {
                ui.Container.Children.Clear();
                ui.TestWindow.AppWindow.Resize(originalSize);
                ui.TestWindow.AppWindow.Move(originalPosition);
            }
        });
    }

    [Theory]
    [InlineData(8, ElementTheme.Light)]
    [InlineData(8, ElementTheme.Dark)]
    [InlineData(6, ElementTheme.Light)]
    [InlineData(6, ElementTheme.Dark)]
    [InlineData(0, ElementTheme.Light)]
    [InlineData(0, ElementTheme.Dark)]
    public async Task Onboarding_OffersOnlyFittingProfiles(int vramGiB, ElementTheme theme)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var directory = new TempDirectory("local-ai-profile-proof-");
        var configPath = directory.Combine("setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: directory.Combine("data"), localDataDir: directory.Combine("local"), commandLineArgs: []));
                window.AccessDraft.Config.NativeLocalAiAcquisition = true;
                FitWindow(window);
                var probeField = typeof(SetupWindow).GetField("_localAiHardwareProbe", BindingFlags.NonPublic | BindingFlags.Instance)!;
                probeField.SetValue(window, Activator.CreateInstance(probeField.FieldType,
                    new Func<HostHardwareInfo>(() => new HardwareProbe(vramGiB).Probe())));
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                using var navigation = OnboardingNativeProof.TrackNavigation(frame);
                window.NavigateToLocalAiSetup();
                OnboardingNativeProof.ActivateOwned(window);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                var page = Assert.IsAssignableFrom<Page>(frame.Content);
                var control = Assert.IsType<LocalAiSetupControl>(page.FindName("LocalAiOptions"));
                var toggle = Assert.IsType<ToggleSwitch>(control.FindName("LocalAiToggle"));
                var unavailable = Assert.IsType<InfoBar>(control.FindName("LocalAiUnavailablePanel"));
                var selector = Assert.IsType<ComboBox>(control.FindName("LocalAiModelSelector"));
                var progress = Assert.IsType<ProgressRing>(control.FindName("LocalAiAvailabilityProgressRing"));
                var fits = !IsBaseline && vramGiB == 8;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => control.IsLoaded && !progress.IsActive && (fits
                        ? selector.SelectedItem is ComboBoxItem
                        : unavailable.Visibility == Visibility.Visible && unavailable.Title == "Local AI is not available"),
                    "completed onboarding hardware eligibility");
                Assert.True(toggle.IsEnabled); // Preserve the existing opt-out escape hatch on unsupported hardware.
                Assert.Equal(fits, selector.IsEnabled);
                Assert.Equal(fits ? Visibility.Collapsed : Visibility.Visible, unavailable.Visibility);
                string label;
                if (fits)
                {
                    toggle.IsOn = true;
                    var fallback = Assert.IsType<TextBlock>(control.FindName("LocalAiProfileSelectionText"));
                    Assert.Contains("Bonsai 2", fallback.Text);
                    Assert.Contains("8,192", fallback.Text);
                    Assert.Contains("Bonsai 2", Assert.IsType<ComboBoxItem>(selector.SelectedItem).Content.ToString());
                    label = fallback.Text;
                }
                else
                    label = unavailable.Title;
                await CaptureAsync(window, page, "onboarding", vramGiB, theme, label);
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await window.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(10));
                }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    private async Task CaptureAsync(Window window, FrameworkElement page, string surface,
        int vramGiB, ElementTheme theme, string label)
    {
        OnboardingNativeProof.ActivateOwned(window);
        // OCR checks decision phrases, while the exact profile copy is asserted before capture.
        string[] labels = !IsBaseline && vramGiB == 8
            ? ["Setup can use Bonsai 2", "experimental", "8,192 context tokens"]
            : ["not available"];
        using var capture = await OnboardingNativeProof.CaptureAsync(window,
            $"{(IsBaseline ? "before" : "after")}-{surface}-{vramGiB}gib-{theme}", output, labels,
            new { baseline = IsBaseline, surface, vramGiB, theme = theme.ToString(), syntheticHardware = true, exactCopy = label },
            requiredContent: page);
        OnboardingNativeProof.AssertSourceUnchanged();
    }

    private static void FitWindow(Window window)
    {
        var workArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Resize(new(Math.Min(1050, workArea.Width - 24), Math.Min(950, workArea.Height - 24)));
        window.AppWindow.Move(new(workArea.X + 12, workArea.Y + 12));
    }

    private sealed class HardwareProbe(int vramGiB) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe()
        {
            var hardware = new HostHardwareInfo(Architecture.X64, 32L << 30, 24L << 30,
                vramGiB == 0 ? [] : [new(GpuVendor.Nvidia, "Synthetic RTX 4060", (long)vramGiB << 30,
                    (long)vramGiB << 30, CudaMajorVersion: 13, StableId: "synthetic-profile-proof")], false);
#if !LOCAL_AI_BASELINE
            hardware = hardware with { LocalAiDiskSpace = new(20L << 30, 20L << 30, true) };
#endif
            return hardware;
        }
    }

    private sealed class InertRuntime : ILocalAiRuntime
    {
        public bool RequiresNativeSelection { get; set; }
        public event EventHandler? SelectionChanged;
        public void SetNativeSelectionRequired(bool required)
        {
            RequiresNativeSelection = required;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        public LocalAiRuntimeSnapshot Snapshot { get; init; } = LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"), DateTimeOffset.UtcNow);
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InertGateway : IPermissionsPageRuntimeSource
    {
        public event EventHandler<PermissionsRuntimeSourceChangedEventArgs>? Changed { add { } remove { } }
        public PermissionsRuntimeSourceSnapshot Current { get; } = new(GatewayConnectionSnapshot.Idle,
            null, "", false, 0, [], [], PermissionsGatewayAllowlistState.NoConfig, PermissionsVoiceSetupRequirement.None);
    }

    private sealed class InertCommands : IAppCommands
    {
        public void OpenDashboard(string? path = null) => throw new NotSupportedException();
        public void Navigate(string pageTag) => throw new NotSupportedException();
        public void Reconnect() => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public void ShowVoiceOverlay() => throw new NotSupportedException();
        public void ShowChat() => throw new NotSupportedException();
        public void CheckForUpdates() => throw new NotSupportedException();
        public void ShowOnboarding() => throw new NotSupportedException();
        public void ShowLocalAiSetup() => throw new NotSupportedException();
        public void ShowGatewayWizard() => throw new NotSupportedException();
        public void ShowConnectionStatus() => throw new NotSupportedException();
        public void NotifySettingsSaved() => throw new NotSupportedException();
        public Task<bool> ApplyAutoStart(SettingsWriteOrigin origin, bool autoStart) => throw new NotSupportedException();
        public Task<bool> ResendOpenTelemetryProbeAsync() => throw new NotSupportedException();
    }
}
