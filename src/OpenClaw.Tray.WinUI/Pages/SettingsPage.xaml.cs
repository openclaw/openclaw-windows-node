using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private AppState? _appState;
    private readonly DispatcherTimer _gatewayUptimeRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long? _sampledGatewayUptimeMs;
    private DateTime _sampledGatewayUptimeUtc;

    private const string DocumentationUrl = "https://docs.openclaw.ai/platforms/windows";
    private const string GitHubUrl = "https://github.com/openclaw/openclaw-windows-node";

    private enum UninstallUiState { Idle, InProgress, Success, Failure }

    private string GatewayIdleBodyText => GetGatewayIdleBodyText(DetectInstalledGatewayMode());


    public SettingsPage()
    {
        InitializeComponent();
        LocalGatewaySetupDescriptionText.Text =
            "Launches setup to install or reconfigure a native Windows or WSL local gateway. Existing local gateways are only replaced after confirmation.";
        GatewayBodyText.Text = GatewayIdleBodyText;
        _gatewayUptimeRefreshTimer.Tick += OnGatewayUptimeRefreshTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Initialize()
    {
        PopulateAppInfo();
        InitializeGatewayInfo();

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
        if (_appState != null)
            _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
        _gatewayUptimeRefreshTimer.Stop();
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
        AppThemeComboBox.SelectionChanged += (_, _) =>
        {
            if (AppThemeComboBox.SelectedItem is ComboBoxItem item)
                Persist(s => s.AppTheme = item.Tag?.ToString() ?? SettingsManager.AppThemeSystem);
        };
        ShowDiagnosticsToggle.Toggled += (_, _) => Persist(s => s.ShowDiagnosticsOverride = ShowDiagnosticsToggle.IsOn);

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

        SelectComboBoxItemByTag(NotificationSoundComboBox, settings.NotificationSound);
        SelectComboBoxItemByTag(AppThemeComboBox, settings.AppTheme);
        ShowDiagnosticsToggle.IsOn = settings.ShowDiagnosticsEffective;

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

    private void PopulateAppInfo()
    {
        AppInfoVersionText.Text = AppVersionInfo.DisplayVersion;
        AppInfoRuntimeText.Text = BuildRuntimeStackDisplayText();
        AppInfoArchText.Text = RuntimeInformation.ProcessArchitecture.ToString();
        AppInfoWindowsText.Text = Environment.OSVersion.Version.ToString();
        AppInfoInstallText.Text = PackageHelper.IsPackaged ? "Packaged (MSIX)" : "Unpackaged (developer)";
        AppInfoChannelText.Text = ResolveUpdateChannelDisplayText();

        var buildDate = TryResolveBuildDateDisplayText();
        if (string.IsNullOrWhiteSpace(buildDate))
        {
            AppInfoBuildLabel.Visibility = Visibility.Collapsed;
            AppInfoBuildText.Visibility = Visibility.Collapsed;
            AppInfoBuildText.Text = string.Empty;
        }
        else
        {
            AppInfoBuildLabel.Visibility = Visibility.Visible;
            AppInfoBuildText.Visibility = Visibility.Visible;
            AppInfoBuildText.Text = buildDate;
        }
    }

    private void InitializeGatewayInfo()
    {
        var appState = CurrentApp.AppState;
        if (!ReferenceEquals(_appState, appState))
        {
            if (_appState != null)
                _appState.PropertyChanged -= OnAppStateChanged;
            _appState = appState;
            if (_appState != null)
                _appState.PropertyChanged += OnAppStateChanged;
        }

        RefreshGatewayInfo();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Status) or nameof(AppState.GatewaySelf))
            RefreshGatewayInfo();
    }

    private void RefreshGatewayInfo()
    {
        var self = CurrentApp.AppState?.GatewaySelf;
        if (CurrentApp.AppState?.Status == ConnectionStatus.Connected && self != null)
        {
            GatewayVersionText.Text = self.VersionText;
            GatewayProtocolText.Text = self.Protocol.HasValue ? $"v{self.Protocol}" : "unknown";
            GatewayAuthText.Text = string.IsNullOrWhiteSpace(self.AuthMode) ? "unknown" : self.AuthMode;
            CaptureGatewayUptimeSample(self);
            RefreshGatewayUptimeText();
        }
        else
        {
            _sampledGatewayUptimeMs = null;
            _gatewayUptimeRefreshTimer.Stop();
            GatewayVersionText.Text = "—";
            GatewayProtocolText.Text = "—";
            GatewayAuthText.Text = "—";
            GatewayUptimeText.Text = "—";
        }
    }

    private void CaptureGatewayUptimeSample(GatewaySelfInfo self)
    {
        if (!self.UptimeMs.HasValue)
        {
            _sampledGatewayUptimeMs = null;
            _gatewayUptimeRefreshTimer.Stop();
            return;
        }

        if (_sampledGatewayUptimeMs != self.UptimeMs.Value)
        {
            _sampledGatewayUptimeMs = self.UptimeMs.Value;
            _sampledGatewayUptimeUtc = DateTime.UtcNow;
        }

        if (!_gatewayUptimeRefreshTimer.IsEnabled)
            _gatewayUptimeRefreshTimer.Start();
    }

    private void OnGatewayUptimeRefreshTimerTick(object? sender, object e)
    {
        RefreshGatewayUptimeText();
    }

    private void RefreshGatewayUptimeText()
    {
        if (CurrentApp.AppState?.Status != ConnectionStatus.Connected ||
            !_sampledGatewayUptimeMs.HasValue)
        {
            _gatewayUptimeRefreshTimer.Stop();
            GatewayUptimeText.Text = "—";
            return;
        }

        var elapsedMs = Math.Max(0, (DateTime.UtcNow - _sampledGatewayUptimeUtc).TotalMilliseconds);
        GatewayUptimeText.Text = FormatDuration(TimeSpan.FromMilliseconds(_sampledGatewayUptimeMs.Value + elapsedMs));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    private static void SelectComboBoxItemByTag(ComboBox comboBox, string? tag)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void LoadGatewaySection(SettingsManager settings)
    {
        var setupStatePath = Path.Combine(SetupExistingGatewayClassifier.ResolveLocalDataPath(), "setup-state.json");
        var activeGateway = CurrentApp.Registry?.GetActive();

        _localGatewayInstalled = File.Exists(setupStatePath)
            || (settings.GatewayUrl?.StartsWith("ws://localhost", StringComparison.OrdinalIgnoreCase) == true);

        OpenClawOnboardCard.Visibility = SetupExistingGatewayClassifier.IsSetupManagedLocalGateway(activeGateway)
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

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).CheckForUpdates();
    }

    private void OnDocumentationLink(object sender, RoutedEventArgs e)
    {
        OpenShellTarget(DocumentationUrl, "documentation");
    }

    private void OnGitHubLink(object sender, RoutedEventArgs e)
    {
        OpenShellTarget(GitHubUrl, "GitHub");
    }

    private void OnDashboardLink(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).OpenDashboard(null);
    }

    private static void OpenShellTarget(string target, string label)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Logger.Warn($"Failed to open {label}: target is empty");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Logger.Warn($"Failed to open {label}: {ex.Message}");
        }
    }

    private static string BuildRuntimeStackDisplayText()
    {
        var dotNet = RuntimeInformation.FrameworkDescription;
        var winUi = ResolveWinUiDisplayName();
        var windowsAppSdk = ResolveWindowsAppSdkDisplayName();

        return $"{dotNet} / {winUi} / {windowsAppSdk}";
    }

    private static string ResolveUpdateChannelDisplayText()
    {
        var channel = Environment.GetEnvironmentVariable("OPENCLAW_UPDATE_CHANNEL");
        return string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim();
    }

    private static string? TryResolveBuildDateDisplayText()
    {
        try
        {
            var location = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
                return null;

            return File.GetLastWriteTime(location).ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Logger.Debug($"SettingsPage: Failed to resolve app build date: {ex.Message}");
            return null;
        }
    }

    private static string ResolveWinUiDisplayName()
    {
        var version = typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version;
        return version is { Major: > 0 }
            ? $"WinUI {version.Major}"
            : "WinUI";
    }

    private static string ResolveWindowsAppSdkDisplayName()
    {
        if (TryResolveWindowsAppSdkPackageVersionFromDeps() is { Length: > 0 } packageVersion)
        {
            return $"Windows App SDK {packageVersion}";
        }

        return ResolveWindowsAppSdkDisplayNameFromFileVersion();
    }

    private static string ResolveWindowsAppSdkDisplayNameFromFileVersion()
    {
        var xamlNativePath = Path.Combine(AppContext.BaseDirectory, "Microsoft.ui.xaml.dll");
        if (File.Exists(xamlNativePath))
        {
            try
            {
                var productVersion = FileVersionInfo.GetVersionInfo(xamlNativePath).ProductVersion;
                if (!string.IsNullOrWhiteSpace(productVersion))
                {
                    return $"Windows App SDK {StripBuildMetadata(productVersion)}";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Logger.Warn($"Failed to read Windows App SDK version from {xamlNativePath}: {ex.Message}");
            }
        }

        return "Windows App SDK";
    }

    private static string? TryResolveWindowsAppSdkPackageVersionFromDeps()
    {
        var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            return null;

        var depsPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.deps.json");
        if (!File.Exists(depsPath))
            return null;

        try
        {
            using var stream = File.OpenRead(depsPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                const string packagePrefix = "Microsoft.WindowsAppSDK/";
                if (library.Name.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return StripBuildMetadata(library.Name[packagePrefix.Length..]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Logger.Warn($"Failed to read Windows App SDK package version from {depsPath}: {ex.Message}");
        }

        return null;
    }

    private static string StripBuildMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    private void OnRemoveGateway(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnRemoveGatewayAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnRemoveGateway));

    private async Task OnRemoveGatewayAsync()
    {
        var installedMode = DetectInstalledGatewayMode();
        var dialogContent = new StackPanel { Spacing = 8 };
        dialogContent.Children.Add(new TextBlock
        {
            Text = "This will permanently remove the following:",
            TextWrapping = TextWrapping.Wrap
        });
        dialogContent.Children.Add(new TextBlock
        {
            Text = GetGatewayRemovalDetails(installedMode),
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
                UninstallResultBar.Title = LocalizationHelper.GetString("SettingsPage_LocalGatewayRemovedTitle");
                UninstallResultBar.Message = LocalizationHelper.GetString("SettingsPage_LocalGatewayRemovedMessage");
                UninstallResultBar.ActionButton = null;
                UninstallResultBar.IsOpen = true;
            }
            else
            {
                ApplyUninstallUiState(UninstallUiState.Failure);
                var errorMsg = LocalizationHelper.GetString("SettingsPage_LocalGatewayRemovalErrorsMessage");
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
            UninstallResultBar.Title = LocalizationHelper.GetString("SettingsPage_LocalGatewayRemovalCancelledTitle");
            UninstallResultBar.Message = LocalizationHelper.GetString("SettingsPage_LocalGatewayRemovalCancelledMessage");
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

    private GatewayInstallMode? DetectInstalledGatewayMode()
    {
        try
        {
            return GatewayInstallModeDetector.DetectInstalled(
                AppIdentity.ResolveRoamingDataDirectory(),
                AppIdentity.ResolveSetupLocalDataDirectory(),
                GatewayInstallMode.Wsl);
        }
        catch (Exception ex)
        {
            Logger.Warn($"SettingsPage: Could not detect installed gateway mode: {ex.Message}");
            return null;
        }
    }

    internal static string GetGatewayIdleBodyText(GatewayInstallMode? mode) => mode switch
    {
        GatewayInstallMode.NativeWindows =>
            "Removes the native Windows gateway service, its managed startup files, and gateway credentials. Your MCP token is preserved. Onboarding will reset.",
        GatewayInstallMode.Wsl =>
            $"Removes the WSL distro ({AppIdentity.SetupDistroName}), its disk image, autostart entry, and gateway credentials. Your MCP token is preserved. Onboarding will reset.",
        _ =>
            "Removes the managed local gateway runtime, startup files, and gateway credentials. Your MCP token is preserved. Onboarding will reset.",
    };

    internal static string GetGatewayRemovalDetails(GatewayInstallMode? mode) => mode switch
    {
        GatewayInstallMode.NativeWindows =>
            "• Native Windows gateway service and managed startup files\n" +
            "• Setup-managed gateway configuration and credential\n" +
            "• Local gateway identity credentials and registry records\n" +
            "• Setup state (onboarding will reset)",
        GatewayInstallMode.Wsl =>
            $"• WSL distro: {AppIdentity.SetupDistroName} (and its disk image)\n" +
            "• Autostart registry entry\n" +
            "• Gateway credentials (token and bootstrap token cleared)\n" +
            "• Setup state (onboarding will reset)",
        _ =>
            "• Managed local gateway runtime and startup files\n" +
            "• Gateway credentials and local identity records\n" +
            "• Setup state (onboarding will reset)",
    };

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
        var logsPath = Path.Combine(AppIdentity.ResolveLocalDataDirectory(), "Logs");

        var viewLogsButton = new Button { Content = LocalizationHelper.GetString("SettingsPage_ViewLogs") };
        viewLogsButton.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", logsPath); }
            catch (Exception ex) { Logger.Warn($"SettingsPage: Failed to open logs folder '{logsPath}': {ex.Message}"); }
        };

        UninstallResultBar.Severity = InfoBarSeverity.Error;
        UninstallResultBar.Title = LocalizationHelper.GetString("SettingsPage_LocalGatewayRemovalFailedTitle");
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
                RemoveGatewayButton.Content = LocalizationHelper.GetString("SettingsPage_RemoveLocalGatewayButton");
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
                    Text = LocalizationHelper.GetString("SettingsPage_RemovingDistro"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                RemoveGatewayButton.Content = sp;
                RemoveGatewayButton.IsEnabled = false;
                RemoveGatewayButton.Visibility = Visibility.Visible;
                GatewayBodyText.Text = LocalizationHelper.GetString("SettingsPage_RemovingLocalGatewayMessage");
                break;
            }

            case UninstallUiState.Success:
                RemoveGatewayButton.Visibility = Visibility.Collapsed;
                MsixWarningBar.IsOpen = false;
                break;
        }
    }
}
