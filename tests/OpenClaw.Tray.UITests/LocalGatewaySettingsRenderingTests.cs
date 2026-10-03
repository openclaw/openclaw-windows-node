using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClawTray.Pages;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class LocalGatewaySettingsRenderingTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private const string CustomDistro = "SettingsProofCustomGateway";
    private const string GenericSetup = "Install a native or WSL gateway, or configure an existing gateway. Existing installations are only replaced after confirmation.";
    private const string NativeSetup = "Configure the AI provider and model on the selected native gateway without reinstalling it.";
    private const string OnboardingDescription = "Configure the AI provider and model on the selected local gateway without reinstalling it.";
    private static readonly MethodInfo ApplySection = typeof(SettingsPage).GetMethod(
        "ApplyLocalGatewaySection", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(SettingsPage).FullName, "ApplyLocalGatewaySection");

    [Theory]
    [InlineData("native", false)]
    [InlineData("native", true)]
    [InlineData("legacy-native", false)]
    [InlineData("legacy-native", true)]
    [InlineData("custom-wsl", false)]
    [InlineData("custom-wsl", true)]
    [InlineData("manual-loopback", false)]
    [InlineData("manual-loopback", true)]
    [InlineData("none", false)]
    [InlineData("none", true)]
    public Task MountedGatewaySection_RendersOnlyControlsForItsOwner(string scenario, bool isPackaged) =>
        WithPageAsync(async page =>
        {
            ApplySection.Invoke(page, [Record(scenario), isPackaged]);
            await AssertAndCaptureAsync(page, scenario, isPackaged, scenario);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MountedGatewaySection_NativeWslNativeSwitchDoesNotKeepStaleCopyOrWarning(bool isPackaged) =>
        WithPageAsync(async page =>
        {
            var native = Record("native");
            ApplySection.Invoke(page, [native, isPackaged]);
            await AssertAndCaptureAsync(page, "native", isPackaged, "switch-1-native");
            var nativeBody = Find<TextBlock>(page, "GatewayBodyText").Text;

            ApplySection.Invoke(page, [Record("custom-wsl"), isPackaged]);
            await AssertAndCaptureAsync(page, "custom-wsl", isPackaged, "switch-2-wsl");
            Assert.NotEqual(nativeBody, Find<TextBlock>(page, "GatewayBodyText").Text);

            ApplySection.Invoke(page, [native, isPackaged]);
            await AssertAndCaptureAsync(page, "native", isPackaged, "switch-3-native");
            Assert.Equal(nativeBody, Find<TextBlock>(page, "GatewayBodyText").Text);
        });

    [Fact]
    public async Task MountedGatewaySection_IsolatesAndRestoresInheritedContainerSize()
    {
        var original = await ui.RunOnUIAsync(() =>
        {
            var dimensions = (ui.Container.Width, ui.Container.Height);
            ui.Container.Width = 600;
            ui.Container.Height = 400;
            return Task.FromResult(dimensions);
        });
        try
        {
            await WithPageAsync(async page =>
            {
                ApplySection.Invoke(page, [Record("native"), true]);
                await AssertAndCaptureAsync(page, "native", true, "inherited-container");
            });
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(600, ui.Container.Width);
                Assert.Equal(400, ui.Container.Height);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.Width = original.Width;
                ui.Container.Height = original.Height;
            });
        }
    }

    [Theory]
    [InlineData("native")]
    [InlineData("custom-wsl")]
    public Task MountedGatewaySection_FitsHostedRunnerViewport(string scenario) =>
        WithPageAsync(async page =>
        {
            ApplySection.Invoke(page, [Record(scenario), true]);
            await AssertAndCaptureAsync(page, scenario, true, "hosted-viewport-" + scenario);
        }, windowHeight: 800);

    private async Task WithPageAsync(Func<SettingsPage, Task> test, double windowHeight = 1100)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var position = ui.TestWindow.AppWindow.Position;
            var size = ui.TestWindow.AppWindow.Size;
            var containerWidth = ui.Container.Width;
            var containerHeight = ui.Container.Height;
            var scale = ui.Container.XamlRoot.RasterizationScale;
            var page = new SettingsPage
            {
                RequestedTheme = ElementTheme.Light,
            };
            var host = new Grid
            {
                Name = "SettingsProofHost",
                Width = 900,
                Height = 1000,
                Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            };
            host.Children.Add(page);
            try
            {
                ui.Container.Width = double.NaN;
                ui.Container.Height = double.NaN;
                // The fixture starts at 1x1. Give only its off-screen HWND a full viewport,
                // without activating it or capturing any desktop pixels.
                ui.TestWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    -32000, -32000, (int)Math.Ceiling(980 * scale), (int)Math.Ceiling(windowHeight * scale)));
                // Do not call Initialize: this mounted page has no live App, registry, or gateway.
                ui.Container.Children.Add(host);
                await TestSupport.WaitForRenderedConditionAsync(() => page.IsLoaded, "Settings page Loaded");
                await TestSupport.WaitForRenderedConditionAsync(
                    () => ui.Container.ActualWidth > 100 && ui.Container.ActualHeight > 100,
                    "resized fixture client area");
                // Hosted desktops can clamp the requested HWND size. Fit its actual client
                // area; the assertions below still require every gateway control to fit.
                var padding = ui.Container.Padding;
                host.Width = Math.Min(900, ui.Container.ActualWidth - padding.Left - padding.Right);
                host.Height = Math.Min(windowHeight - 100, ui.Container.ActualHeight - padding.Top - padding.Bottom);
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                output.WriteLine($"client={ui.Container.ActualWidth}x{ui.Container.ActualHeight}; " +
                    $"host={host.ActualWidth}x{host.ActualHeight}; scale={host.XamlRoot.RasterizationScale}");
                Assert.Same(ui.Container.XamlRoot, page.XamlRoot);
                AssertFullyVisible(host, ui.Container);
                AssertFullyVisible(page, host);
                await test(page);
            }
            finally
            {
                ui.Container.Children.Clear();
                ui.Container.Width = containerWidth;
                ui.Container.Height = containerHeight;
                ui.TestWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    position.X, position.Y, size.Width, size.Height));
                await ui.YieldToRenderAsync();
            }
        });
    }

    private async Task AssertAndCaptureAsync(SettingsPage page, string scenario, bool isPackaged, string capture)
    {
        var onboarding = Find<Border>(page, "OpenClawOnboardCard");
        var removal = Find<Border>(page, "LocalGatewayExpander");
        var body = Find<TextBlock>(page, "GatewayBodyText");
        var setup = Find<TextBlock>(page, "LocalGatewaySetupDescriptionText");
        var warning = Find<InfoBar>(page, "MsixWarningBar");
        var removeButton = Find<Button>(page, "RemoveGatewayButton");
        var setupButton = ByAutomationId<Button>(page, "OpenLocalGatewaySetupButton");
        var onboardingButton = ByAutomationId<Button>(page, "OpenClawOnboardButton");
        var managed = scenario is "native" or "legacy-native" or "custom-wsl";
        var canOnboard = scenario is "native" or "custom-wsl";

        Assert.Equal(canOnboard ? Visibility.Visible : Visibility.Collapsed, onboarding.Visibility);
        Assert.Equal(managed ? Visibility.Visible : Visibility.Collapsed, removal.Visibility);
        Assert.Equal(isPackaged && scenario == "custom-wsl", warning.IsOpen);
        Assert.False(Find<InfoBar>(page, "UninstallResultBar").IsOpen);
        Assert.Equal(scenario == "native" ? NativeSetup : GenericSetup, setup.Text);
        Assert.Equal("Open setup", setupButton.Content);
        Assert.True(setupButton.IsEnabled);
        Assert.Equal("Open onboarding", onboardingButton.Content);
        Assert.True(onboardingButton.IsEnabled);
        Assert.Equal("Remove Local Gateway", removeButton.Content);
        Assert.True(removeButton.IsEnabled);
        Assert.Equal(canOnboard, IsVisible(onboardingButton, page));
        Assert.Equal(managed, IsVisible(removeButton, page));

        if (canOnboard)
        {
            var text = TestSupport.FindDescendants<TextBlock>(onboarding).Select(block => block.Text).ToArray();
            Assert.Contains(OnboardingDescription, text);
            Assert.DoesNotContain(text, value => value.Contains("WSL", StringComparison.Ordinal));
        }

        switch (scenario)
        {
            case "native":
                Assert.StartsWith("Permanently removes this Windows package's isolated gateway account", body.Text);
                Assert.Contains("Other Companion profiles", body.Text);
                AssertNativePreservationCopy(body.Text);
                Assert.DoesNotContain("WSL", setup.Text);
                break;
            case "legacy-native":
                Assert.StartsWith("Stops this legacy native gateway", body.Text);
                AssertNativePreservationCopy(body.Text);
                break;
            case "custom-wsl":
                Assert.StartsWith($"Permanently removes the WSL gateway in {CustomDistro},", body.Text);
                Assert.DoesNotContain("OpenClawGateway", body.Text);
                Assert.Contains("disk image", body.Text);
                if (isPackaged)
                {
                    Assert.Equal("MSIX installation detected", warning.Title);
                    Assert.Contains("WSL distro, disk image", warning.Message);
                }
                break;
            default:
                Assert.Empty(body.Text);
                break;
        }

        page.UpdateLayout();
        await ui.YieldToRenderAsync();
        var scroll = Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>());
        var header = Find<TextBlock>(page, "LocalGatewaySectionHeader");
        var headerY = header.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
        scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset + headerY - 16), null, disableAnimation: true);
        await ui.YieldToRenderAsync();
        page.UpdateLayout();
        AssertFullyVisible(header, scroll);
        AssertFullyVisible(setup, scroll);
        AssertFullyVisible(setupButton, scroll);
        if (canOnboard)
        {
            AssertFullyVisible(onboarding, scroll);
            AssertFullyVisible(onboardingButton, scroll);
        }
        if (managed)
        {
            AssertFullyVisible(removal, scroll);
            AssertFullyVisible(body, scroll);
            AssertFullyVisible(removeButton, scroll);
        }
        if (warning.IsOpen)
            AssertFullyVisible(warning, scroll);

        var visibleText = TestSupport.FindDescendants<TextBlock>(page)
            .Where(block => IsVisible(block, page) && block.ActualWidth > 0 && block.ActualHeight > 0)
            .Where(block => IsInside(block, scroll))
            .Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        Assert.Contains(setup.Text, visibleText);
        Assert.DoesNotContain(visibleText, text => text.StartsWith("SettingsPage_", StringComparison.Ordinal));
        if (managed) Assert.Contains(body.Text, visibleText);
        if (scenario == "native")
            Assert.All(visibleText.Where(text => text.Contains("WSL", StringComparison.Ordinal)),
                text => Assert.Equal(body.Text, text));
        await CaptureAsync(page, $"{capture}-{(isPackaged ? "packaged" : "unpackaged")}", visibleText);
    }

    private static void AssertNativePreservationCopy(string text)
    {
        Assert.Contains("The Gateway Store app, WSL gateways, other saved gateways, Companion settings, and MCP token are preserved.", text);
        Assert.DoesNotContain("WSL", text[..text.IndexOf("The Gateway Store app", StringComparison.Ordinal)]);
        Assert.DoesNotContain("disk image", text);
    }

    private async Task CaptureAsync(SettingsPage page, string name, string[] visibleText)
    {
        var bitmap = new RenderTargetBitmap();
        var host = Assert.IsType<Grid>(VisualTreeHelper.GetParent(page));
        await bitmap.RenderAsync(host);
        var scale = host.XamlRoot.RasterizationScale;
        Assert.InRange(bitmap.PixelWidth, (int)Math.Floor(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualWidth * scale));
        Assert.InRange(bitmap.PixelHeight, (int)Math.Floor(host.ActualHeight * scale), (int)Math.Ceiling(host.ActualHeight * scale));
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var darkPixels = 0;
        var lightPixels = 0;
        var opaquePixels = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            if (pixels[i + 3] == 255) opaquePixels++;
            if (pixels[i] < 120 && pixels[i + 1] < 120 && pixels[i + 2] < 120) darkPixels++;
            if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200) lightPixels++;
        }
        Assert.True(opaquePixels >= pixels.Length / 4 * 0.99,
            $"The mounted page background is clipped or transparent ({opaquePixels}/{pixels.Length / 4} opaque pixels).");
        Assert.True(darkPixels > 500 && lightPixels > 10000, "The mounted page capture has no legible foreground/background.");
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_UI_TEST_ARTIFACTS_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync(checked((uint)stream.Size));
        var png = new byte[checked((int)stream.Size)];
        reader.ReadBytes(png);
        var path = Path.Combine(directory, $"settings-gateway-{name}.png");
        await File.WriteAllBytesAsync(path, png);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            capture = "RenderTargetBitmap of owned SettingsPage host, not desktop capture",
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            sha256 = hash,
            visibleText,
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"proof={path}; sha256={hash}");
    }

    private static T Find<T>(SettingsPage page, string name) where T : FrameworkElement =>
        Assert.IsType<T>(page.FindName(name));

    private static T ByAutomationId<T>(SettingsPage page, string id) where T : FrameworkElement =>
        Assert.Single(TestSupport.FindDescendants<T>(page), element => AutomationProperties.GetAutomationId(element) == id);

    private static bool IsVisible(FrameworkElement element, SettingsPage page)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: 0 }) return false;
            if (ReferenceEquals(current, page)) return true;
        }
        return false;
    }

    private static bool IsInside(FrameworkElement element, FrameworkElement viewport)
    {
        var bounds = element.TransformToVisual(viewport).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 &&
            bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }

    private static void AssertFullyVisible(FrameworkElement element, FrameworkElement viewport)
    {
        Assert.True(element.IsLoaded && element.ActualWidth > 0 && element.ActualHeight > 0,
            $"{element.Name} has no mounted layout.");
        Assert.True(IsInside(element, viewport),
            $"{element.GetType().Name} '{element.Name}' ({element.ActualWidth}x{element.ActualHeight}) " +
            $"is cropped by the Settings viewport ({viewport.ActualWidth}x{viewport.ActualHeight}).");
    }

    private static GatewayRecord? Record(string scenario) => scenario switch
    {
        "native" or "legacy-native" => new GatewayRecord
        {
            Id = scenario, FriendlyName = "Settings native proof", Url = "ws://127.0.0.1:18789", IsLocal = true,
            NativePackageFamilyName = "OpenClaw.Gateway_123456789abcd",
            NativeRuntimeContract = scenario == "native" ? NativeGatewayPackageClient.IsolatedContract : null,
        },
        "custom-wsl" => new GatewayRecord
        {
            Id = "wsl", FriendlyName = "Settings WSL proof", Url = "ws://127.0.0.1:18789", IsLocal = true,
            SetupManagedDistroName = CustomDistro,
        },
        "manual-loopback" => new GatewayRecord
        {
            Id = "manual", FriendlyName = "Manual loopback", Url = "ws://localhost:18789", IsLocal = true,
        },
        "none" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}
