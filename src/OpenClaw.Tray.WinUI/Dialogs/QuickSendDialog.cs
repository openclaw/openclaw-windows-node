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
    // Bug #3 (manual test 2026-05-05): resolve the live App._gatewayClient
    // on every Send via this provider instead of capturing a single instance
    // at construction time. This survives autopair / SSH-tunnel-restart /
    // manual-pair / onboarding-completion swaps under the dialog.
    private readonly Func<OpenClawGatewayClient?> _clientProvider;
    private readonly QuickSendCoordinator _coordinator;
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

    public QuickSendDialog(Func<OpenClawGatewayClient?> clientProvider, string? prefillMessage = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _coordinator = new QuickSendCoordinator(() =>
        {
            var live = _clientProvider();
            return live == null ? null : new OpenClawGatewayClientAdapter(live);
        });

        
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

        QuickSendOutcome outcome;
        try
        {
            outcome = await _coordinator.SendAsync(message);
        }
        catch (Exception ex)
        {
            // Coordinator catches/classifies all expected failures; this is
            // a defensive guard against unexpected programmer errors.
            Logger.Error($"Quick send coordinator threw: {ex.Message}");
            outcome = new QuickSendOutcome.Failed(ex.Message);
        }

        switch (outcome)
        {
            case QuickSendOutcome.Sent:
                Logger.Info($"[QuickSend] Message sent ({message.Length} chars)");
                new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("QuickSend_ToastTitle"))
                    .AddText(LocalizationHelper.GetString("QuickSend_ToastBody"))
                    .Show();
                Close();
                return;

            case QuickSendOutcome.GatewayInitializing init:
                // Bug #3: provider returned null (App is mid-swap). Do NOT
                // copy any pair-command remediation to clipboard — show a
                // simple "try again" message instead.
                Logger.Warn($"[QuickSend] {init.Message}");
                ShowErrorDetails(init.Message);
                break;

            case QuickSendOutcome.PairingRequired pr:
                // Genuine NOT_PAIRED on the live current client — clipboard
                // remediation MUST still fire (Mike explicitly does not want
                // this case suppressed; RubberDucky closure condition #3).
                CopyTextToClipboard(pr.Commands);
                ShowErrorDetails($"Pairing approval required\n\n{pr.Commands}");
                new ToastContentBuilder()
                    .AddText("Quick Send device approval required")
                    .AddText("Gateway reported pairing required. Approval guidance copied to clipboard.")
                    .Show();
                Logger.Warn($"[QuickSend] Pairing required. Commands copied to clipboard.\n{pr.Commands}");
                break;

            case QuickSendOutcome.MissingScope ms:
                CopyTextToClipboard(ms.Commands);
                ShowErrorDetails($"Missing scope: {ms.Scope}\n\n{ms.Commands}");
                new ToastContentBuilder()
                    .AddText("Quick Send permission required")
                    .AddText($"Missing scope '{ms.Scope}'. Identity + remediation guidance copied to clipboard.")
                    .Show();
                Logger.Warn($"[QuickSend] Missing scope '{ms.Scope}'. Commands copied to clipboard.\n{ms.Commands}");
                break;

            case QuickSendOutcome.Failed f:
                Logger.Error($"Quick send failed: {f.ErrorMessage}");
                ShowErrorDetails(f.ErrorMessage);
                break;
        }

        _sendButton.IsEnabled = true;
        _messageTextBox.IsEnabled = true;
        _isSending = false;
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
