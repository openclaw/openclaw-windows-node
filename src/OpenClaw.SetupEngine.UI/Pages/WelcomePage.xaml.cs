using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using System.Diagnostics;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WelcomePage : Page
{
    private SetupConfig? _config;

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var isDark = ActualTheme == ElementTheme.Dark;
        InfoCard.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x2C, 0x2C, 0x2C)
            : Color.FromArgb(255, 0xF0, 0xF0, 0xF0));

        InfoText.Text = "This local setup installs a small WSL Linux instance dedicated to OpenClaw. "
                      + "If you'd rather connect to an existing or remote gateway, choose Advanced setup.";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var dataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");

        var existing = ExistingConfigDetector.Detect(dataDir, _config!.DistroName);
        var summary = ExistingConfigDetector.BuildReplacementSummary(existing);

        var dialog = new ContentDialog
        {
            Title = existing.HasLocalGateway || existing.HasDistro
                ? "Replace existing WSL gateway?"
                : "Install a new WSL gateway?",
            Content = summary,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            App.MainWindow?.NavigateToCapabilities();
    }

    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        // Launch tray app navigated to connection settings
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenClawTray", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenClaw.Tray.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "OpenClaw.Tray.WinUI.exe"),
        };

        var trayPath = candidates.FirstOrDefault(File.Exists);
        var args = "--page connection";

        if (trayPath != null)
            Process.Start(new ProcessStartInfo(trayPath, args) { UseShellExecute = true });
        else
        {
            try { Process.Start(new ProcessStartInfo("OpenClaw.Tray.WinUI.exe", args) { UseShellExecute = true }); }
            catch { /* best effort */ }
        }

        App.MainWindow?.Close();
    }
}
