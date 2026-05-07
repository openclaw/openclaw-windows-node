using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// First-run welcome dialog for new users.
/// </summary>
public sealed class WelcomeDialog : WindowEx
{
    private readonly TaskCompletionSource<ContentDialogResult> _tcs = new();
    private ContentDialogResult _result = ContentDialogResult.None;

    public WelcomeDialog()
    {
        Title = LocalizationHelper.GetString("WindowTitle_Welcome");
        ExtendsContentIntoTitleBar = true;
        this.SetWindowSize(480, 440);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        
        // Apply Mica backdrop for modern Windows 11 look
        SystemBackdrop = new MicaBackdrop();
        
        // Build UI directly in the window (no ContentDialog needed)
        var root = new Grid
        {
            Padding = new Thickness(32),
            RowSpacing = 16
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Lobster header
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        headerPanel.Children.Add(new TextBlock
        {
            Text = "🦞",
            FontSize = 48
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Welcome_Title"),
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(headerPanel, 0);
        root.Children.Add(headerPanel);

        // Content
        var content = new StackPanel { Spacing = 16 };
        
        content.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Welcome_Description"),
            TextWrapping = TextWrapping.Wrap
        });

        var gettingStarted = new StackPanel { Spacing = 8 };
        gettingStarted.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Welcome_GettingStarted"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var bulletList = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 0, 0) };
        bulletList.Children.Add(new TextBlock { Text = LocalizationHelper.GetString("Welcome_NeedGateway") });
        bulletList.Children.Add(new TextBlock { Text = LocalizationHelper.GetString("Welcome_NeedToken") });
        gettingStarted.Children.Add(bulletList);
        content.Children.Add(gettingStarted);

        var docsButton = new HyperlinkButton
        {
            Content = LocalizationHelper.GetString("Welcome_ViewDocs"),
            NavigateUri = new Uri("https://docs.molt.bot/web/dashboard")
        };
        content.Children.Add(docsButton);

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var laterButton = new Button { Content = LocalizationHelper.GetString("Welcome_LaterButton") };
        laterButton.Click += (s, e) =>
        {
            Logger.Info("[Welcome] User clicked 'Later'");
            _result = ContentDialogResult.None;
            Close();
        };
        buttonPanel.Children.Add(laterButton);

        var settingsButton = new Button
        {
            Content = LocalizationHelper.GetString("Welcome_OpenSettingsButton"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        settingsButton.Click += (s, e) =>
        {
            Logger.Info("[Welcome] User clicked 'Open Settings'");
            _result = ContentDialogResult.Primary;
            Close();
        };
        buttonPanel.Children.Add(settingsButton);

        Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        // Wrap content with custom titlebar
        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Grid { Padding = new Thickness(16, 0, 140, 0) };
        var titleIcon = new TextBlock
        {
            Text = "🦞",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var titleTextBlock = new TextBlock
        {
            Text = LocalizationHelper.GetString("WindowTitle_Welcome"),
            FontSize = 13,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(titleIcon);
        titleStack.Children.Add(titleTextBlock);
        titleBar.Children.Add(titleStack);
        Grid.SetRow(titleBar, 0);
        outerGrid.Children.Add(titleBar);

        Grid.SetRow(root, 1);
        outerGrid.Children.Add(root);
        Content = outerGrid;
        SetTitleBar(titleBar);
        
        Closed += (s, e) => _tcs.TrySetResult(_result);

        Logger.Info("[Welcome] Welcome dialog shown");
    }

    public new Task<ContentDialogResult> ShowAsync()
    {
        Activate();
        return _tcs.Task;
    }
}
