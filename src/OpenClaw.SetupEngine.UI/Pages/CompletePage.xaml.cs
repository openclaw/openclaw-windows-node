using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CompletePage : Page
{
    private string? _logPath;

    public CompletePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is CompletePageArgs args)
        {
            _logPath = args.LogPath;

            if (args.Success)
            {
                SuccessIcon.Visibility = Visibility.Visible;
                FailureIcon.Visibility = Visibility.Collapsed;
                TitleText.Text = "All set!";
                SubtitleText.Text = "OpenClaw is ready to go";
                ErrorCard.Visibility = Visibility.Collapsed;
            }
            else
            {
                SuccessIcon.Visibility = Visibility.Collapsed;
                FailureIcon.Visibility = Visibility.Visible;
                TitleText.Text = "Setup failed";
                SubtitleText.Text = args.ErrorMessage ?? "An error occurred during setup";
                NodeModeBanner.Visibility = Visibility.Collapsed;
                StartupRow.Visibility = Visibility.Collapsed;
                LaunchButton.Content = "Close";

                // Show error card with details and log link
                ErrorCard.Visibility = Visibility.Visible;
                ErrorText.Text = args.ErrorMessage ?? "Unknown error";
                if (args.LogPath != null)
                    ViewLogLink.Content = $"View full log → {Path.GetFileName(args.LogPath)}";
                else
                    ViewLogLink.Visibility = Visibility.Collapsed;
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
        if (StartupToggle.Visibility == Visibility.Visible && StartupToggle.IsOn)
            RegisterStartup();

        // Launch tray on success, just close on failure
        if (LaunchButton.Content?.ToString() != "Close")
            LaunchTray();
        App.MainWindow?.Close();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logPath != null && File.Exists(_logPath))
            Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
    }

    private static void LaunchTray()
    {
        // Kill any existing tray instances so fresh one picks up new gateway
        foreach (var proc in Process.GetProcessesByName("OpenClaw.Tray.WinUI"))
        {
            try { proc.Kill(); } catch { }
        }

        // Brief pause for process cleanup
        Thread.Sleep(1000);

        // Launch via protocol deep link — opens tray and navigates to chat
        Process.Start(new ProcessStartInfo("openclaw://chat") { UseShellExecute = true });
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
