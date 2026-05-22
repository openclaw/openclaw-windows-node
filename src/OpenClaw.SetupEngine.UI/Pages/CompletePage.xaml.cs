using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CompletePage : Page
{
    public CompletePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is CompletePageArgs args)
        {
            if (args.Success)
            {
                TitleText.Text = "All set!";
                SubtitleText.Text = "OpenClaw is ready to go";
            }
            else
            {
                TitleText.Text = "Setup failed";
                SubtitleText.Text = "Check the log for details";
                NodeModeBanner.Visibility = Visibility.Collapsed;
                LaunchButton.Content = "Close";
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Style the Node Mode banner with amber/brown background
        var isDark = ActualTheme == ElementTheme.Dark;
        NodeModeBanner.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x4A, 0x3D, 0x10) // dark amber
            : Color.FromArgb(255, 0xF5, 0xE6, 0xB8)); // light amber

        // Default startup toggle to off (user can enable)
        StartupToggle.IsOn = false;
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        // Register startup if toggled on
        if (StartupToggle.IsOn)
            RegisterStartup();

        // Launch tray and close
        LaunchTray();
        App.MainWindow?.Close();
    }

    private static void LaunchTray()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenClaw.Tray.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "OpenClaw.Tray.WinUI.exe"),
        };

        var trayPath = candidates.FirstOrDefault(File.Exists);
        if (trayPath != null)
            Process.Start(new ProcessStartInfo(trayPath) { UseShellExecute = true });
        else
        {
            try { Process.Start(new ProcessStartInfo("OpenClaw.Tray.WinUI.exe") { UseShellExecute = true }); }
            catch { /* best effort */ }
        }
    }

    private static void RegisterStartup()
    {
        try
        {
            // Find tray exe path for startup registration
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.exe"),
                Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.exe"),
            };
            var trayPath = candidates.FirstOrDefault(File.Exists);
            if (trayPath == null) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("OpenClawTray", $"\"{Path.GetFullPath(trayPath)}\"");
        }
        catch { /* best effort */ }
    }
}
