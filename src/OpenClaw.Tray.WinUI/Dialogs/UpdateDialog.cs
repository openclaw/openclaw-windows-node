using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using System;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Dialogs;

public enum UpdateDialogResult
{
    Download,
    Skip,
    RemindLater
}

/// <summary>
/// Dialog showing available update with release notes.
/// Built directly in a WindowEx (no ContentDialog/XamlRoot issues).
/// </summary>
public sealed class UpdateDialog : WindowEx
{
    private readonly TaskCompletionSource<UpdateDialogResult> _tcs = new();
    private UpdateDialogResult _result = UpdateDialogResult.RemindLater;

    public UpdateDialog(string version, string changelog)
    {
        Title = LocalizationHelper.GetString("WindowTitle_Update");
        this.SetWindowSize(560, 420);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();

        var root = new Grid
        {
            Padding = new Thickness(32),
            RowSpacing = 16
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new TextBlock
        {
            Text = string.Format(LocalizationHelper.GetString("Update_VersionAvailable"), version),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Content
        var content = new StackPanel { Spacing = 12 };

        var currentVersion = typeof(UpdateDialog).Assembly.GetName().Version?.ToString() ?? "Unknown";
        content.Children.Add(new TextBlock
        {
            Text = string.Format(LocalizationHelper.GetString("Update_CurrentVersion"), currentVersion),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        content.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Update_WhatsNew"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 200,
            Content = new TextBlock
            {
                Text = changelog,
                TextWrapping = TextWrapping.Wrap
            }
        });

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var skipButton = new Button { Content = LocalizationHelper.GetString("Update_SkipButton") };
        skipButton.Click += (s, e) => { _result = UpdateDialogResult.Skip; Close(); };
        buttonPanel.Children.Add(skipButton);

        var laterButton = new Button { Content = LocalizationHelper.GetString("Update_RemindLaterButton") };
        laterButton.Click += (s, e) => { _result = UpdateDialogResult.RemindLater; Close(); };
        buttonPanel.Children.Add(laterButton);

        var downloadButton = new Button
        {
            Content = LocalizationHelper.GetString("Update_DownloadButton"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        downloadButton.Click += (s, e) => { _result = UpdateDialogResult.Download; Close(); };
        buttonPanel.Children.Add(downloadButton);

        Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        Content = root;
        Closed += (s, e) => _tcs.TrySetResult(_result);
    }

    public new Task<UpdateDialogResult> ShowAsync()
    {
        Activate();
        return _tcs.Task;
    }
}
