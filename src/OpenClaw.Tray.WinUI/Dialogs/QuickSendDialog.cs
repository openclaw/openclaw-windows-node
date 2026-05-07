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
using System.Text.RegularExpressions;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// Quick send dialog for sending messages to OpenClaw.
/// </summary>
public sealed class QuickSendDialog : WindowEx
{
    private readonly OpenClawGatewayClient _client;
    private readonly TextBox _messageTextBox;
    private readonly TextBox _errorDetailsTextBox;
    private readonly Button _sendButton;
    private bool _isSending;
    private bool _isClosed;
    private bool _focusRetryRunning;

    private const string TitleIcon = "🦞";
    private const double WindowControlsReservedWidth = 140;
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
    private const int TitleBarHeight = 48;
    private const int SW_SHOWNORMAL = 1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    public QuickSendDialog(OpenClawGatewayClient client, string? prefillMessage = null)
    {
        _client = client;
        
        // Window setup
        Title = LocalizationHelper.GetString("WindowTitle_QuickSend");
        ExtendsContentIntoTitleBar = true;
        this.SetWindowSize(420, 260 + TitleBarHeight);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));
        
        // Apply Acrylic via controller to keep IsInputActive=true.
        // This avoids focus/activation oddities on Windows 10 for hotkey-launched windows.
        BackdropHelper.TrySetAcrylicBackdrop((Microsoft.UI.Xaml.Window)this);

        // Hotkey-launched windows can fail to foreground on Windows 10 due to
        // foreground activation restrictions. Keep the existing topmost promotion.
        this.IsAlwaysOnTop = true;

        // Build UI programmatically (simple dialog)
        var root = new Grid
        {
            RowSpacing = 12
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = LocalizationHelper.GetString("QuickSend_Header"),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _messageTextBox = new TextBox
        {
            PlaceholderText = LocalizationHelper.GetString("QuickSend_Placeholder"),
            AcceptsReturn = false,
            Text = prefillMessage ?? ""
        };
        _messageTextBox.KeyDown += OnKeyDown;
        Grid.SetRow(_messageTextBox, 1);
        root.Children.Add(_messageTextBox);

        _errorDetailsTextBox = new TextBox
        {
            Visibility = Visibility.Collapsed,
            IsReadOnly = true,
            IsTabStop = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 240,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_errorDetailsTextBox, ScrollBarVisibility.Auto);
        Grid.SetRow(_errorDetailsTextBox, 2);
        root.Children.Add(_errorDetailsTextBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

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

        Grid.SetRow(buttonPanel, 3);
        root.Children.Add(buttonPanel);

        var body = new Border
        {
            Padding = new Thickness(24),
            Child = root
        };

        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Grid { Padding = new Thickness(16, 0, WindowControlsReservedWidth, 0) };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(new TextBlock
        {
            Text = TitleIcon,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("WindowTitle_QuickSend"),
            FontSize = 13,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        titleBar.Children.Add(titleStack);
        Grid.SetRow(titleBar, 0);
        outerGrid.Children.Add(titleBar);

        Grid.SetRow(body, 1);
        outerGrid.Children.Add(body);

        Content = outerGrid;
        SetTitleBar(titleBar);

        // Focus the text box when shown without closing on transient deactivation.
        Activated += (s, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                TryBringToFront();
                RequestInputFocus();
            }
        };

        Closed += (s, e) =>
        {
            _isClosed = true;
            Logger.Info("[QuickSend] Dialog closed");
        };

        Logger.Info($"[QuickSend] Dialog opened (prefill={!string.IsNullOrEmpty(prefillMessage)})");
    }

    private void TryBringToFront()
    {
        try
        {
            if (_isClosed)
                return;

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

        _errorDetailsTextBox.Visibility = Visibility.Collapsed;
        _errorDetailsTextBox.Text = string.Empty;
        this.SetWindowSize(420, 260 + TitleBarHeight);

        _isSending = true;
        _sendButton.IsEnabled = false;
        _messageTextBox.IsEnabled = false;
        ShowDetails(LocalizationHelper.GetString("QuickSend_Sending"));

        try
        {
            if (!await EnsureGatewayConnectedAsync())
            {
                throw new InvalidOperationException("Gateway connection is not open");
            }

            await _client.SendChatMessageAsync(message);
            Logger.Info($"[QuickSend] Message sent ({message.Length} chars)");
            new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("QuickSend_ToastTitle"))
                .AddText(LocalizationHelper.GetString("QuickSend_ToastBody"))
                .Show();
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"Quick send failed: {ex.Message}");
            if (IsPairingRequired(ex.Message))
            {
                var commands = _client.BuildPairingApprovalFixCommands();
                CopyTextToClipboard(commands);

                ShowErrorDetails($"Pairing approval required\n\n{commands}");
                new ToastContentBuilder()
                    .AddText("Quick Send device approval required")
                    .AddText("Gateway reported pairing required. Approval guidance copied to clipboard.")
                    .Show();
                Logger.Warn($"[QuickSend] Pairing required. Commands copied to clipboard.\n{commands}");
            }
            else if (TryExtractMissingScope(ex.Message, out var missingScope))
            {
                var commands = _client.BuildMissingScopeFixCommands(missingScope);
                CopyTextToClipboard(commands);

                ShowErrorDetails($"Missing scope: {missingScope}\n\n{commands}");
                new ToastContentBuilder()
                    .AddText("Quick Send permission required")
                    .AddText($"Missing scope '{missingScope}'. Identity + remediation guidance copied to clipboard.")
                    .Show();
                Logger.Warn($"[QuickSend] Missing scope '{missingScope}'. Commands copied to clipboard.\n{commands}");
            }
            else
            {
                ShowErrorDetails(ex.Message);
            }

            _sendButton.IsEnabled = true;
            _messageTextBox.IsEnabled = true;
            _isSending = false;
        }
    }

    private void ShowErrorDetails(string details)
    {
        _errorDetailsTextBox.Header = LocalizationHelper.GetString("QuickSend_Failed");
        _errorDetailsTextBox.MinHeight = 140;
        _errorDetailsTextBox.Text = details;
        _errorDetailsTextBox.Visibility = Visibility.Visible;
        this.SetWindowSize(520, 400 + TitleBarHeight);

        // Move focus to the details box so users can immediately select/copy text.
        _errorDetailsTextBox.Focus(FocusState.Programmatic);
    }

    private void ShowDetails(string details)
    {
        _errorDetailsTextBox.Header = null;
        _errorDetailsTextBox.MinHeight = 80;
        _errorDetailsTextBox.Text = details;
        _errorDetailsTextBox.Visibility = Visibility.Visible;
        this.SetWindowSize(500, 320 + TitleBarHeight);
    }

    private static bool TryExtractMissingScope(string? message, out string scope)
    {
        scope = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = Regex.Match(message, @"missing\s+scope\s*:\s*([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        scope = match.Groups[1].Value;
        return !string.IsNullOrWhiteSpace(scope);
    }

    private static bool IsPairingRequired(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("pairing required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not paired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("NOT_PAIRED", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyTextToClipboard(string text)
    {
        var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    private void QueueFocusMessageInput()
    {
        if (_isClosed)
            return;

        DispatcherQueue?.TryEnqueue(FocusMessageInput);
    }

    private void RequestInputFocus()
    {
        QueueFocusMessageInput();
        if (!_focusRetryRunning)
        {
            _focusRetryRunning = true;
            _ = RetryFocusMessageInputAsync();
        }
    }

    private async Task RetryFocusMessageInputAsync()
    {
        try
        {
            var delaysMs = new[] { 60, 160, 320 };
            foreach (var delay in delaysMs)
            {
                await Task.Delay(delay);
                if (_isClosed)
                    return;

                TryBringToFront();
                QueueFocusMessageInput();
            }
        }
        finally
        {
            _focusRetryRunning = false;
        }
    }

    private async Task<bool> EnsureGatewayConnectedAsync(int timeoutMs = 3000)
    {
        if (_client.IsConnectedToGateway)
        {
            return true;
        }

        try
        {
            await _client.ConnectAsync();
        }
        catch
        {
            // Connect errors are handled by the send flow.
        }

        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (_client.IsConnectedToGateway)
            {
                return true;
            }

            await Task.Delay(120);
        }

        return _client.IsConnectedToGateway;
    }

    public void FocusMessageInput()
    {
        _messageTextBox.Focus(FocusState.Programmatic);
        _messageTextBox.SelectionStart = _messageTextBox.Text?.Length ?? 0;
    }

    public new void ShowAsync()
    {
        Activate();
        RequestInputFocus();
    }
}
