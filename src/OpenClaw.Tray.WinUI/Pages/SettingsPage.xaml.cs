using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class SettingsPage : Page
{
    private HubWindow? _hub;
    private bool _initialized;


    public SettingsPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        if (!_initialized && hub.Settings != null)
        {
            LoadSettings(hub.Settings);
            _initialized = true;
        }
    }

    private void LoadSettings(SettingsManager settings)
    {
        UseSshTunnelToggle.IsOn = settings.UseSshTunnel;
        GatewayUrlTextBox.Text = settings.GatewayUrl;
        TokenTextBox.Text = settings.Token;
        AutoStartToggle.IsOn = settings.AutoStart;
        GlobalHotkeyToggle.IsOn = settings.GlobalHotkeyEnabled;
        NotificationsToggle.IsOn = settings.ShowNotifications;

        for (int i = 0; i < NotificationSoundComboBox.Items.Count; i++)
        {
            if (NotificationSoundComboBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == settings.NotificationSound)
            {
                NotificationSoundComboBox.SelectedIndex = i;
                break;
            }
        }
        if (NotificationSoundComboBox.SelectedIndex < 0)
            NotificationSoundComboBox.SelectedIndex = 0;

        NotifyHealthCb.IsChecked = settings.NotifyHealth;
        NotifyUrgentCb.IsChecked = settings.NotifyUrgent;
        NotifyReminderCb.IsChecked = settings.NotifyReminder;
        NotifyEmailCb.IsChecked = settings.NotifyEmail;
        NotifyCalendarCb.IsChecked = settings.NotifyCalendar;
        NotifyBuildCb.IsChecked = settings.NotifyBuild;
        NotifyStockCb.IsChecked = settings.NotifyStock;
        NotifyInfoCb.IsChecked = settings.NotifyInfo;

        SshTunnelUserTextBox.Text = settings.SshTunnelUser;
        SshTunnelHostTextBox.Text = settings.SshTunnelHost;
        SshTunnelRemotePortTextBox.Text = settings.SshTunnelRemotePort.ToString();
        SshTunnelLocalPortTextBox.Text = settings.SshTunnelLocalPort.ToString();
        SshTunnelDetailsPanel.Visibility = settings.UseSshTunnel ? Visibility.Visible : Visibility.Collapsed;


    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;

        var useSshTunnel = UseSshTunnelToggle.IsOn;
        var gatewayUrl = GatewayUrlTextBox.Text.Trim();

        // Validate gateway URL (when not using SSH tunnel)
        if (!useSshTunnel && !GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
        {
            ConnectionStatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
            return;
        }

        // Validate SSH tunnel settings
        if (useSshTunnel)
        {
            if (string.IsNullOrWhiteSpace(SshTunnelUserTextBox.Text))
            {
                ConnectionStatusLabel.Text = "❌ SSH User is required when tunnel mode is enabled.";
                return;
            }
            if (string.IsNullOrWhiteSpace(SshTunnelHostTextBox.Text))
            {
                ConnectionStatusLabel.Text = "❌ SSH Host is required when tunnel mode is enabled.";
                return;
            }
        }

        var s = _hub.Settings;

        s.UseSshTunnel = useSshTunnel;
        s.GatewayUrl = gatewayUrl;
        s.Token = TokenTextBox.Text.Trim();
        s.AutoStart = AutoStartToggle.IsOn;
        s.GlobalHotkeyEnabled = GlobalHotkeyToggle.IsOn;
        s.ShowNotifications = NotificationsToggle.IsOn;

        if (NotificationSoundComboBox.SelectedItem is ComboBoxItem item)
            s.NotificationSound = item.Tag?.ToString() ?? "Default";

        s.NotifyHealth = NotifyHealthCb.IsChecked ?? true;
        s.NotifyUrgent = NotifyUrgentCb.IsChecked ?? true;
        s.NotifyReminder = NotifyReminderCb.IsChecked ?? true;
        s.NotifyEmail = NotifyEmailCb.IsChecked ?? true;
        s.NotifyCalendar = NotifyCalendarCb.IsChecked ?? true;
        s.NotifyBuild = NotifyBuildCb.IsChecked ?? true;
        s.NotifyStock = NotifyStockCb.IsChecked ?? true;
        s.NotifyInfo = NotifyInfoCb.IsChecked ?? true;

        s.SshTunnelUser = SshTunnelUserTextBox.Text.Trim();
        s.SshTunnelHost = SshTunnelHostTextBox.Text.Trim();
        if (int.TryParse(SshTunnelRemotePortTextBox.Text, out var rp)) s.SshTunnelRemotePort = rp;
        if (int.TryParse(SshTunnelLocalPortTextBox.Text, out var lp)) s.SshTunnelLocalPort = lp;

        s.Save();
        AutoStartManager.SetAutoStart(s.AutoStart);
        _hub.RaiseSettingsSaved();

        SaveButton.Content = "✓ Saved";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (t, a) => { SaveButton.Content = "Save"; timer.Stop(); };
        timer.Start();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings != null)
        {
            _initialized = false;
            LoadSettings(_hub.Settings);
            _initialized = true;
        }
    }

    private void OnSshTunnelToggled(object sender, RoutedEventArgs e)
    {
        var isTunnel = UseSshTunnelToggle.IsOn;
        SshTunnelDetailsPanel.Visibility = isTunnel ? Visibility.Visible : Visibility.Collapsed;

        if (isTunnel)
        {
            // Store the manual URL and show tunnel URL
            if (!GatewayUrlTextBox.Text.StartsWith("ws://127.0.0.1"))
            {
                GatewayUrlTextBox.Tag = GatewayUrlTextBox.Text; // stash manual URL
            }
            var localPort = int.TryParse(SshTunnelLocalPortTextBox.Text, out var lp) ? lp : 18789;
            GatewayUrlTextBox.Text = $"ws://127.0.0.1:{localPort}";
            GatewayUrlTextBox.IsEnabled = false;
        }
        else
        {
            // Restore manual URL
            GatewayUrlTextBox.IsEnabled = true;
            if (GatewayUrlTextBox.Tag is string savedUrl && !string.IsNullOrEmpty(savedUrl))
            {
                GatewayUrlTextBox.Text = savedUrl;
            }
        }
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var useSshTunnel = UseSshTunnelToggle.IsOn;
        var gatewayUrl = GatewayUrlTextBox.Text.Trim();

        if (!useSshTunnel && string.IsNullOrEmpty(gatewayUrl))
        {
            ConnectionStatusLabel.Text = "❌ Gateway URL is required";
            return;
        }

        if (!useSshTunnel && !GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
        {
            ConnectionStatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
            return;
        }

        ConnectionStatusLabel.Text = "Testing...";
        TestConnectionButton.IsEnabled = false;
        SshTunnelService? testTunnel = null;

        try
        {
            var testLogger = new SettingsTestLogger();

            string connectUrl = gatewayUrl;
            if (useSshTunnel)
            {
                var sshUser = SshTunnelUserTextBox.Text.Trim();
                var sshHost = SshTunnelHostTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(sshUser) || string.IsNullOrWhiteSpace(sshHost))
                {
                    ConnectionStatusLabel.Text = "❌ SSH User and Host are required";
                    TestConnectionButton.IsEnabled = true;
                    return;
                }
                var remotePort = int.TryParse(SshTunnelRemotePortTextBox.Text, out var rp) ? rp : 18789;
                var localPort = int.TryParse(SshTunnelLocalPortTextBox.Text, out var lp) ? lp : 18789;

                testTunnel = new SshTunnelService(testLogger);
                testTunnel.EnsureStarted(sshUser, sshHost, remotePort, localPort);
                connectUrl = $"ws://127.0.0.1:{localPort}";
            }

            var client = new OpenClawGatewayClient(connectUrl, TokenTextBox.Text.Trim(), testLogger);

            var tcs = new TaskCompletionSource<bool>();
            client.StatusChanged += (s, status) =>
            {
                if (status == ConnectionStatus.Connected) tcs.TrySetResult(true);
                else if (status == ConnectionStatus.Error) tcs.TrySetResult(false);
            };

            _ = client.ConnectAsync();
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            var connected = completed == tcs.Task && await tcs.Task;

            ConnectionStatusLabel.Text = connected ? "✅ Connected" : "❌ Connection failed";
            client.Dispose();
        }
        catch (Exception ex)
        {
            ConnectionStatusLabel.Text = $"❌ {ex.Message}";
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
                .AddText("Test Notification")
                .AddText("This is a test notification from OpenClaw Hub settings.")
                .Show();
        }
        catch (Exception ex)
        {
            ConnectionStatusLabel.Text = $"❌ {ex.Message}";
        }
    }

    private class SettingsTestLogger : IOpenClawLogger
    {
        public void Info(string message) => Logger.Info($"[Settings:TestClient] {message}");
        public void Debug(string message) { }
        public void Warn(string message) => Logger.Warn($"[Settings:TestClient] {message}");
        public void Error(string message, Exception? ex = null) =>
            Logger.Error($"[Settings:TestClient] {message}{(ex != null ? $": {ex.Message}" : "")}");
    }
}
