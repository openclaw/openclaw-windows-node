using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class SettingsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private bool _initialized;
    private bool _saving;
    private bool _loading;
    private bool _localGatewayInstalled;
    private bool _uninstallInitiatedThisSession;
    private CancellationTokenSource? _uninstallCts;

    private enum UninstallUiState { Idle, InProgress, Success, Failure }

    private const string GatewayIdleBodyText =
        "Removes the WSL distro (OpenClawGateway), its disk image, autostart entry, and clears gateway credentials. Your MCP token is preserved. Onboarding will reset.";


    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Initialize()
    {
        var settings = CurrentApp.Settings;
        if (!_initialized && settings != null)
        {
            _loading = true;
            LoadSettings(settings);
            _loading = false;
            WireAutoSaveHandlers();
            _initialized = true;
        }
        else if (_initialized && settings != null)
        {
            _loading = true;
            ScreenRecordingToggle.IsOn = settings.ScreenRecordingConsentGiven;
            CameraRecordingToggle.IsOn = settings.CameraRecordingConsentGiven;
            _loading = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings != null)
            CurrentApp.Settings.Saved += OnExternalSettingsChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings != null)
            CurrentApp.Settings.Saved -= OnExternalSettingsChanged;
    }

    // ── Auto-save wiring ──

    private void WireAutoSaveHandlers()
    {
        AutoStartToggle.Toggled += (_, _) => PersistAutoStart();
        GlobalHotkeyToggle.Toggled += (_, _) => Persist(s => s.GlobalHotkeyEnabled = GlobalHotkeyToggle.IsOn);
        UseLegacyWebChatToggle.Toggled += (_, _) => Persist(s => s.UseLegacyWebChat = UseLegacyWebChatToggle.IsOn);
        NotificationsToggle.Toggled += (_, _) => Persist(s => s.ShowNotifications = NotificationsToggle.IsOn);
        NotificationSoundComboBox.SelectionChanged += (_, _) =>
        {
            if (NotificationSoundComboBox.SelectedItem is ComboBoxItem item)
                Persist(s => s.NotificationSound = item.Tag?.ToString() ?? "Default");
        };

        WireCheckBox(NotifyHealthCb, v => CurrentApp.Settings!.NotifyHealth = v);
        WireCheckBox(NotifyUrgentCb, v => CurrentApp.Settings!.NotifyUrgent = v);
        WireCheckBox(NotifyReminderCb, v => CurrentApp.Settings!.NotifyReminder = v);
        WireCheckBox(NotifyEmailCb, v => CurrentApp.Settings!.NotifyEmail = v);
        WireCheckBox(NotifyCalendarCb, v => CurrentApp.Settings!.NotifyCalendar = v);
        WireCheckBox(NotifyBuildCb, v => CurrentApp.Settings!.NotifyBuild = v);
        WireCheckBox(NotifyStockCb, v => CurrentApp.Settings!.NotifyStock = v);
        WireCheckBox(NotifyInfoCb, v => CurrentApp.Settings!.NotifyInfo = v);

        ScreenRecordingToggle.Toggled += (_, _) => Persist(s => s.ScreenRecordingConsentGiven = ScreenRecordingToggle.IsOn);
        CameraRecordingToggle.Toggled += (_, _) => Persist(s => s.CameraRecordingConsentGiven = CameraRecordingToggle.IsOn);
    }

    private void WireCheckBox(CheckBox cb, Action<bool> mutate)
    {
        RoutedEventHandler handler = (_, _) => Persist(_ => mutate(cb.IsChecked ?? false));
        cb.Checked += handler;
        cb.Unchecked += handler;
    }

    private void Persist(Action<SettingsManager> mutate)
    {
        if (_loading || CurrentApp.Settings == null) return;
        _saving = true;
        try
        {
            mutate(CurrentApp.Settings);
            CurrentApp.Settings.Save();
            ((IAppCommands)CurrentApp).NotifySettingsSaved();
            ShowSavedIndicator();
        }
        finally
        {
            _saving = false;
        }
    }

    private void PersistAutoStart() =>
        AsyncEventHandlerGuard.Run(
            PersistAutoStartAsync,
            new OpenClawTray.AppLogger(),
            nameof(PersistAutoStart));

    private async Task PersistAutoStartAsync()
    {
        if (_loading || CurrentApp.Settings == null) return;
        _saving = true;
        try
        {
            CurrentApp.Settings.AutoStart = AutoStartToggle.IsOn;
            CurrentApp.Settings.Save();
            await AutoStartManager.SetAutoStartAsync(CurrentApp.Settings.AutoStart);
            ((IAppCommands)CurrentApp).NotifySettingsSaved();
            ShowSavedIndicator();
        }
        finally
        {
            _saving = false;
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _savedIndicatorTimer;
    private void ShowSavedIndicator()
    {
        SavedInfoBar.IsOpen = true;
        if (_savedIndicatorTimer == null)
        {
            _savedIndicatorTimer = DispatcherQueue.CreateTimer();
            _savedIndicatorTimer.Interval = TimeSpan.FromSeconds(1.5);
            _savedIndicatorTimer.Tick += (t, _) => { SavedInfoBar.IsOpen = false; t.Stop(); };
        }
        _savedIndicatorTimer.Stop();
        _savedIndicatorTimer.Start();
    }

    private void OnExternalSettingsChanged(object? sender, EventArgs e)
    {
        if (CurrentApp.Settings == null || _saving) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _loading = true;
            try
            {
                LoadSettings(CurrentApp.Settings);
            }
            finally
            {
                _loading = false;
            }
        });
    }

    private void LoadSettings(SettingsManager settings)
    {
        AutoStartToggle.IsOn = settings.AutoStart;
        GlobalHotkeyToggle.IsOn = settings.GlobalHotkeyEnabled;
        UseLegacyWebChatToggle.IsOn = settings.UseLegacyWebChat;
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
        var setupStatePath = Path.Combine(SetupExistingGatewayClassifier.ResolveLocalDataPath(), "setup-state.json");
        var activeGatewayAccess = GatewayHostAccessClassifier.Classify(CurrentApp.Registry?.GetActive());

        _localGatewayInstalled = File.Exists(setupStatePath)
            || (settings.GatewayUrl?.StartsWith("ws://localhost", StringComparison.OrdinalIgnoreCase) == true);

        OpenClawOnboardCard.Visibility = activeGatewayAccess.CanControlWslGateway
            ? Visibility.Visible : Visibility.Collapsed;
        LocalGatewayExpander.Visibility = ComputeLocalGatewaySectionVisibility();

        // MSIX warning: Path A (conservative) — show when packaged AND gateway installed.
        MsixWarningBar.IsOpen = PackageHelper.IsPackaged && _localGatewayInstalled;
    }

    /// <summary>
    /// Returns Visible for the installed-gateway management card when a local gateway exists
    /// OR an uninstall has been initiated this view session (latch). The latch prevents the
    /// card from collapsing mid-flight when
    /// the engine deletes setup-state.json before the result InfoBar is shown.
    /// Resets on page navigation — the card hides again on clean Settings re-open.
    /// </summary>
    private Visibility ComputeLocalGatewaySectionVisibility()
    {
        return (_localGatewayInstalled || _uninstallInitiatedThisSession)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenLocalGatewaySetup(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).ShowOnboarding();
    }

    private void OnOpenGatewayWizard(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).ShowGatewayWizard();
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
        catch (Exception ex)
        {
            Logger.Warn($"SettingsPage: Test notification failed: {ex.Message}");
        }
    }

    private void OnRemoveGateway(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnRemoveGatewayAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnRemoveGateway));

    private async Task OnRemoveGatewayAsync()
    {
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

        _uninstallInitiatedThisSession = true;
        LocalGatewayExpander.Visibility = ComputeLocalGatewaySectionVisibility();

        ApplyUninstallUiState(UninstallUiState.InProgress);
        UninstallResultBar.IsOpen = false;

        _uninstallCts = new CancellationTokenSource();
        Process? proc = null;
        string? jsonOutput = null;
        try
        {
            var exePath = ResolveCurrentExecutablePath()
                ?? throw new FileNotFoundException("OpenClaw tray executable could not be resolved for local gateway removal.");

            jsonOutput = Path.Combine(Path.GetTempPath(), $"openclaw-uninstall-{Guid.NewGuid():N}.json");

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--uninstall");
            psi.ArgumentList.Add("--confirm-destructive");
            psi.ArgumentList.Add("--json-output");
            psi.ArgumentList.Add(jsonOutput);

            proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start OpenClaw uninstall process.");
            await proc.WaitForExitAsync(_uninstallCts.Token);

            if (proc.ExitCode == 0)
            {
                CurrentApp.Registry?.Load();
                OpenClawOnboardCard.Visibility = Visibility.Collapsed;
                ApplyUninstallUiState(UninstallUiState.Success);
                UninstallResultBar.Severity = InfoBarSeverity.Success;
                UninstallResultBar.Title = "Local gateway removed";
                UninstallResultBar.Message = "Setup is reset; you can re-run setup from the tray menu.";
                UninstallResultBar.ActionButton = null;
                UninstallResultBar.IsOpen = true;
            }
            else
            {
                ApplyUninstallUiState(UninstallUiState.Failure);
                var errorMsg = "Removal completed with errors. Check logs for details.";
                if (File.Exists(jsonOutput))
                {
                    try
                    {
                        var json = File.ReadAllText(jsonOutput);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } m)
                            errorMsg = m;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"SettingsPage: Failed to parse uninstall result JSON '{jsonOutput}': {ex.Message}");
                    }
                }
                ShowUninstallError(errorMsg);
            }

            // Clean up temp file
            try { if (File.Exists(jsonOutput)) File.Delete(jsonOutput); }
            catch (Exception ex) { Logger.Warn($"SettingsPage: Failed to delete uninstall result file '{jsonOutput}': {ex.Message}"); }
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (proc is { HasExited: false })
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SettingsPage: Failed to stop uninstall process during cancellation: {ex.Message}");
            }

            ApplyUninstallUiState(UninstallUiState.Failure);
            UninstallResultBar.Severity = InfoBarSeverity.Warning;
            UninstallResultBar.Title = "Removal cancelled";
            UninstallResultBar.Message = "Gateway may be in a partially-removed state. Review logs or retry.";
            UninstallResultBar.ActionButton = null;
            UninstallResultBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SettingsPage: gateway uninstall failed: {ex}");
            ApplyUninstallUiState(UninstallUiState.Failure);
            ShowUninstallError(ex.Message);
        }
        finally
        {
            proc?.Dispose();
            try { if (jsonOutput is not null && File.Exists(jsonOutput)) File.Delete(jsonOutput); }
            catch (Exception ex) { Logger.Warn($"SettingsPage: Failed to delete uninstall result file '{jsonOutput}': {ex.Message}"); }
            _uninstallCts?.Dispose();
            _uninstallCts = null;
        }
    }

    private static string? ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            return Environment.ProcessPath;

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
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
            try { System.Diagnostics.Process.Start("explorer.exe", logsPath); }
            catch (Exception ex) { Logger.Warn($"SettingsPage: Failed to open logs folder '{logsPath}': {ex.Message}"); }
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
                RemoveGatewayButton.Visibility = Visibility.Collapsed;
                MsixWarningBar.IsOpen = false;
                break;
        }
    }
}
