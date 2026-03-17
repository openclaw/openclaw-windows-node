using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// Quick send dialog for sending messages to OpenClaw.
/// </summary>
public sealed class QuickSendDialog : WindowEx
{
    private readonly OpenClawGatewayClient _client;
    private readonly TextBox _messageTextBox;
    private readonly Button _sendButton;
    private readonly TextBlock _statusText;
    private bool _isSending;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int SW_SHOWNORMAL = 1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    public QuickSendDialog(OpenClawGatewayClient client, string? prefillMessage = null)
    {
        _client = client;
        
        // Window setup
        Title = LocalizationHelper.GetString("WindowTitle_QuickSend");
        this.SetWindowSize(400, 200);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));
        
        // Apply Acrylic via controller to keep IsInputActive=true.
        // This avoids focus/activation oddities on Windows 10 for hotkey-launched windows.
        BackdropHelper.TrySetAcrylicBackdrop((Microsoft.UI.Xaml.Window)this);

        // Hotkey-launched windows can fail to foreground on Windows 10 due to
        // foreground activation restrictions. Ensure the window is topmost.
        this.IsAlwaysOnTop = true;
        
        // Build UI programmatically (simple dialog)
        var root = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(24)
        };

        var header = new TextBlock
        {
            Text = LocalizationHelper.GetString("QuickSend_Header"),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        };
        root.Children.Add(header);

        _messageTextBox = new TextBox
        {
            PlaceholderText = LocalizationHelper.GetString("QuickSend_Placeholder"),
            AcceptsReturn = false,
            Text = prefillMessage ?? ""
        };
        _messageTextBox.KeyDown += OnKeyDown;
        root.Children.Add(_messageTextBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _statusText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        buttonPanel.Children.Add(_statusText);

        var cancelButton = new Button { Content = LocalizationHelper.GetString("QuickSend_CancelButton") };
        cancelButton.Click += (s, e) => Close();
        buttonPanel.Children.Add(cancelButton);

        _sendButton = new Button
        {
            Content = LocalizationHelper.GetString("QuickSend_SendButton"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        _sendButton.Click += OnSendClick;
        buttonPanel.Children.Add(_sendButton);

        root.Children.Add(buttonPanel);

        Content = root;

        // Focus the text box when shown
        Activated += (s, e) =>
        {
            _messageTextBox.Focus(FocusState.Programmatic);
            TryBringToFront();
        };
    }

    private void TryBringToFront()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            // Make sure it's actually shown and promoted.
            ShowWindow(hwnd, SW_SHOWNORMAL);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Logger.Warn($"QuickSend bring-to-front failed: {ex.Message}");
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter && !_isSending)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
        else if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            Close();
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        var message = _messageTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(message)) return;

        _isSending = true;
        _sendButton.IsEnabled = false;
        _messageTextBox.IsEnabled = false;
        _statusText.Text = LocalizationHelper.GetString("QuickSend_Sending");

        try
        {
            await _client.SendChatMessageAsync(message);
            Logger.Info($"Quick send: {message}");
            new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("QuickSend_ToastTitle"))
                .AddText(LocalizationHelper.GetString("QuickSend_ToastBody"))
                .Show();
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"Quick send failed: {ex.Message}");
            _statusText.Text = LocalizationHelper.GetString("QuickSend_Failed");
            _sendButton.IsEnabled = true;
            _messageTextBox.IsEnabled = true;
            _isSending = false;
        }
    }

    public new void ShowAsync()
    {
        Activate();
    }
}
