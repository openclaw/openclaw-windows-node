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
    private string _manualGatewayUrl = "";
    public bool IsClosed { get; private set; }

    public event EventHandler? SettingsSaved;

    public SettingsWindow(SettingsManager settings)
    {
        _settings = settings;
        InitializeComponent();
        
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
                Logger.Info($"[Settings] Starting temporary SSH tunnel for test: {sshUser}@{sshHost} local:{localPort} remote:{remotePort}");
                testTunnel.EnsureStarted(sshUser, sshHost, remotePort, localPort);
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
