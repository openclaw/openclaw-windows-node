using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CompletePage : Page
{
    private string? _logPath;

    public CompletePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is CompletePageArgs args)
        {
            _logPath = args.LogPath;

            if (args.Success)
            {
                ResultImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/Setup/PartyPopper.png"));
                TitleText.Text = "All set!";
                SubtitleText.Text = "OpenClaw is ready to go";
            }
            else
            {
                TitleText.Text = "Setup failed";
                SubtitleText.Text = "Check the log for details";
                LaunchButton.Content = "Close";
                ResultImage.Visibility = Visibility.Collapsed;
            }

            ElapsedText.Text = $"Completed in {args.Elapsed.TotalSeconds:F0}s";
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        // Find the tray exe — check common locations
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.exe"),
            // Dev build: sibling project output
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenClaw.Tray.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "OpenClaw.Tray.WinUI.exe"),
        };

        var trayPath = candidates.FirstOrDefault(File.Exists);
        if (trayPath != null)
        {
            Process.Start(new ProcessStartInfo(trayPath) { UseShellExecute = true });
        }
        else
        {
            // Fallback: try to start by name (if installed/in PATH)
            try { Process.Start(new ProcessStartInfo("OpenClaw.Tray.WinUI.exe") { UseShellExecute = true }); }
            catch { /* best effort */ }
        }

        App.MainWindow?.Close();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logPath != null && File.Exists(_logPath))
        {
            Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
        }
    }
}
