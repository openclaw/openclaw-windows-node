// Proves that an open Mica dialog follows the Companion appearance: Mica tinted with the Gateway theme, the
// opaque canvas when Mica is off, and untinted system Mica when the theme is cleared, and that switching themes
// raises no framework fault. With OPENCLAW_UI_PROOF_DIR set on an interactive desktop, it also saves a screen
// capture of each state for visual proof.
// Run with dotnet test tests\OpenClaw.Tray.UITests -r win-x64 --filter FullyQualifiedName~GatewayThemeBackdropTests.
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
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
    public Task OpenDialogFollowsMicaSettingAndGatewayThemeTint() => ui.RunOnUIAsync(async () =>
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

            BackdropPolicy.Configure(useMica: true, theme);
            if (highContrast)
                Assert.Same(original, dialog.SystemBackdrop);
            else
                Assert.True(Assert.IsType<ThemedMicaBackdrop>(dialog.SystemBackdrop).IsTinted);
            var themed = dialog.SystemBackdrop;
            await CaptureAsync(dialog, "pairing-dialog-tide-tinted-mica");

            BackdropPolicy.Configure(useMica: false, theme);
            Assert.Null(dialog.SystemBackdrop);
            Assert.NotNull(root.Background);
            Assert.NotSame(originalBackground, root.Background);
            await CaptureAsync(dialog, "pairing-dialog-tide-opaque");

            // Clearing the theme keeps the window's backdrop and drops its tint, rather than swapping backdrops.
            BackdropPolicy.Configure(useMica: true, null);
            Assert.Same(themed, dialog.SystemBackdrop);
            if (themed is ThemedMicaBackdrop untinted)
                Assert.False(untinted.IsTinted);
            Assert.Same(originalBackground, root.Background);
            await CaptureAsync(dialog, "pairing-dialog-system-mica");
        }
        finally
        {
            BackdropPolicy.Configure(useMica: true, null);
            dialog.Close();
        }
    });

    // Standalone dialogs are outside the main windows' theme refresh; their unstyled text must still take the theme's
    // UI font when it is applied and return to the Windows font when it is cleared.
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

            Assert.True(applicator.Apply(BuiltinGatewayThemes.All.First(entry => entry.Id == "manuscript")));
            BackdropPolicy.Configure(useMica: true, applicator.Current);
            Assert.NotEqual(windowsFont, ThemeFont());
            Assert.Equal(ThemeFont(), HostFont(root).Source);

            Assert.True(applicator.Apply(null));
            BackdropPolicy.Configure(useMica: true, null);
            Assert.Equal(windowsFont, HostFont(root).Source);
        }
        finally
        {
            applicator.Apply(null);
            BackdropPolicy.Configure(useMica: true, null);
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

    // Restoring the Windows look after a tinted theme detached ThemedMicaBackdrop and reattached a system backdrop;
    // App then refreshes theme resources by flipping the window's RequestedTheme. After the first such cycle, WinUI
    // raised ArgumentException("target") from a framework callback: an unhandled crash on every later restore. The
    // fault needs an active window on a host where Mica is supported and High Contrast is off.
    [Fact]
    public Task SwitchingBetweenTintedThemesAndTheWindowsLookRaisesNoFrameworkFault() => ui.RunOnUIAsync(async () =>
    {
        var crt = BuiltinGatewayThemes.All.First(entry => entry.Id == "crt");
        var manuscript = BuiltinGatewayThemes.All.First(entry => entry.Id == "manuscript");
        var faults = new List<Exception>();
        void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            faults.Add(args.Exception);
            args.Handled = true;
        }
        var window = new Microsoft.UI.Xaml.Window
        {
            Content = new Grid { Children = { new TextBlock { Text = "Backdrop proof" } } },
            SystemBackdrop = new MicaBackdrop(),
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
                BackdropPolicy.Configure(useMica: true, theme);
                ThemeHelper.ApplyTheme(window, "Dark", refreshThemeResources: true);
                await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(300));
            }
            // The framework raises the fault from a later callback, so let it settle before asserting.
            await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromSeconds(1));

            Assert.Empty(faults);
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported() && !new AccessibilitySettings().HighContrast)
                Assert.False(Assert.IsType<ThemedMicaBackdrop>(window.SystemBackdrop).IsTinted);
        }
        finally
        {
            BackdropPolicy.Configure(useMica: true, null);
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
    private static async Task ShowForProofAsync(PairingApprovalDialog dialog)
    {
        if (string.IsNullOrWhiteSpace(ProofDirectory))
            return;
        OnboardingNativeProof.ActivateOwned(dialog);
        await OnboardingNativeProof.NextCompositionAsync();
    }

    private static async Task CaptureAsync(PairingApprovalDialog dialog, string name)
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
