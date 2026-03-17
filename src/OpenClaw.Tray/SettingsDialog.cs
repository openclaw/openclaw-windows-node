using Microsoft.Toolkit.Uwp.Notifications;
using OpenClaw.Shared;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace OpenClawTray;

public partial class SettingsDialog : ModernForm
{
    private readonly SettingsManager _settings;
    
    private TextBox _gatewayUrlTextBox = null!;
    private TextBox _tokenTextBox = null!;
    private CheckBox _autoStartCheckBox = null!;
    private CheckBox _showNotificationsCheckBox = null!;
    private CheckBox _globalHotkeyCheckBox = null!;
    private ComboBox _notificationSoundComboBox = null!;
    private Button _testConnectionButton = null!;
    private Button _testNotificationButton = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;
    private Label _statusLabel = null!;

    // Notification filter checkboxes
    private CheckBox _notifyHealthCb = null!;
    private CheckBox _notifyUrgentCb = null!;
    private CheckBox _notifyReminderCb = null!;
    private CheckBox _notifyEmailCb = null!;
    private CheckBox _notifyCalendarCb = null!;
    private CheckBox _notifyBuildCb = null!;
    private CheckBox _notifyStockCb = null!;
    private CheckBox _notifyInfoCb = null!;
    private Panel _notifyFilterPanel = null!;

    public SettingsDialog(SettingsManager settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        Text = "Settings — OpenClaw Tray";
        Size = new Size(480, 600);
        ShowInTaskbar = false;
        AutoScroll = false;
        Icon = IconHelper.GetLobsterIcon();

        var y = 16;

        // --- Connection Section ---
        var connHeader = CreateModernLabel("CONNECTION");
        connHeader.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        connHeader.ForeColor = AccentColor;
        connHeader.Location = new Point(16, y);
        y += 26;

        var gatewayUrlLabel = CreateModernLabel("Gateway URL:");
        gatewayUrlLabel.Location = new Point(16, y);
        y += 24;

        _gatewayUrlTextBox = CreateModernTextBox();
        _gatewayUrlTextBox.Location = new Point(16, y);
        _gatewayUrlTextBox.Size = new Size(310, 28);

        _testConnectionButton = CreateModernButton("Test");
        _testConnectionButton.Location = new Point(334, y - 2);
        _testConnectionButton.Size = new Size(70, 30);
        _testConnectionButton.Click += OnTestConnection;
        y += 36;

        var tokenLabel = CreateModernLabel("Token:");
        tokenLabel.Location = new Point(16, y);
        y += 24;

        _tokenTextBox = CreateModernTextBox();
        _tokenTextBox.Location = new Point(16, y);
        _tokenTextBox.Size = new Size(310, 28);
        _tokenTextBox.UseSystemPasswordChar = true;

        _statusLabel = CreateModernLabel("", isSubtle: true);
        _statusLabel.Location = new Point(334, y + 4);
        _statusLabel.Font = new Font("Segoe UI", 8.5F);
        y += 44;

        // --- Startup Section ---
        var startupHeader = CreateModernLabel("STARTUP");
        startupHeader.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        startupHeader.ForeColor = AccentColor;
        startupHeader.Location = new Point(16, y);
        y += 26;

        _autoStartCheckBox = CreateModernCheckBox("Start automatically with Windows");
        _autoStartCheckBox.Location = new Point(16, y);
        y += 28;

        _globalHotkeyCheckBox = CreateModernCheckBox("Global hotkey (Ctrl+Alt+Shift+C → Quick Send)");
        _globalHotkeyCheckBox.Location = new Point(16, y);
        y += 40;

        // --- Notifications Section ---
        var notifyHeader = CreateModernLabel("NOTIFICATIONS");
        notifyHeader.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        notifyHeader.ForeColor = AccentColor;
        notifyHeader.Location = new Point(16, y);
        y += 26;

        _showNotificationsCheckBox = CreateModernCheckBox("Show desktop notifications");
        _showNotificationsCheckBox.Location = new Point(16, y);
        _showNotificationsCheckBox.CheckedChanged += (_, _) =>
        {
            _notifyFilterPanel.Enabled = _showNotificationsCheckBox.Checked;
        };
        y += 28;

        var soundLabel = CreateModernLabel("Sound:");
        soundLabel.Location = new Point(16, y + 3);
        soundLabel.AutoSize = true;

        _notificationSoundComboBox = new ComboBox
        {
            Location = new Point(80, y),
            Size = new Size(140, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = SurfaceColor,
            ForeColor = ForegroundColor,
            FlatStyle = FlatStyle.Flat
        };
        // Items are stable persistence keys (must match values in settings.json).
        // Do not localize these strings — use a display/key mapping if localization is needed.
        _notificationSoundComboBox.Items.AddRange(new[] { "Default", "None", "Critical", "Information" });
        
        _testNotificationButton = CreateModernButton("Test");
        _testNotificationButton.Location = new Point(230, y);
        _testNotificationButton.Size = new Size(80, 28);
        _testNotificationButton.Click += OnTestNotification;
        y += 36;

        // Filter panel
        var filterLabel = CreateModernLabel("Show toasts for:", isSubtle: true);
        filterLabel.Location = new Point(16, y);
        y += 24;

        _notifyFilterPanel = new Panel
        {
            Location = new Point(16, y),
            Size = new Size(440, 72),
            BorderStyle = BorderStyle.None,
            BackColor = Color.Transparent
        };

        // Two columns of filter checkboxes
        _notifyHealthCb = MakeFilterCb("🩸 Health", 0, 0);
        _notifyUrgentCb = MakeFilterCb("🚨 Urgent", 0, 24);
        _notifyReminderCb = MakeFilterCb("⏰ Reminders", 0, 48);
        _notifyEmailCb = MakeFilterCb("📧 Email", 150, 0);
        _notifyCalendarCb = MakeFilterCb("📅 Calendar", 150, 24);
        _notifyBuildCb = MakeFilterCb("🔨 Build/CI", 150, 48);
        _notifyStockCb = MakeFilterCb("📦 Stock", 300, 0);
        _notifyInfoCb = MakeFilterCb("🤖 General", 300, 24);

        _notifyFilterPanel.Controls.AddRange(new Control[]
        {
            _notifyHealthCb, _notifyUrgentCb, _notifyReminderCb,
            _notifyEmailCb, _notifyCalendarCb, _notifyBuildCb,
            _notifyStockCb, _notifyInfoCb
        });

        y += 90;

        // --- About Section ---
        var aboutLabel = CreateModernLabel("Made with 🦞 love by Scott Hanselman and Molty", isSubtle: true);
        aboutLabel.Font = new Font("Segoe UI", 8.5F);
        aboutLabel.Location = new Point(16, y);
        aboutLabel.AutoSize = true;
        aboutLabel.Cursor = Cursors.Hand;
        aboutLabel.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/shanselman/openclaw-windows-hub",
            UseShellExecute = true
        });

        y += 30;

        // --- Buttons ---
        _cancelButton = CreateModernButton("Cancel");
        _cancelButton.Location = new Point(Width - 116, y);
        _cancelButton.Size = new Size(90, 34);
        _cancelButton.Click += OnCancelClick;

        _okButton = CreateModernButton("Save", isPrimary: true);
        _okButton.Location = new Point(Width - 214, y);
        _okButton.Size = new Size(90, 34);
        _okButton.Click += OnOkClick;

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        // Add all controls
        Controls.AddRange(new Control[]
        {
            connHeader, gatewayUrlLabel, _gatewayUrlTextBox, _testConnectionButton,
            tokenLabel, _tokenTextBox, _statusLabel,
            startupHeader, _autoStartCheckBox, _globalHotkeyCheckBox,
            notifyHeader, _showNotificationsCheckBox, soundLabel, _notificationSoundComboBox, _testNotificationButton,
            filterLabel, _notifyFilterPanel,
            aboutLabel, _okButton, _cancelButton
        });
    }

    private void OnTestNotification(object? sender, EventArgs e)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("🦞 Test Notification")
                .AddText("This is what an OpenClaw notification looks like!")
                .Show();
        }
        catch
        {
            MessageBox.Show("Notifications may not be available on this system.", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private CheckBox MakeFilterCb(string text, int x, int y)
    {
        var cb = CreateModernCheckBox(text);
        cb.Location = new Point(x, y);
        cb.Size = new Size(140, 22);
        cb.Font = new Font("Segoe UI", 8.5F);
        cb.Checked = true;
        return cb;
    }

    private void LoadSettings()
    {
        _gatewayUrlTextBox.Text = _settings.GatewayUrl;
        _tokenTextBox.Text = _settings.Token;
        _autoStartCheckBox.Checked = _settings.AutoStart;
        _globalHotkeyCheckBox.Checked = _settings.ShowGlobalHotkey;
        _showNotificationsCheckBox.Checked = _settings.ShowNotifications;
        _notifyFilterPanel.Enabled = _settings.ShowNotifications;
        
        var soundIndex = _notificationSoundComboBox.Items.IndexOf(_settings.NotificationSound);
        _notificationSoundComboBox.SelectedIndex = soundIndex >= 0 ? soundIndex : 0;

        _notifyHealthCb.Checked = _settings.NotifyHealth;
        _notifyUrgentCb.Checked = _settings.NotifyUrgent;
        _notifyReminderCb.Checked = _settings.NotifyReminder;
        _notifyEmailCb.Checked = _settings.NotifyEmail;
        _notifyCalendarCb.Checked = _settings.NotifyCalendar;
        _notifyBuildCb.Checked = _settings.NotifyBuild;
        _notifyStockCb.Checked = _settings.NotifyStock;
        _notifyInfoCb.Checked = _settings.NotifyInfo;
    }

    private void SaveSettings()
    {
        _settings.GatewayUrl = _gatewayUrlTextBox.Text.Trim();
        _settings.Token = _tokenTextBox.Text.Trim();
        _settings.AutoStart = _autoStartCheckBox.Checked;
        _settings.ShowGlobalHotkey = _globalHotkeyCheckBox.Checked;
        _settings.ShowNotifications = _showNotificationsCheckBox.Checked;
        _settings.NotificationSound = _notificationSoundComboBox.SelectedItem?.ToString() ?? "Default";
        _settings.NotifyHealth = _notifyHealthCb.Checked;
        _settings.NotifyUrgent = _notifyUrgentCb.Checked;
        _settings.NotifyReminder = _notifyReminderCb.Checked;
        _settings.NotifyEmail = _notifyEmailCb.Checked;
        _settings.NotifyCalendar = _notifyCalendarCb.Checked;
        _settings.NotifyBuild = _notifyBuildCb.Checked;
        _settings.NotifyStock = _notifyStockCb.Checked;
        _settings.NotifyInfo = _notifyInfoCb.Checked;
    }

    private async void OnTestConnection(object? sender, EventArgs e)
    {
        _testConnectionButton.Enabled = false;
        _statusLabel.Text = "Testing...";
        _statusLabel.ForeColor = Color.Blue;

        try
        {
            var testClient = new OpenClawGatewayClient(
                _gatewayUrlTextBox.Text.Trim(),
                _tokenTextBox.Text.Trim());

            await testClient.ConnectAsync();
            await testClient.DisconnectAsync();
            testClient.Dispose();

            _statusLabel.Text = "✅ Connected";
            _statusLabel.ForeColor = Color.DarkGreen;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"❌ {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
        }
        finally
        {
            _testConnectionButton.Enabled = true;
        }
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gatewayUrlTextBox.Text))
        {
            MessageBox.Show("Gateway URL is required.", "Settings", 
                          MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _gatewayUrlTextBox.Focus();
            return;
        }

        if (!GatewayUrlHelper.IsValidGatewayUrl(_gatewayUrlTextBox.Text.Trim()))
        {
            MessageBox.Show(GatewayUrlHelper.ValidationMessage, "Settings",
                          MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _gatewayUrlTextBox.Focus();
            return;
        }

        SaveSettings();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }
}

