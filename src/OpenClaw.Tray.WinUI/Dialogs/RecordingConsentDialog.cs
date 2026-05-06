using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// Privacy consent dialog shown before the first screen or camera recording.
/// Parameterized by recording type so each capability gets its own consent.
/// </summary>
public sealed class RecordingConsentDialog : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private readonly TaskCompletionSource<bool> _tcs = new();
    private bool _consented;

    public RecordingConsentDialog(RecordingType type)
    {
        var isScreen = type == RecordingType.Screen;
        var headingKey = isScreen ? "RecordingConsent_ScreenTitle" : "RecordingConsent_CameraTitle";
        var descriptionKey = isScreen ? "RecordingConsent_ScreenDescription" : "RecordingConsent_CameraDescription";
        var emoji = isScreen ? "🖥️" : "📷";

        Title = LocalizationHelper.GetString("RecordingConsent_WindowTitle");
        this.SetWindowSize(460, 340);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        // Custom title bar
        var titleBar = new Grid
        {
            Height = 48,
            Padding = new Thickness(16, 0, 140, 0)
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new TextBlock
        {
            Text = "🦞",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(titleIcon, 0);
        titleBar.Children.Add(titleIcon);

        var titleText = new TextBlock
        {
            Text = LocalizationHelper.GetString("RecordingConsent_WindowTitle"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
        };
        Grid.SetColumn(titleText, 1);
        titleBar.Children.Add(titleText);

        SetTitleBar(titleBar);

        // Main layout
        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(titleBar, 0);
        outerGrid.Children.Add(titleBar);

        var root = new Grid
        {
            Padding = new Thickness(32, 16, 32, 32),
            RowSpacing = 16
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        header.Children.Add(new TextBlock { Text = emoji, FontSize = 36 });
        header.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString(headingKey),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Content
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString(descriptionKey),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("RecordingConsent_Privacy"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
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

        var denyButton = new Button
        {
            Content = LocalizationHelper.GetString("RecordingConsent_Deny")
        };
        denyButton.Click += (s, e) =>
        {
            Logger.Info($"[RecordingConsent] User denied {type} recording consent");
            _consented = false;
            Close();
        };
        buttonPanel.Children.Add(denyButton);

        var allowButton = new Button
        {
            Content = LocalizationHelper.GetString("RecordingConsent_Allow"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        allowButton.Click += (s, e) =>
        {
            Logger.Info($"[RecordingConsent] User allowed {type} recording consent");
            _consented = true;
            Close();
        };
        buttonPanel.Children.Add(allowButton);

        Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        Grid.SetRow(root, 1);
        outerGrid.Children.Add(root);

        Content = outerGrid;

        Closed += (s, e) => _tcs.TrySetResult(_consented);

        Logger.Info($"[RecordingConsent] {type} recording consent dialog shown");
    }

    public new Task<bool> ShowAsync()
    {
        Activate();

        // Force to foreground since this may be triggered from a background context
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                // Briefly set topmost to guarantee visibility, then remove topmost flag
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { /* best-effort */ }

        return _tcs.Task;
    }
}
