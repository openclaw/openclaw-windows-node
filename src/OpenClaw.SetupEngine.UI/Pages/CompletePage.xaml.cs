using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CompletePage : Page
{
    private const string StartupRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRunValue = "OpenClawTray";
    private static readonly Regex s_urlRegex = new(@"https?://[^\s)]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
                HelpLink.Visibility = Visibility.Collapsed;
            }
            else
            {
                var errorMessage = args.ErrorMessage ?? "Unknown error";
                var helpUrl = ExtractHelpUrl(errorMessage);

                SuccessIcon.Visibility = Visibility.Collapsed;
                FailureIcon.Visibility = Visibility.Visible;
                TitleText.Text = "Setup failed";
                SubtitleText.Text = helpUrl is null
                    ? args.ErrorMessage ?? "An error occurred during setup"
                    : "Follow the steps below to resolve the setup issue and retry.";
                NodeModeBanner.Visibility = Visibility.Collapsed;
                StartupRow.Visibility = Visibility.Collapsed;
                LaunchButton.Content = "Close";

                // Show error card with details and log link
                ErrorCard.Visibility = Visibility.Visible;
                ErrorText.Text = errorMessage;
                if (helpUrl != null)
                {
                    HelpLink.Content = errorMessage.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                        ? "Update WSL →"
                        : "Open help link →";
                    HelpLink.NavigateUri = helpUrl;
                    HelpLink.Visibility = Visibility.Visible;
                }
                else
                {
                    HelpLink.Visibility = Visibility.Collapsed;
                }
                if (args.LogPath != null)
                {
                    var displayPath = LogFileLauncher.ResolveRealPath(args.LogPath);
                    ViewLogLink.Content = $"View full log → {displayPath}";
                    ToolTipService.SetToolTip(ViewLogLink, displayPath);
                    ViewLogLink.Visibility = Visibility.Visible;
                }
                else
                    ViewLogLink.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static Uri? ExtractHelpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = s_urlRegex.Match(text);
        if (!match.Success)
            return null;

        return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? uri : null;
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
        LogFileLauncher.RevealInExplorer(_logPath);
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

        var trayPath = TrayExecutableResolver.Resolve();
        if (trayPath != null)
            Process.Start(new ProcessStartInfo(trayPath, "openclaw://chat") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("openclaw://chat") { UseShellExecute = true });
    }

    private static void RegisterStartup()
    {
        try
        {
            var trayPath = TrayExecutableResolver.Resolve();
            if (trayPath == null) return;

            if (StartupTaskRegistration.Register(trayPath))
            {
                DeleteLegacyStartupRunValue();
                return;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRunKey, writable: true);
            key?.SetValue(StartupRunValue, $"\"{Path.GetFullPath(trayPath)}\"");
        }
        catch { /* best effort */ }
    }

    private static void DeleteLegacyStartupRunValue()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRunKey, writable: true);
            key?.DeleteValue(StartupRunValue, throwOnMissingValue: false);
        }
        catch { /* best effort */ }
    }
}
