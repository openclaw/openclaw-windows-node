using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SetupWindowMinimumSizeTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task NativePresenter_ClampsBelowMinimumAndDetachesOnConfigurationErrorClose(ElementTheme theme)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("setup-minimum-");
        var config = temp.Combine("invalid.json");
        File.WriteAllText(config, "{invalid");
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: config,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"), commandLineArgs: [],
                    startupRegistrationAllowed: false));
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var dpi = GetDpiForWindow(hwnd);
                var initial = SetupWindowSizing.InitialPixels(dpi);
                const int smCxMaxTrack = 59, smCyMaxTrack = 60;
                var maxWidth = GetSystemMetricsForDpi(smCxMaxTrack, dpi);
                var maxHeight = GetSystemMetricsForDpi(smCyMaxTrack, dpi);
                Assert.True(maxWidth > 0 && maxHeight > 0);
                output.WriteLine($"initial={window.AppWindow.Size}; requested={initial}; maxTrack={maxWidth}x{maxHeight}");
                Assert.Equal(Math.Min(initial.Width, maxWidth), window.AppWindow.Size.Width);
                Assert.Equal(Math.Min(initial.Height, maxHeight), window.AppWindow.Size.Height);
                window.Activate();
                window.AppWindow.Move(new(-32000, -32000));
                var root = Assert.IsType<Grid>(window.Content);
                await TestSupport.WaitForRenderedConditionAsync(() => root.IsLoaded, "minimum window loaded");
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                var minimum = await ResizeBelowMinimumAsync(window, ui, output, $"error-{theme}");
                var presenter = Assert.IsType<OverlappedPresenter>(window.AppWindow.Presenter);
                Assert.True(presenter.IsResizable);
                Assert.True(presenter.IsMaximizable);
                Assert.Equal(minimum.Width, presenter.PreferredMinimumWidth);
                Assert.Equal(minimum.Height, presenter.PreferredMinimumHeight);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is CompletePage { IsLoaded: true }, "configuration error mounted");
                var page = Assert.IsType<CompletePage>(frame.Content);
                root.UpdateLayout();
                OnboardingNativeProof.AssertFullyVisible(Assert.IsType<Button>(page.FindName("LaunchButton")), root);
                Assert.True(Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>()).ViewportHeight > 100);
                await SaveViewportsAsync(window, page, ui, output, $"setup-minimum-config-error-{theme}");
                window.AppWindow.Resize(new(minimum.Width + 120, minimum.Height + 120));
                await ui.YieldToRenderAsync();
                Assert.True(window.AppWindow.Size.Width > minimum.Width);
                Assert.True(window.AppWindow.Size.Height > minimum.Height);
                var size = window.AppWindow.Size;
                typeof(SetupWindow).GetMethod("ApplyMinimumWindowSize", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Assert.Equal(size, window.AppWindow.Size);
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await window.CleanupCompleted;
                    Assert.Null(typeof(SetupWindow).GetField("_minimumSizeRoot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
                    typeof(SetupWindow).GetMethod("ApplyMinimumWindowSize", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Theory]
    [InlineData("native-install", ElementTheme.Light, false)]
    [InlineData("native-install", ElementTheme.Dark, false)]
    [InlineData("capabilities-native", ElementTheme.Light, false)]
    [InlineData("capabilities-native", ElementTheme.Dark, false)]
    [InlineData("capabilities-wsl", ElementTheme.Light, false)]
    [InlineData("capabilities-wsl", ElementTheme.Dark, false)]
    [InlineData("capabilities-native", ElementTheme.Light, true)]
    [InlineData("capabilities-native", ElementTheme.Dark, true)]
    [InlineData("capabilities-wsl", ElementTheme.Light, true)]
    [InlineData("capabilities-wsl", ElementTheme.Dark, true)]
    public async Task MinimumViewport_KeepsNativeInstallAndCapabilitiesReachable(
        string scene, ElementTheme theme, bool compactViewport)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        var captureName = $"{scene}-{theme}{(compactViewport ? "-compact" : "")}";
        using var temp = new TempDirectory("setup-minimum-pages-");
        var config = temp.Combine("setup.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var oldPreview = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE");
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: config,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"), commandLineArgs: [],
                    startupRegistrationAllowed: false));
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                window.SelectGatewayRoute(scene == "capabilities-wsl" ? SetupGatewayRoute.ManagedWsl : SetupGatewayRoute.Native);
                if (scene == "native-install")
                {
                    Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE", "native");
                    var preview = typeof(SetupWindow).Assembly.GetType("OpenClaw.SetupEngine.UI.SetupPreview", throwOnError: true)!;
                    Assert.Equal(true, preview.GetProperty("IsActive")!.GetValue(null));
                    typeof(SetupWindow).GetMethod("NavigateToNativeGatewaySetup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                }
                else
                    window.NavigateToCapabilities();
                window.Activate();
                window.AppWindow.Move(new(-32000, -32000));
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is Page { IsLoaded: true }, scene);
                var page = Assert.IsAssignableFrom<Page>(frame.Content);
                await ResizeBelowMinimumAsync(window, ui, output, captureName);
                if (compactViewport)
                {
                    page.Width = 704;
                    Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>()).Height = 170;
                }
                if (page is NativeGatewaySetupPage)
                {
                    Assert.Null(typeof(NativeGatewaySetupPage).GetField("_operation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
                    Assert.Contains("Preview only", Assert.IsType<TextBlock>(page.FindName("StatusText")).Text);
                    Assert.IsType<Button>(page.FindName("RetryButton")).Visibility = Visibility.Visible;
                }
                else
                {
                    var fineTune = Assert.IsType<SettingsExpander>(page.FindName("FineTuneExpander"));
                    fineTune.IsExpanded = true;
                    Assert.Equal(8, fineTune.Items.Count);
                    await TestSupport.WaitForSettingsExpanderSettledAsync(ui, fineTune, true);
                }
                root.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
                var grid = Assert.IsType<Grid>(page.Content);
                var footer = Assert.Single(grid.Children.OfType<Grid>(), child => Grid.GetRow(child) == 2);
                OnboardingNativeProof.AssertFullyVisible(footer, root);
                foreach (var action in TestSupport.FindDescendants<Button>(footer).Where(button => button.Visibility == Visibility.Visible))
                    OnboardingNativeProof.AssertFullyVisible(action, root);
                if (page is CapabilitiesPage capabilities)
                    await SaveCapabilitiesAsync(window, capabilities, ui, output, $"setup-minimum-{captureName}");
                else
                    await SaveViewportsAsync(window, page, ui, output, $"setup-minimum-{captureName}");
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE", oldPreview);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    internal static async Task<(int Width, int Height)> ResizeBelowMinimumAsync(
        SetupWindow window, UIThreadFixture ui, ITestOutputHelper output, string scene)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        var minimum = SetupWindowSizing.MinimumPixels(dpi);
        var presenter = Assert.IsType<OverlappedPresenter>(window.AppWindow.Presenter);
        Assert.Equal(minimum.Width, presenter.PreferredMinimumWidth);
        Assert.Equal(minimum.Height, presenter.PreferredMinimumHeight);
        window.AppWindow.Resize(new(240, 160));
        await ui.YieldToRenderAsync();
        Assert.True(GetWindowRect(hwnd, out var rect));
        Assert.True(rect.Right - rect.Left >= minimum.Width);
        Assert.True(rect.Bottom - rect.Top >= minimum.Height);
        var work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var measurement = new
        {
            scene, actualDpi = dpi, rasterizationScale = Assert.IsType<Grid>(window.Content).XamlRoot.RasterizationScale,
            requested = new { width = 240, height = 160 },
            preferredMinimum = new { width = minimum.Width, height = minimum.Height },
            actualWindow = new { width = window.AppWindow.Size.Width, height = window.AppWindow.Size.Height },
            hwndBounds = new { width = rect.Right - rect.Left, height = rect.Bottom - rect.Top },
            workArea = new { width = work.Width, height = work.Height },
            presenter.IsResizable,
        };
        output.WriteLine(JsonSerializer.Serialize(measurement));
        if (Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR") is { Length: > 0 } proof)
        {
            Directory.CreateDirectory(proof);
            File.WriteAllText(Path.Combine(proof, $"minimum-{scene}.json"), JsonSerializer.Serialize(measurement));
        }
        return minimum;
    }

    internal static async Task SaveViewportsAsync(SetupWindow window, Page page, UIThreadFixture ui,
        ITestOutputHelper output, string name)
    {
        var root = Assert.IsType<Grid>(window.Content);
        var scroll = Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>());
        root.UpdateLayout();
        Assert.True(scroll.ViewportHeight >= 100, $"Scrollable content is a sliver: {scroll.ViewportHeight} DIPs");
        var offset = 0d;
        var index = 0;
        try
        {
            do
            {
                scroll.ChangeView(null, offset, null, disableAnimation: true);
                await TestSupport.WaitForRenderedConditionAsync(() => Math.Abs(scroll.VerticalOffset - offset) < 1, "minimum viewport");
                root.UpdateLayout();
                await ui.YieldToRenderAsync();
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
                    await OnboardingArtworkRenderingTests.SaveProofAsync(root, $"{name}-{++index:D2}", output);
                else
                    index++;
                if (offset >= scroll.ScrollableHeight) break;
                offset = Math.Min(offset + scroll.ViewportHeight, scroll.ScrollableHeight);
            } while (index < 16);
            Assert.True(offset >= scroll.ScrollableHeight, "Every minimum-size content viewport must remain reachable.");
        }
        finally
        {
            scroll.ChangeView(null, 0, null, disableAnimation: true);
            await ui.YieldToRenderAsync();
        }
    }

    [Fact]
    public async Task SettledCapabilityScroll_ReachesLastRowAfterExtentGrowth()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.RunOnUIAsync(async () =>
        {
            var target = new SettingsCard { Header = "Last capability", Height = 70 };
            var content = new StackPanel();
            content.Children.Add(new Border { Height = 400 });
            content.Children.Add(target);
            var scroll = new ScrollViewer { Content = content };
            var root = new Grid { Width = 600, Height = 170 };
            root.Children.Add(scroll);
            var window = OnboardingNativeProof.CreateWindow(() => new Window { Content = root });
            try
            {
                window.AppWindow.Resize(new(800, 500));
                window.Activate();
                window.AppWindow.Move(new(-32000, -32000));
                await TestSupport.WaitForRenderedConditionAsync(() => target.IsLoaded, "scroll regression loaded");
                root.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                var oldEnd = scroll.ScrollableHeight;
                target.Height += 15;
                root.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                Assert.True(scroll.ScrollableHeight >= oldEnd + 14);
                scroll.ChangeView(null, oldEnd, null, disableAnimation: true);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Math.Abs(scroll.VerticalOffset - oldEnd) < 1, "stale scroll extent");
                var clipped = target.TransformToVisual(scroll).TransformBounds(new(0, 0, target.ActualWidth, target.ActualHeight));
                Assert.True(clipped.Bottom > scroll.ViewportHeight + 14);

                await ScrollMeasuredTargetIntoViewportAsync(target, scroll, ui);

                Assert.True(scroll.VerticalOffset > oldEnd);
                OnboardingNativeProof.AssertFullyVisible(target, scroll);
            }
            finally
            {
                window.Close();
                await ui.YieldToRenderAsync();
            }
        });
    }

    private static async Task ScrollMeasuredTargetIntoViewportAsync(
        FrameworkElement target, ScrollViewer scroll, UIThreadFixture ui)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(scroll.XamlRoot.Content);
        root.UpdateLayout();
        var bounds = target.TransformToVisual(scroll).TransformBounds(new(0, 0, target.ActualWidth, target.ActualHeight));
        var offset = Math.Clamp(scroll.VerticalOffset + bounds.Top +
            (bounds.Height - scroll.ViewportHeight) / 2, 0, scroll.ScrollableHeight);
        scroll.ChangeView(null, offset, null, disableAnimation: true);
        await TestSupport.WaitForRenderedConditionAsync(
            () => Math.Abs(scroll.VerticalOffset - offset) < 1, "measured capability scroll");
        await ui.YieldToRenderAsync();
        await OnboardingNativeProof.NextCompositionAsync();
        root.UpdateLayout();
    }

    private static async Task SaveCapabilitiesAsync(SetupWindow window, CapabilitiesPage page, UIThreadFixture ui,
        ITestOutputHelper output, string name)
    {
        var root = Assert.IsType<Grid>(window.Content);
        var scroll = Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>());
        Assert.True(scroll.ViewportHeight >= 100);
        var profiles = Assert.IsType<ListView>(page.FindName("ProfileSelector"));
        var fineTune = Assert.IsType<SettingsExpander>(page.FindName("FineTuneExpander"));
        var cards = TestSupport.FindDescendants<SettingsCard>(page).ToArray();
        var targets = profiles.Items.Cast<FrameworkElement>()
            .Concat(fineTune.Items.Cast<FrameworkElement>())
            .Concat(new[] { "NodeModeToggle", "McpToggle", "OllamaToggle" }.Select(toggle =>
                Assert.Single(cards, card => ReferenceEquals(card.Content, page.FindName(toggle)))))
            .ToArray();
        Assert.Equal(14, targets.Length);
        var index = 0;
        foreach (var target in targets)
        {
            target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
            root.UpdateLayout();
            await ui.YieldToRenderAsync();
            await OnboardingNativeProof.NextCompositionAsync();
            root.UpdateLayout();
            // Responsive cards can grow the extent after BringIntoView used the old end offset.
            await ScrollMeasuredTargetIntoViewportAsync(target, scroll, ui);
            var bounds = target.TransformToVisual(scroll).TransformBounds(new(0, 0, target.ActualWidth, target.ActualHeight));
            output.WriteLine($"control={index}; bounds={bounds}; viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}; offset={scroll.VerticalOffset}; scrollable={scroll.ScrollableHeight}");
            OnboardingNativeProof.AssertFullyVisible(target, scroll);
            output.WriteLine($"reachable={index}; offset={scroll.VerticalOffset}; height={target.ActualHeight}; viewport={scroll.ViewportHeight}");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
                await OnboardingArtworkRenderingTests.SaveProofAsync(root, $"{name}-control-{index++:D2}", output);
            else
                index++;
        }
    }
}
