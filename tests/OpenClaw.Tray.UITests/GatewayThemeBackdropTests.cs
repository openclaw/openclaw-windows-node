// Proves that an open Mica dialog follows the Companion appearance live: system Mica for Windows default and the
// opaque canvas under any Gateway theme, without swapping the window's own backdrop, and that switching themes raises
// no framework fault. With OPENCLAW_UI_PROOF_DIR set on an interactive desktop, it also saves a screen capture of each
// state for visual proof.
// Run with dotnet test tests\OpenClaw.Tray.UITests -r win-x64 --filter FullyQualifiedName~GatewayThemeBackdropTests.
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Themes;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Theming;
using Windows.UI.ViewManagement;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayThemeBackdropTests(UIThreadFixture ui)
{
    [Fact]
    public Task OpenDialogFollowsTheGatewayTheme() => ui.RunOnUIAsync(async () =>
    {
        var theme = BuiltinGatewayThemes.All.First(entry => entry.Id == "tide");
        var highContrast = new AccessibilitySettings().HighContrast;
        var dialog = new PairingApprovalDialog(SyntheticCoordinator());
        try
        {
            var root = Assert.IsAssignableFrom<Panel>(dialog.Content);
            var original = Assert.IsType<MicaBackdrop>(dialog.SystemBackdrop);
            var originalBackground = root.Background;
            await ShowForProofAsync(dialog);

            BackdropPolicy.Configure(null);
            Assert.Same(originalBackground, root.Background);
            await CaptureAsync(dialog, "pairing-dialog-windows-default");

            BackdropPolicy.Configure(theme);
            if (highContrast)
                Assert.Same(originalBackground, root.Background);
            else
                AssertOpaque(root.Background);
            await CaptureAsync(dialog, "pairing-dialog-tide");

            BackdropPolicy.Configure(null);
            Assert.Same(originalBackground, root.Background);
            Assert.Same(original, dialog.SystemBackdrop);
        }
        finally
        {
            BackdropPolicy.Configure(null);
            dialog.Close();
        }
    });

    private static void AssertOpaque(Microsoft.UI.Xaml.Media.Brush? background) =>
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(background).Color.A);
    // Standalone dialogs are outside the main windows' theme refresh; their unstyled and styled text (the dialog's
    // Subtitle heading) must still take the theme's UI font when it is applied and return to the Windows font when
    // it is cleared.
    [Fact]
    public Task OpenDialogTakesTheThemeUiFont() => ui.RunOnUIAsync(async () =>
    {
        using var resources = new ChatThemeProofScope("Dark");
        var applicator = new AppThemeApplicator();
        var dialog = new PairingApprovalDialog(SyntheticCoordinator());
        string ThemeFont() => ((Microsoft.UI.Xaml.Media.FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["ContentControlThemeFontFamily"]).Source;
        try
        {
            var windowsFont = ThemeFont();
            dialog.Activate();
            var root = Assert.IsAssignableFrom<Panel>(dialog.Content);
            await TestSupport.WaitForRenderedConditionAsync(() => root.IsLoaded, "Dialog content loaded");

            var heading = TestSupport.FindDescendants<TextBlock>(root)
                .First(text => ReferenceEquals(text.Style, Microsoft.UI.Xaml.Application.Current.Resources["SubtitleTextBlockStyle"]));
            Assert.Equal(windowsFont, heading.FontFamily.Source);

            Assert.True(applicator.Apply(BuiltinGatewayThemes.All.First(entry => entry.Id == "manuscript")));
            BackdropPolicy.Configure(applicator.Current);
            BackdropPolicy.ConfigureWindowTheme("Dark", refreshResources: true);
            Assert.NotEqual(windowsFont, ThemeFont());
            Assert.Equal(ThemeFont(), HostFont(root).Source);
            Assert.Equal(ThemeFont(), heading.FontFamily.Source);

            Assert.True(applicator.Apply(null));
            BackdropPolicy.Configure(null);
            BackdropPolicy.ConfigureWindowTheme("Dark", refreshResources: true);
            Assert.Equal(windowsFont, HostFont(root).Source);
            Assert.Equal(windowsFont, heading.FontFamily.Source);
        }
        finally
        {
            applicator.Apply(null);
            BackdropPolicy.Configure(null);
            BackdropPolicy.ConfigureWindowTheme(null, refreshResources: false);
            dialog.Close();
        }
    });

    // A standalone dialog takes the light/dark preference App gives it. App keeps dialogs in the app's own mode, because
    // they resolve some brushes in code from that mode, except under a one-palette theme, which pins its mode and gives
    // both modes its only palette: a dark-only theme on light Windows shows its dark card with readable text. With
    // OPENCLAW_UI_PROOF_DIR set it saves the dialog under that theme.
    [Fact]
    public Task OpenDialogTakesAOnePaletteThemesPinnedModeWithItsOwnColors() => ui.RunOnUIAsync(async () =>
    {
        using var resources = new ChatThemeProofScope("Dark");
        var applicator = new AppThemeApplicator();
        var dialog = new PairingApprovalDialog(SyntheticCoordinator());
        try
        {
            dialog.Activate();
            var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
            await TestSupport.WaitForRenderedConditionAsync(() => root.IsLoaded, "Dialog content loaded");
            await ShowForProofAsync(dialog);

            foreach (var (preference, expected) in new[] { ("Dark", ElementTheme.Dark), ("Light", ElementTheme.Light), ("System", ElementTheme.Default) })
            {
                BackdropPolicy.ConfigureWindowTheme(preference, refreshResources: false);
                Assert.Equal(expected, root.RequestedTheme);
            }

            using var definition = System.Text.Json.JsonDocument.Parse("""
                { "name": "Ink", "dark": {
                  "background": "#05010a", "foreground": "#f5e9ff", "card": "#120320", "card-foreground": "#f5e9ff",
                  "popover": "#120320", "popover-foreground": "#f5e9ff", "primary": "#ff2bd6", "primary-foreground": "#05010a",
                  "secondary": "#1d0a33", "secondary-foreground": "#f5e9ff", "muted": "#1d0a33", "muted-foreground": "#b9a3d1",
                  "accent": "#28f0ff", "accent-foreground": "#05010a", "destructive": "#ff4d6d", "destructive-foreground": "#05010a",
                  "border": "#2c1446", "input": "#2c1446", "ring": "#28f0ff" } }
                """);
            Assert.True(GatewayThemeProtocol.TryParseDefinition(definition.RootElement, out var parsed, out _));
            var ink = ThemePaletteExpander.Resolve(
                new GatewayThemeDescriptor("acme/ink", "Ink", "", GatewayThemeSource.Plugin, [ThemeColorMode.Dark], "acme"), parsed);
            Assert.Equal(ThemeColorMode.Dark, ink.PinnedMode);

            Assert.True(applicator.Apply(ink, ThemeColorMode.Dark));
            BackdropPolicy.Configure(applicator.Current);
            BackdropPolicy.ConfigureWindowTheme("Dark", refreshResources: true);
            Assert.Equal(ElementTheme.Dark, root.ActualTheme);
            var card = TestSupport.FindDescendants<Border>(root).First(border =>
                ReferenceEquals(border.Background, Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]));
            var cardColor = Assert.IsType<SolidColorBrush>(card.Background).Color;
            var themeCard = ThemeResourceProjection.Project(ink.Dark!).Brushes["CardBackgroundFillColorDefaultBrush"];
            Assert.Equal(Windows.UI.Color.FromArgb(themeCard.A, themeCard.R, themeCard.G, themeCard.B), cardColor);
            var details = TestSupport.FindDescendants<TextBlock>(card).Select(text => text.Foreground).OfType<SolidColorBrush>().First();
            Assert.True(ThemeLegibility.Contrast(
                new ThemeColor(details.Color.A, details.Color.R, details.Color.G, details.Color.B),
                new ThemeColor(cardColor.A, cardColor.R, cardColor.G, cardColor.B)) >= ThemeLegibility.MinimumContrast);
            await CaptureAsync(dialog, "pairing-dialog-dark-only-theme");
        }
        finally
        {
            applicator.Apply(null);
            BackdropPolicy.Configure(null);
            BackdropPolicy.ConfigureWindowTheme(null, refreshResources: false);
            dialog.Close();
        }
    });
    // The framework host above the content panel carries the window's font; panels cannot.
    private static Microsoft.UI.Xaml.Media.FontFamily HostFont(Panel root)
    {
        for (var parent = VisualTreeHelper.GetParent(root); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Control control)
                return control.FontFamily;
            if (parent is ContentPresenter presenter)
                return presenter.FontFamily;
        }
        throw new InvalidOperationException("No font host above the dialog content.");
    }

    // Swapping a window's backdrop while App refreshes theme resources by flipping the window's RequestedTheme made
    // WinUI raise ArgumentException("target") from a framework callback: an unhandled crash on every later restore.
    // Theme changes therefore keep the window's own backdrop. The fault needs an active window on a host
    // where Mica is supported and High Contrast is off.
    [Fact]
    public Task SwitchingThemesKeepsTheWindowsBackdropAndRaisesNoFrameworkFault() => ui.RunOnUIAsync(async () =>
    {
        var crt = BuiltinGatewayThemes.All.First(entry => entry.Id == "crt");
        var manuscript = BuiltinGatewayThemes.All.First(entry => entry.Id == "manuscript");
        var faults = new List<Exception>();
        void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            faults.Add(args.Exception);
            args.Handled = true;
        }
        var backdrop = new MicaBackdrop();
        var window = new Microsoft.UI.Xaml.Window
        {
            Content = new Grid { Children = { new TextBlock { Text = "Backdrop proof" } } },
            SystemBackdrop = backdrop,
        };
        window.AppWindow.Resize(new(640, 480));
        Microsoft.UI.Xaml.Application.Current.UnhandledException += OnUnhandled;
        try
        {
            window.Activate();
            await OnboardingNativeProof.NextCompositionAsync();
            BackdropPolicy.Apply(window);
            ResolvedTheme?[] cycle = [crt, null, manuscript, null, crt, manuscript, null];
            foreach (var theme in cycle.Concat(cycle))
            {
                BackdropPolicy.Configure(theme);
                ThemeHelper.ApplyTheme(window, "Dark", refreshThemeResources: true);
                await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(300));
            }
            // The framework raises the fault from a later callback, so let it settle before asserting.
            await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromSeconds(1));

            Assert.Empty(faults);
            Assert.Same(backdrop, window.SystemBackdrop);
        }
        finally
        {
            BackdropPolicy.Configure(null);
            window.Close();
            Microsoft.UI.Xaml.Application.Current.UnhandledException -= OnUnhandled;
        }
    });

    private static PairingApprovalCoordinator SyntheticCoordinator()
    {
        var coordinator = new PairingApprovalCoordinator(() => null, () => [], () => false, NullLogger.Instance);
        var queue = (PairingApprovalQueue)typeof(PairingApprovalCoordinator)
            .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        queue.Reconcile(new DevicePairingListInfo
        {
            Pending = [new DevicePairingRequest
            {
                RequestId = "synthetic-theme-request", DeviceId = "synthetic-theme-device",
                DisplayName = "Theme Proof Laptop", Platform = "Windows", Role = "operator",
                Scopes = ["operator.read"],
            }],
        }, null, [], Environment.TickCount64);
        return coordinator;
    }

    private static string? ProofDirectory => Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR");

    // Mica renders only in a shown, active window, so the visual pass needs an interactive desktop.
    private static async Task ShowForProofAsync(Window dialog)
    {
        if (string.IsNullOrWhiteSpace(ProofDirectory))
            return;
        OnboardingNativeProof.ActivateOwned(dialog);
        await OnboardingNativeProof.NextCompositionAsync();
    }

    private static async Task CaptureAsync(Window dialog, string name)
    {
        if (ProofDirectory is not { Length: > 0 } directory)
            return;
        await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromSeconds(1));
        var position = dialog.AppWindow.Position;
        var size = dialog.AppWindow.Size;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(position.X, position.Y, 0, 0, new Size(size.Width, size.Height));
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
    }
}
