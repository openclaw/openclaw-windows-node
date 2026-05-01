using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class SettingsWindow : WindowEx
{
    private readonly SettingsManager _settings;
    // Delegate (not captured instance) so the window always reads the current
    // node service — the tray disposes & recreates NodeService in
    // App.OnSettingsSaved, and the previous reference would otherwise go stale
    // and show "Stopped" / "Failed to start" forever.
    private readonly Func<NodeService?> _nodeServiceProvider;
    private string _manualGatewayUrl = "";
    public bool IsClosed { get; private set; }

    public event EventHandler? SettingsSaved;
    public event EventHandler? CommandCenterRequested;

    public SettingsWindow(SettingsManager settings, NodeService? nodeService = null)
        : this(settings, nodeService != null ? () => nodeService : (Func<NodeService?>)(() => null))
    {
    }

    public SettingsWindow(SettingsManager settings, Func<NodeService?> nodeServiceProvider)
    {
        _settings = settings;
        _nodeServiceProvider = nodeServiceProvider;
        InitializeComponent();
        VisualTestCapture.CaptureOnLoaded(RootGrid, "Settings");
        
        Title = LocalizationHelper.GetString("WindowTitle_Settings");
        
        // Window configuration
        this.SetWindowSize(480, 700);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));
        
        LoadSettings();
        
        Closed += (s, e) => IsClosed = true;
        
        Logger.Info("[Settings] Window opened");
    }

    private void LoadSettings()
    {
        UseSshTunnelToggle.IsOn = _settings.UseSshTunnel;
        SshTunnelUserTextBox.Text = _settings.SshTunnelUser;
        SshTunnelHostTextBox.Text = _settings.SshTunnelHost;
        SshTunnelRemotePortTextBox.Text = _settings.SshTunnelRemotePort.ToString();
        SshTunnelLocalPortTextBox.Text = _settings.SshTunnelLocalPort.ToString();
        _manualGatewayUrl = _settings.GatewayUrl;
        GatewayUrlTextBox.Text = _settings.GatewayUrl;
        UpdateSshTunnelUiState();
        UpdateDetectedTopologyText();
        TokenTextBox.Text = _settings.Token;
        AutoStartToggle.IsOn = _settings.AutoStart;
        GlobalHotkeyToggle.IsOn = _settings.GlobalHotkeyEnabled;
        NotificationsToggle.IsOn = _settings.ShowNotifications;
        
        // Set sound combo — match by Tag (stable persistence key), not Content (display text)
        for (int i = 0; i < NotificationSoundComboBox.Items.Count; i++)
        {
            if (NotificationSoundComboBox.Items[i] is Microsoft.UI.Xaml.Controls.ComboBoxItem item &&
                item.Tag?.ToString() == _settings.NotificationSound)
            {
                NotificationSoundComboBox.SelectedIndex = i;
                break;
            }
        }
        if (NotificationSoundComboBox.SelectedIndex < 0)
            NotificationSoundComboBox.SelectedIndex = 0;

        // Notification filters
        NotifyHealthCb.IsChecked = _settings.NotifyHealth;
        NotifyUrgentCb.IsChecked = _settings.NotifyUrgent;
        NotifyReminderCb.IsChecked = _settings.NotifyReminder;
        NotifyEmailCb.IsChecked = _settings.NotifyEmail;
        NotifyCalendarCb.IsChecked = _settings.NotifyCalendar;
        NotifyBuildCb.IsChecked = _settings.NotifyBuild;
        NotifyStockCb.IsChecked = _settings.NotifyStock;
        NotifyInfoCb.IsChecked = _settings.NotifyInfo;
        
        // Advanced
        NodeModeToggle.IsOn = _settings.EnableNodeMode;
        NodeCanvasToggle.IsOn = _settings.NodeCanvasEnabled;
        NodeScreenToggle.IsOn = _settings.NodeScreenEnabled;
        NodeCameraToggle.IsOn = _settings.NodeCameraEnabled;
        NodeLocationToggle.IsOn = _settings.NodeLocationEnabled;
        NodeBrowserProxyToggle.IsOn = _settings.NodeBrowserProxyEnabled;
        NodeTtsToggle.IsOn = _settings.NodeTtsEnabled;
        SelectTtsProvider(_settings.TtsProvider);
        TtsElevenLabsApiKeyPasswordBox.Password = _settings.TtsElevenLabsApiKey;
        TtsElevenLabsVoiceIdTextBox.Text = _settings.TtsElevenLabsVoiceId;
        TtsElevenLabsModelTextBox.Text = _settings.TtsElevenLabsModel;
        UpdateTtsProviderUiState();
        UpdateSshTunnelPreviewText();
        McpServerToggle.IsOn = _settings.EnableMcpServer;
        McpUrlTextBox.Text = NodeService.McpServerUrl;
        McpServerToggle.Toggled += (_, _) => UpdateMcpStatus();
        UpdateMcpStatus();
        UpdateMcpTokenDisplay();
    }

    // Bearer-token display state. Token is masked by default — first click of Reveal
    // shows it, second click hides again. Copy always copies the unmasked value.
    private bool _mcpTokenRevealed;

    private void UpdateMcpTokenDisplay()
    {
        var token = OpenClaw.Shared.Mcp.McpAuthToken.TryLoad(NodeService.McpTokenPath);
        var path = NodeService.McpTokenPath;
        if (token == null)
        {
            // File hasn't been generated yet — only happens before the first MCP server start.
            McpTokenTextBox.Text = LocalizationHelper.GetString("SettingsMcpToken_NotGenerated");
            McpTokenRevealButton.IsEnabled = false;
            McpTokenCopyButton.IsEnabled = false;
            McpTokenResetButton.IsEnabled = true;
            McpTokenHintText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationHelper.GetString("SettingsMcpToken_StoredAtFormat"), path);
            return;
        }
        McpTokenRevealButton.IsEnabled = true;
        McpTokenCopyButton.IsEnabled = true;
        McpTokenResetButton.IsEnabled = true;
        McpTokenTextBox.Text = _mcpTokenRevealed ? token : new string('•', token.Length);
        McpTokenRevealButton.Content = LocalizationHelper.GetString(
            _mcpTokenRevealed ? "SettingsMcpToken_HideButton" : "SettingsMcpToken_RevealButton");
        McpTokenHintText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationHelper.GetString("SettingsMcpToken_HintFormat"), path);
    }

    private void OnRevealMcpToken(object sender, RoutedEventArgs e)
    {
        _mcpTokenRevealed = !_mcpTokenRevealed;
        UpdateMcpTokenDisplay();
    }

    // Auto-clear delay for the bearer token after Copy. 30s matches the
    // Edge/Chrome password-manager default and gives the user time to switch
    // windows and paste once. We only wipe if the clipboard *still* contains
    // our token — copying anything else in the interim takes precedence.
    private static readonly TimeSpan s_mcpTokenClipboardClearDelay = TimeSpan.FromSeconds(30);
    private System.Threading.CancellationTokenSource? _mcpTokenClipboardClearCts;

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        var token = OpenClaw.Shared.Mcp.McpAuthToken.TryLoad(NodeService.McpTokenPath);
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            var pkg = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(token);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            ScheduleMcpTokenClipboardClear(token);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Settings] Failed to copy MCP bearer token: {ex.Message}");
        }
    }

    private void ScheduleMcpTokenClipboardClear(string copiedToken)
    {
        // Cancel any previous pending clear — we just refreshed the clipboard.
        _mcpTokenClipboardClearCts?.Cancel();
        _mcpTokenClipboardClearCts?.Dispose();
        var cts = new System.Threading.CancellationTokenSource();
        _mcpTokenClipboardClearCts = cts;
        var dispatcherQueue = global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(s_mcpTokenClipboardClearDelay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            // Clipboard APIs are STA-bound — marshal back to the UI thread.
            dispatcherQueue?.TryEnqueue(() =>
            {
                try
                {
                    var current = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (current == null) return;
                    if (!current.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
                    string? text = null;
                    try
                    {
                        text = current.GetTextAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch { return; }
                    if (!string.Equals(text, copiedToken, StringComparison.Ordinal)) return; // user replaced it
                    global::Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
                    Logger.Info("[Settings] MCP bearer token auto-cleared from clipboard");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Settings] Clipboard auto-clear failed: {ex.Message}");
                }
            });
        });
    }

    private async void OnResetMcpToken(object sender, RoutedEventArgs e)
    {
        // Reset rotates the bearer token immediately and invalidates every
        // configured local MCP client. A single accidental click would force
        // the user to reconfigure Claude Desktop / Cursor / Claude Code, so
        // gate behind a confirmation dialog with Cancel as the default button.
        if (!await ConfirmResetMcpTokenAsync()) return;

        try
        {
            var nodeService = _nodeServiceProvider();
            if (nodeService != null)
                nodeService.ResetMcpToken();
            else
                OpenClaw.Shared.Mcp.McpAuthToken.Reset(NodeService.McpTokenPath);

            _mcpTokenRevealed = false;
            UpdateMcpTokenDisplay();
            Logger.Info("[Settings] MCP bearer token reset");

            new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("SettingsMcpToken_ResetToastTitle"))
                .AddText(LocalizationHelper.GetString("SettingsMcpToken_ResetToastBody"))
                .Show();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Settings] Failed to reset MCP bearer token: {ex.Message}");
            McpTokenHintText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationHelper.GetString("SettingsMcpToken_ResetFailedFormat"), ex.Message);
        }
    }

    private async Task<bool> ConfirmResetMcpTokenAsync()
    {
        // ContentDialog needs a XamlRoot to anchor against. Settings is a real
        // WinUI window with content, so this should always succeed; if it
        // doesn't (e.g. content not yet attached), fall back to confirming via
        // a Win32 message box rather than silently performing the reset.
        var xamlRoot = (this.Content as Microsoft.UI.Xaml.FrameworkElement)?.XamlRoot;
        if (xamlRoot != null)
        {
            try
            {
                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = LocalizationHelper.GetString("SettingsMcpTokenResetDialog_Title"),
                    Content = LocalizationHelper.GetString("SettingsMcpTokenResetDialog_Body"),
                    PrimaryButtonText = LocalizationHelper.GetString("SettingsMcpTokenResetDialog_PrimaryButton"),
                    CloseButtonText = LocalizationHelper.GetString("SettingsMcpTokenResetDialog_CloseButton"),
                    DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
                    XamlRoot = xamlRoot,
                };
                var result = await dialog.ShowAsync();
                return result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Settings] Reset confirmation dialog failed, falling back to MessageBox: {ex.Message}");
            }
        }

        // Off-thread fallback: Win32 MessageBoxW so we never reset without confirmation.
        return await Task.Run(() =>
        {
            const uint MB_OKCANCEL = 0x00000001;
            const uint MB_ICONWARNING = 0x00000030;
            const uint MB_DEFBUTTON2 = 0x00000100;
            const int IDOK = 1;
            var caption = LocalizationHelper.GetString("SettingsMcpTokenResetDialog_Title");
            var text = LocalizationHelper.GetString("SettingsMcpTokenResetDialog_Body");
            var rc = NativeMessageBox(IntPtr.Zero, text, caption,
                MB_OKCANCEL | MB_ICONWARNING | MB_DEFBUTTON2);
            return rc == IDOK;
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int NativeMessageBox(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// Public refresh hook called by the host after it tears down and rebuilds
    /// the NodeService (e.g. App.OnSettingsSaved). Re-reads the running/error
    /// state from the current node service and refreshes the status text.
    /// </summary>
    public void RefreshMcpStatus()
    {
        try
        {
            UpdateMcpStatus();
            UpdateMcpTokenDisplay();
        }
        catch { /* best-effort UI refresh */ }
    }

    private void UpdateMcpStatus()
    {
        var toggleOn = McpServerToggle.IsOn;
        var savedOn = _settings.EnableMcpServer;
        var nodeService = _nodeServiceProvider();
        var running = nodeService?.IsMcpRunning == true;
        var startupError = nodeService?.McpStartupError;

        if (!toggleOn)
        {
            McpStatusText.Text = LocalizationHelper.GetString("Mcp_Status_Disabled");
            return;
        }

        // Toggle changed but not saved yet — Save applies immediately, so be
        // explicit instead of the old "save and restart" wording (the tray
        // reinitializes services in OnSettingsSaved without an app restart).
        if (toggleOn != savedOn)
        {
            McpStatusText.Text = LocalizationHelper.GetString(savedOn
                ? "Mcp_Status_WillStopOnSave"
                : "Mcp_Status_WillStartOnSave");
            return;
        }

        if (running)
        {
            McpStatusText.Text = LocalizationHelper.GetString("Mcp_Status_Listening");
            return;
        }

        if (!string.IsNullOrEmpty(startupError))
        {
            // The diagnostic detail (URL ACL command, port number) stays in
            // English on purpose — it's a literal CLI invocation. Only the
            // localized "Failed to start:" prefix wraps it.
            McpStatusText.Text = LocalizationHelper.GetString("Mcp_Status_FailedToStart") + startupError;
            return;
        }

        // Toggle on, saved on, but no service yet — node service is still
        // initializing or hasn't been created (gateway-only setup path).
        McpStatusText.Text = LocalizationHelper.GetString("Mcp_Status_Stopped");
    }

    private void SaveSettings()
    {
        _settings.UseSshTunnel = UseSshTunnelToggle.IsOn;
        _settings.SshTunnelUser = SshTunnelUserTextBox.Text.Trim();
        _settings.SshTunnelHost = SshTunnelHostTextBox.Text.Trim();
        _settings.SshTunnelRemotePort = ParsePortOrDefault(SshTunnelRemotePortTextBox.Text, _settings.SshTunnelRemotePort);
        _settings.SshTunnelLocalPort = ParsePortOrDefault(SshTunnelLocalPortTextBox.Text, _settings.SshTunnelLocalPort);
        if (!_settings.UseSshTunnel)
        {
            _settings.GatewayUrl = GatewayUrlTextBox.Text.Trim();
            _manualGatewayUrl = _settings.GatewayUrl;
        }
        _settings.Token = TokenTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.Token))
        {
            _settings.BootstrapToken = "";
        }
        _settings.AutoStart = AutoStartToggle.IsOn;
        _settings.GlobalHotkeyEnabled = GlobalHotkeyToggle.IsOn;
        _settings.ShowNotifications = NotificationsToggle.IsOn;
        
        if (NotificationSoundComboBox.SelectedItem is Microsoft.UI.Xaml.Controls.ComboBoxItem item)
        {
            _settings.NotificationSound = item.Tag?.ToString() ?? "Default";
        }

        _settings.NotifyHealth = NotifyHealthCb.IsChecked ?? true;
        _settings.NotifyUrgent = NotifyUrgentCb.IsChecked ?? true;
        _settings.NotifyReminder = NotifyReminderCb.IsChecked ?? true;
        _settings.NotifyEmail = NotifyEmailCb.IsChecked ?? true;
        _settings.NotifyCalendar = NotifyCalendarCb.IsChecked ?? true;
        _settings.NotifyBuild = NotifyBuildCb.IsChecked ?? true;
        _settings.NotifyStock = NotifyStockCb.IsChecked ?? true;
        _settings.NotifyInfo = NotifyInfoCb.IsChecked ?? true;
        
        // Advanced
        _settings.EnableNodeMode = NodeModeToggle.IsOn;
        _settings.NodeCanvasEnabled = NodeCanvasToggle.IsOn;
        _settings.NodeScreenEnabled = NodeScreenToggle.IsOn;
        _settings.NodeCameraEnabled = NodeCameraToggle.IsOn;
        _settings.NodeLocationEnabled = NodeLocationToggle.IsOn;
        _settings.NodeBrowserProxyEnabled = NodeBrowserProxyToggle.IsOn;
        _settings.NodeTtsEnabled = NodeTtsToggle.IsOn;
        _settings.TtsProvider = GetSelectedTtsProvider();
        _settings.TtsElevenLabsApiKey = TtsElevenLabsApiKeyPasswordBox.Password.Trim();
        _settings.TtsElevenLabsVoiceId = TtsElevenLabsVoiceIdTextBox.Text.Trim();
        _settings.TtsElevenLabsModel = TtsElevenLabsModelTextBox.Text.Trim();
        _settings.EnableMcpServer = McpServerToggle.IsOn;

        _settings.Save();
        AutoStartManager.SetAutoStart(_settings.AutoStart);
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var useSshTunnel = UseSshTunnelToggle.IsOn;
        var sshUser = "";
        var sshHost = "";
        var remotePort = 0;
        var localPort = 0;
        SshTunnelService? testTunnel = null;

        var gatewayUrl = GatewayUrlTextBox.Text.Trim();
        if (!useSshTunnel && !GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
        {
            StatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
            return;
        }

        if (useSshTunnel && !TryReadTunnelSettings(out sshUser, out sshHost, out remotePort, out localPort, out var tunnelError))
        {
            StatusLabel.Text = $"❌ {tunnelError}";
            return;
        }

        Logger.Info("[Settings] Test connection initiated");
        StatusLabel.Text = LocalizationHelper.GetString("Status_Testing");
        TestConnectionButton.IsEnabled = false;

        try
        {
            var testLogger = new TestLogger();
            if (useSshTunnel)
            {
                testTunnel = new SshTunnelService(testLogger);
                var includeBrowserProxyForward =
                    NodeBrowserProxyToggle.IsOn &&
                    SshTunnelCommandLine.CanForwardBrowserProxyPort(remotePort, localPort);
                Logger.Info($"[Settings] Starting temporary SSH tunnel for test: {sshUser}@{sshHost} local:{localPort} remote:{remotePort} browserProxyForward:{includeBrowserProxyForward}");
                testTunnel.EnsureStarted(sshUser, sshHost, remotePort, localPort, includeBrowserProxyForward);
            }

            var client = new OpenClawGatewayClient(
                useSshTunnel ? $"ws://127.0.0.1:{localPort}" : gatewayUrl,
                TokenTextBox.Text.Trim(),
                testLogger);

            var connected = false;
            var tcs = new TaskCompletionSource<bool>();
            
            client.StatusChanged += (s, status) =>
            {
                if (status == ConnectionStatus.Connected)
                {
                    connected = true;
                    tcs.TrySetResult(true);
                }
                else if (status == ConnectionStatus.Error)
                {
                    tcs.TrySetResult(false);
                }
            };

            _ = client.ConnectAsync();
            
            // Wait up to 5 seconds for connection
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            if (completedTask != tcs.Task)
            {
                connected = false;
            }

            if (connected)
            {
                Logger.Info("[Settings] Test connection succeeded");
                StatusLabel.Text = LocalizationHelper.GetString("Status_Connected");
            }
            else
            {
                Logger.Warn("[Settings] Test connection failed or timed out");
                var lastError = testLogger.LastError;
                StatusLabel.Text = !string.IsNullOrEmpty(lastError)
                    ? $"❌ {lastError}"
                    : $"❌ {LocalizationHelper.GetString("Status_ConnectionFailed")}";
            }
            client.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Settings] Test connection error: {ex.Message}");
            StatusLabel.Text = $"❌ {ex.Message}";
        }
        finally
        {
            testTunnel?.Dispose();
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void OnTestNotification(object sender, RoutedEventArgs e)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("TestNotification_Title"))
                .AddText(LocalizationHelper.GetString("TestNotification_Body"))
                .Show();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"❌ {ex.Message}";
        }
    }

    private void OnOpenCommandCenter(object sender, RoutedEventArgs e)
    {
        Logger.Info("[Settings] Open Command Center requested");
        CommandCenterRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var useSshTunnel = UseSshTunnelToggle.IsOn;
        var gatewayUrl = GatewayUrlTextBox.Text.Trim();
        if (!useSshTunnel && !GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
        {
            Logger.Warn($"[Settings] Save blocked — invalid gateway URL");
            StatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
            return;
        }

        if (useSshTunnel && !TryReadTunnelSettings(out _, out _, out _, out _, out var tunnelError))
        {
            Logger.Warn("[Settings] Save blocked — invalid SSH tunnel settings");
            StatusLabel.Text = $"❌ {tunnelError}";
            return;
        }

        // Log key setting changes before saving
        var oldGateway = _settings.GatewayUrl;
        var oldAutoStart = _settings.AutoStart;
        var oldNodeMode = _settings.EnableNodeMode;
        SaveSettings();

        if (!string.Equals(oldGateway, _settings.GatewayUrl, StringComparison.Ordinal))
            Logger.Info($"[Settings] GatewayUrl changed");
        if (oldAutoStart != _settings.AutoStart)
            Logger.Info($"[Settings] AutoStart changed to {_settings.AutoStart}");
        if (oldNodeMode != _settings.EnableNodeMode)
            Logger.Info($"[Settings] NodeMode changed to {_settings.EnableNodeMode}");

        Logger.Info("[Settings] Settings saved");
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Logger.Info("[Settings] Cancel clicked");
        Close();
    }

    private static int ParsePortOrDefault(string? value, int fallback)
    {
        if (int.TryParse(value?.Trim(), out var parsed) && parsed is >= 1 and <= 65535)
        {
            return parsed;
        }

        return fallback;
    }

    private bool TryReadTunnelSettings(
        out string user,
        out string host,
        out int remotePort,
        out int localPort,
        out string? error)
    {
        user = SshTunnelUserTextBox.Text.Trim();
        host = SshTunnelHostTextBox.Text.Trim();
        remotePort = 0;
        localPort = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(user))
        {
            error = "SSH User is required when tunnel mode is enabled.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "SSH Host is required when tunnel mode is enabled.";
            return false;
        }

        if (!int.TryParse(SshTunnelRemotePortTextBox.Text.Trim(), out remotePort) || remotePort is < 1 or > 65535)
        {
            error = "Remote Gateway Port must be a number from 1 to 65535.";
            return false;
        }

        if (!int.TryParse(SshTunnelLocalPortTextBox.Text.Trim(), out localPort) || localPort is < 1 or > 65535)
        {
            error = "Local Forward Port must be a number from 1 to 65535.";
            return false;
        }

        return true;
    }

    private void OnUseSshTunnelToggled(object sender, RoutedEventArgs e)
    {
        UpdateSshTunnelUiState();
    }

    private void OnSshTunnelLocalPortTextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        if (UseSshTunnelToggle.IsOn)
        {
            UpdateSshTunnelUiState();
        }
        else
        {
            UpdateDetectedTopologyText();
            UpdateSshTunnelPreviewText();
        }
    }

    private void OnTopologyInputChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        UpdateDetectedTopologyText();
        UpdateSshTunnelPreviewText();
    }

    private void OnNodeBrowserProxyToggled(object sender, RoutedEventArgs e)
    {
        UpdateSshTunnelPreviewText();
    }

    private void OnTtsProviderSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        UpdateTtsProviderUiState();
    }

    private void SelectTtsProvider(string provider)
    {
        for (int i = 0; i < TtsProviderComboBox.Items.Count; i++)
        {
            if (TtsProviderComboBox.Items[i] is Microsoft.UI.Xaml.Controls.ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                TtsProviderComboBox.SelectedIndex = i;
                return;
            }
        }

        TtsProviderComboBox.SelectedIndex = 0;
    }

    private string GetSelectedTtsProvider()
    {
        if (TtsProviderComboBox.SelectedItem is Microsoft.UI.Xaml.Controls.ComboBoxItem item &&
            item.Tag is not null)
        {
            return item.Tag.ToString() ?? "windows";
        }

        return "windows";
    }

    private void UpdateTtsProviderUiState()
    {
        if (TtsElevenLabsSettingsPanel == null)
            return;

        TtsElevenLabsSettingsPanel.Visibility =
            string.Equals(GetSelectedTtsProvider(), "elevenlabs", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnUseLocalGateway(object sender, RoutedEventArgs e)
    {
        UseSshTunnelToggle.IsOn = false;
        GatewayUrlTextBox.Text = "ws://127.0.0.1:18789";
        _manualGatewayUrl = GatewayUrlTextBox.Text;
        StatusLabel.Text = "Local gateway selected. Use this when the gateway runs directly on Windows.";
        UpdateDetectedTopologyText();
        Logger.Info("[Settings] Topology preset selected: local gateway");
    }

    private void OnUseWslGateway(object sender, RoutedEventArgs e)
    {
        UseSshTunnelToggle.IsOn = false;
        GatewayUrlTextBox.Text = "ws://wsl.localhost:18789";
        _manualGatewayUrl = GatewayUrlTextBox.Text;
        StatusLabel.Text = "WSL gateway selected. Change the distro host if your gateway uses a named distro.";
        UpdateDetectedTopologyText();
        Logger.Info("[Settings] Topology preset selected: WSL gateway");
    }

    private void OnUseSshTunnel(object sender, RoutedEventArgs e)
    {
        UseSshTunnelToggle.IsOn = true;
        UpdateSshTunnelUiState();
        StatusLabel.Text = "SSH tunnel selected. Fill in SSH User and SSH Host, then test the connection.";
        UpdateDetectedTopologyText();
        Logger.Info("[Settings] Topology preset selected: SSH tunnel");
    }

    private void OnUseRemoteGateway(object sender, RoutedEventArgs e)
    {
        UseSshTunnelToggle.IsOn = false;
        GatewayUrlTextBox.Text = GatewayUrlTextBox.Text.StartsWith("ws://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
                                 GatewayUrlTextBox.Text.StartsWith("ws://wsl.localhost:", StringComparison.OrdinalIgnoreCase)
            ? "wss://host.tailnet.ts.net"
            : GatewayUrlTextBox.Text;
        _manualGatewayUrl = GatewayUrlTextBox.Text;
        StatusLabel.Text = "Remote gateway selected. Prefer wss:// for Tailscale, LAN, or public gateways.";
        UpdateDetectedTopologyText();
        Logger.Info("[Settings] Topology preset selected: remote gateway");
    }

    private void UpdateSshTunnelUiState()
    {
        var useSshTunnel = UseSshTunnelToggle.IsOn;
        var wasReadOnly = GatewayUrlTextBox.IsReadOnly;

        SshTunnelDetailsPanel.Visibility = useSshTunnel ? Visibility.Visible : Visibility.Collapsed;
        GatewayUrlTextBox.IsReadOnly = useSshTunnel;

        if (useSshTunnel)
        {
            if (!wasReadOnly)
            {
                _manualGatewayUrl = GatewayUrlTextBox.Text.Trim();
            }

            var localPort = ParsePortOrDefault(SshTunnelLocalPortTextBox.Text, 18789);
            GatewayUrlTextBox.Text = $"ws://127.0.0.1:{localPort}";
        }
        else
        {
            if (GatewayUrlTextBox.Text.StartsWith("ws://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            {
                GatewayUrlTextBox.Text = _manualGatewayUrl;
            }
        }

        UpdateDetectedTopologyText();
        UpdateSshTunnelPreviewText();
    }

    private void UpdateDetectedTopologyText()
    {
        if (DetectedTopologyText == null)
            return;

        var topology = GatewayTopologyClassifier.Classify(
            GatewayUrlTextBox.Text,
            UseSshTunnelToggle.IsOn,
            SshTunnelHostTextBox.Text,
            ParsePortOrDefault(SshTunnelLocalPortTextBox.Text, _settings.SshTunnelLocalPort),
            ParsePortOrDefault(SshTunnelRemotePortTextBox.Text, _settings.SshTunnelRemotePort));

        DetectedTopologyText.Text = $"Detected: {topology.DisplayName} · {topology.Transport} · {topology.Detail}";
    }

    private void UpdateSshTunnelPreviewText()
    {
        if (SshTunnelPreviewText == null)
            return;

        if (!UseSshTunnelToggle.IsOn)
        {
            SshTunnelPreviewText.Text = "";
            return;
        }

        var user = SshTunnelUserTextBox.Text.Trim();
        var host = SshTunnelHostTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(host) ||
            !int.TryParse(SshTunnelRemotePortTextBox.Text.Trim(), out var remotePort) ||
            !int.TryParse(SshTunnelLocalPortTextBox.Text.Trim(), out var localPort))
        {
            SshTunnelPreviewText.Text = "Managed tunnel preview: fill SSH user, SSH host, and ports to preview the exact ssh command.";
            return;
        }

        var includeBrowserProxyForward =
            NodeBrowserProxyToggle.IsOn &&
            SshTunnelCommandLine.CanForwardBrowserProxyPort(remotePort, localPort);

        try
        {
            var args = SshTunnelCommandLine.BuildArguments(user, host, remotePort, localPort, includeBrowserProxyForward);
            SshTunnelPreviewText.Text = $"Managed tunnel preview: ssh {args}";
            if (NodeBrowserProxyToggle.IsOn && !includeBrowserProxyForward)
            {
                SshTunnelPreviewText.Text += "\nBrowser proxy companion forward skipped because gateway ports must be 65533 or below.";
            }
        }
        catch (ArgumentException ex)
        {
            SshTunnelPreviewText.Text = $"Managed tunnel preview unavailable: {ex.Message}";
        }
    }

    private class TestLogger : IOpenClawLogger
    {
        public string? LastError { get; private set; }

        public void Info(string message) => Logger.Info($"[Settings:TestClient] {message}");
        public void Debug(string message) { }
        public void Warn(string message)
        {
            LastError ??= message;
            Logger.Warn($"[Settings:TestClient] {message}");
        }
        public void Error(string message, Exception? ex = null)
        {
            LastError = ex != null
                ? $"{message}: {ex.Message}"
                : message;
            Logger.Error($"[Settings:TestClient] {LastError}");
        }
    }
}
