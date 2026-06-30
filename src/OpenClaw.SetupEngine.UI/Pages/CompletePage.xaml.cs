using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CompletePage : Page
{
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
                SummaryPanel.Visibility = Visibility.Visible;
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
                SummaryPanel.Visibility = Visibility.Collapsed;
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
        // Default "launch at startup" to on — most users want OpenClaw ready after reboot.
        StartupToggle.IsOn = true;
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchButton.Content?.ToString() != "Close")
        {
            var enableAutoStart = StartupToggle.Visibility == Visibility.Visible && StartupToggle.IsOn;
            if (SetupWindow.Active?.RequestSetupCompleted(enableAutoStart) == true)
                return;
        }

        SetupWindow.Active?.Close();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_logPath);
    }

}
