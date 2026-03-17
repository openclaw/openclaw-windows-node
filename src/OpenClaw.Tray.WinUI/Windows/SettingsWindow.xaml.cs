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
        GatewayUrlTextBox.Text = _settings.GatewayUrl;
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
        _settings.GatewayUrl = GatewayUrlTextBox.Text.Trim();
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
        var gatewayUrl = GatewayUrlTextBox.Text.Trim();
        if (!GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
        {
            StatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
            return;
        }

        Logger.Info("[Settings] Test connection initiated");
        StatusLabel.Text = LocalizationHelper.GetString("Status_Testing");
        TestConnectionButton.IsEnabled = false;

        try
        {
            var testLogger = new TestLogger();
            var client = new OpenClawGatewayClient(
                gatewayUrl,
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
        var gatewayUrl = GatewayUrlTextBox.Text.Trim();
        if (!GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
        {
            Logger.Warn($"[Settings] Save blocked — invalid gateway URL");
            StatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
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
            LastError = message;
            Logger.Error($"[Settings:TestClient] {message}");
        }
    }
}
