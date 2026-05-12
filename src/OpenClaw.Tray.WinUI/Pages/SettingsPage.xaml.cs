using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Services.LocalGatewaySetup;
using OpenClawTray.Windows;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class SettingsPage : Page
{
    private HubWindow? _hub;
    private bool _initialized;
    private bool _saving;
    private bool _isDirty;
    private bool _localGatewayInstalled;
    private bool _uninstallInitiatedThisSession;
    private CancellationTokenSource? _uninstallCts;

    private enum UninstallUiState { Idle, InProgress, Success, Failure }

    private const string GatewayIdleBodyText =
        "Removes the WSL distro (OpenClawGateway), its disk image, autostart entry, and clears gateway credentials. Your MCP token is preserved. Onboarding will reset.";


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
            hub.Settings.Saved += OnExternalSettingsChanged;
            RegisterDirtyHandlers();
            _initialized = true;
        }
        else if (_initialized && hub.Settings != null)
        {
            ScreenRecordingToggle.IsOn = hub.Settings.ScreenRecordingConsentGiven;
            CameraRecordingToggle.IsOn = hub.Settings.CameraRecordingConsentGiven;
        }
    }

    private void RegisterDirtyHandlers()
    {
        void MarkDirty(object s, RoutedEventArgs e) { if (_initialized) _isDirty = true; }

        AutoStartToggle.Toggled += MarkDirty;
        GlobalHotkeyToggle.Toggled += MarkDirty;
        NotificationsToggle.Toggled += MarkDirty;
        ScreenRecordingToggle.Toggled += MarkDirty;
        CameraRecordingToggle.Toggled += MarkDirty;
        NotificationSoundComboBox.SelectionChanged += (s, e) => { if (_initialized) _isDirty = true; };
        NotifyHealthCb.Checked += MarkDirty; NotifyHealthCb.Unchecked += MarkDirty;
        NotifyUrgentCb.Checked += MarkDirty; NotifyUrgentCb.Unchecked += MarkDirty;
        NotifyReminderCb.Checked += MarkDirty; NotifyReminderCb.Unchecked += MarkDirty;
        NotifyEmailCb.Checked += MarkDirty; NotifyEmailCb.Unchecked += MarkDirty;
        NotifyCalendarCb.Checked += MarkDirty; NotifyCalendarCb.Unchecked += MarkDirty;
        NotifyBuildCb.Checked += MarkDirty; NotifyBuildCb.Unchecked += MarkDirty;
        NotifyStockCb.Checked += MarkDirty; NotifyStockCb.Unchecked += MarkDirty;
        NotifyInfoCb.Checked += MarkDirty; NotifyInfoCb.Unchecked += MarkDirty;
    }

    private void OnExternalSettingsChanged(object? sender, EventArgs e)
    {
        if (_hub?.Settings == null || _saving || _isDirty) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            ScreenRecordingToggle.IsOn = _hub.Settings.ScreenRecordingConsentGiven;
            CameraRecordingToggle.IsOn = _hub.Settings.CameraRecordingConsentGiven;

            // Show that the change is already persisted
            SaveButton.Content = "✓ Saved";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (t, a) => { SaveButton.Content = "Save"; timer.Stop(); };
            timer.Start();
        });
    }

    private void LoadSettings(SettingsManager settings)
    {
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

        ScreenRecordingToggle.IsOn = settings.ScreenRecordingConsentGiven;
        CameraRecordingToggle.IsOn = settings.CameraRecordingConsentGiven;
        LoadGatewaySection(settings);
    }

    private void LoadGatewaySection(SettingsManager settings)
    {
        var localDataRoot = Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
        var setupStatePath = Path.Combine(localDataRoot, "setup-state.json");

        _localGatewayInstalled = File.Exists(setupStatePath)
            || (settings.GatewayUrl?.StartsWith("ws://localhost", StringComparison.OrdinalIgnoreCase) == true);

        LocalGatewayExpander.Visibility = ComputeLocalGatewaySectionVisibility();

        // MSIX warning: Path A (conservative) — show when packaged AND gateway installed.
        // TODO(commit-5): soften copy if Bostick's MSIX test confirms Path B (package-virtualized APPDATA).
        MsixWarningBar.IsOpen = PackageHelper.IsPackaged && _localGatewayInstalled;
    }

    /// <summary>
    /// Returns Visible when a local gateway exists OR an uninstall has been initiated this
    /// view session (latch). The latch prevents the section from collapsing mid-flight when
    /// the engine deletes setup-state.json before the result InfoBar is shown.
    /// Resets on page navigation — section hides again on clean Settings re-open.
    /// </summary>
    private Visibility ComputeLocalGatewaySectionVisibility() =>
        (_localGatewayInstalled || _uninstallInitiatedThisSession)
            ? Visibility.Visible : Visibility.Collapsed;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;

        var s = _hub.Settings;
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

        s.ScreenRecordingConsentGiven = ScreenRecordingToggle.IsOn;
        s.CameraRecordingConsentGiven = CameraRecordingToggle.IsOn;

        _saving = true;
        s.Save();
        _saving = false;
        _isDirty = false;
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
            _isDirty = false;
        }
    }

    private void OnTestNotification(object sender, RoutedEventArgs e)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("Test Notification")
                .AddText("This is a test notification from OpenClaw settings.")
                .Show();
        }
        catch { }
    }

    private async void OnRemoveGateway(object sender, RoutedEventArgs e)
    {
        // Build confirmation dialog content
        var dialogContent = new StackPanel { Spacing = 8 };
        dialogContent.Children.Add(new TextBlock
        {
            Text = "This will permanently remove the following:",
            TextWrapping = TextWrapping.Wrap
        });
        dialogContent.Children.Add(new TextBlock
        {
            Text = "• WSL distro: OpenClawGateway (and its disk image)\n" +
                   "• Autostart registry entry\n" +
                   "• Gateway credentials (token and bootstrap token cleared)\n" +
                   "• Setup state (onboarding will reset)",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        dialogContent.Children.Add(new TextBlock
        {
            Text = "Preserved: Your MCP token and root device key are NOT deleted.\n" +
                   "Removed: Local gateway identity credentials and registry records.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        dialogContent.Children.Add(new TextBlock
        {
            Text = "This cannot be undone.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 0),
            Opacity = 0.7
        });

        var dialog = new ContentDialog
        {
            Title = "Remove Local Gateway?",
            Content = dialogContent,
            PrimaryButtonText = "Remove Local Gateway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary) return;

        // Latch: keeps the section visible even after setup-state.json is deleted by the engine.
        _uninstallInitiatedThisSession = true;
        LocalGatewayExpander.Visibility = ComputeLocalGatewaySectionVisibility();

        // --- Begin uninstall ---
        ApplyUninstallUiState(UninstallUiState.InProgress);
        UninstallResultBar.IsOpen = false;

        _uninstallCts = new CancellationTokenSource();
        try
        {
            if (_hub?.Settings == null)
                throw new InvalidOperationException("Settings not available.");

            var engine = LocalGatewayUninstall.Build(_hub.Settings, registry: _hub.GatewayRegistry);
            var uninstallResult = await engine.RunAsync(
                new LocalGatewayUninstallOptions
                {
                    DryRun = false,
                    ConfirmDestructive = true
                },
                _uninstallCts.Token);

            if (uninstallResult.Success)
            {
                ApplyUninstallUiState(UninstallUiState.Success);
                UninstallResultBar.Severity = InfoBarSeverity.Success;
                UninstallResultBar.Title = "Local gateway removed";
                UninstallResultBar.Message = "Setup is reset; you can re-run the wizard from Onboarding.";
                UninstallResultBar.ActionButton = null;
                UninstallResultBar.IsOpen = true;
            }
            else
            {
                ApplyUninstallUiState(UninstallUiState.Failure);
                var errorSummary = uninstallResult.Errors.Count > 0
                    ? string.Join("; ", uninstallResult.Errors)
                    : "Removal completed with unknown errors. Check logs for details.";
                ShowUninstallError(errorSummary);
            }
        }
        catch (OperationCanceledException)
        {
            ApplyUninstallUiState(UninstallUiState.Failure);
            UninstallResultBar.Severity = InfoBarSeverity.Warning;
            UninstallResultBar.Title = "Removal cancelled";
            UninstallResultBar.Message = "Gateway may be in a partially-removed state. Review logs or retry.";
            UninstallResultBar.ActionButton = null;
            UninstallResultBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ApplyUninstallUiState(UninstallUiState.Failure);
            ShowUninstallError(ex.Message);
        }
        finally
        {
            _uninstallCts?.Dispose();
            _uninstallCts = null;
        }
    }

    private void ShowUninstallError(string message)
    {
        var logsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray", "Logs");

        var viewLogsButton = new Button { Content = "View Logs" };
        viewLogsButton.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", logsPath); } catch { }
        };

        UninstallResultBar.Severity = InfoBarSeverity.Error;
        UninstallResultBar.Title = "Removal failed";
        UninstallResultBar.Message = message;
        UninstallResultBar.ActionButton = viewLogsButton;
        UninstallResultBar.IsOpen = true;
    }

    private void ApplyUninstallUiState(UninstallUiState state)
    {
        switch (state)
        {
            case UninstallUiState.Idle:
            case UninstallUiState.Failure:
                RemoveGatewayButton.Content = "Remove Local Gateway";
                RemoveGatewayButton.IsEnabled = true;
                RemoveGatewayButton.Visibility = Visibility.Visible;
                GatewayBodyText.Text = GatewayIdleBodyText;
                break;

            case UninstallUiState.InProgress:
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                sp.Children.Add(new ProgressRing
                {
                    IsActive = true,
                    Width = 16,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = "Removing distro\u2026",
                    VerticalAlignment = VerticalAlignment.Center
                });
                RemoveGatewayButton.Content = sp;
                RemoveGatewayButton.IsEnabled = false;
                RemoveGatewayButton.Visibility = Visibility.Visible;
                GatewayBodyText.Text = "Removing the local gateway. This may take 10\u201330 seconds\u2026";
                break;
            }

            case UninstallUiState.Success:
                // Button hidden — success InfoBar carries the message.
                // MSIX warning is moot once the gateway is gone.
                RemoveGatewayButton.Visibility = Visibility.Collapsed;
                MsixWarningBar.IsOpen = false;
                break;
        }
    }
}
