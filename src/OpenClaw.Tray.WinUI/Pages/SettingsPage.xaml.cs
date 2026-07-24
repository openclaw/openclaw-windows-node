using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class SettingsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private SettingsPageViewModel? _viewModel;
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

    private static string GatewayIdleBodyText =>
        $"Removes the WSL distro ({AppIdentity.SetupDistroName}), its disk image, autostart entry, and clears gateway credentials. Your MCP token is preserved. Onboarding will reset.";


    public SettingsPage()
    {
        InitializeComponent();
        LocalGatewaySetupDescriptionText.Text =
            $"Launches setup to install the app-owned {AppIdentity.SetupDistroName} WSL distro or re-run provider and model setup for an existing one. Existing local gateways are only replaced after confirmation.";
        GatewayBodyText.Text = GatewayIdleBodyText;
        _gatewayUptimeRefreshTimer.Tick += OnGatewayUptimeRefreshTimerTick;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    public void Initialize()
    {
        PopulateAppInfo();
        InitializeGatewayInfo();
        if (CurrentApp.Settings is { } settings)
            LoadGatewaySection(settings);
    }

    /// <summary>
    /// The Settings view model is assigned as the page DataContext by the navigation activation
    /// hook. The two-way bindings handle load/persist; the page only subscribes to the view-only
    /// side effects it applies on the UI thread: the saved indicator, and refreshing the
    /// view-owned gateway section when settings change externally.
    /// </summary>
    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel != null)
        {
            _viewModel.SavedIndicated -= OnViewModelSavedIndicated;
            _viewModel.ExternalChanged -= OnViewModelExternalChanged;
        }

        _viewModel = args.NewValue as SettingsPageViewModel;

        if (_viewModel != null)
        {
            _viewModel.SavedIndicated += OnViewModelSavedIndicated;
            _viewModel.ExternalChanged += OnViewModelExternalChanged;
        }
    }

    private void OnViewModelSavedIndicated(object? sender, EventArgs e) => ShowSavedIndicator();

    /// <summary>
    /// The gateway management section is view-owned (not settings-bound), so it must be refreshed
    /// when settings change from another source, matching the page's previous live-refresh behavior.
    /// </summary>
    private void OnViewModelExternalChanged(object? sender, EventArgs e)
    {
        if (CurrentApp.Settings is { } settings)
            LoadGatewaySection(settings);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appState != null)
            _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
        _gatewayUptimeRefreshTimer.Stop();
    }

    // ── Saved indicator ──

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

    private void PopulateAppInfo()
    {
        AppInfoVersionText.Text = AppVersionInfo.DisplayVersion;
        var windowsAppSdk = SettingsAppInfoProjection.ResolveWindowsAppSdkDisplayName(
            Assembly.GetEntryAssembly()?.GetName().Name, AppContext.BaseDirectory);
        AppInfoRuntimeText.Text = SettingsAppInfoProjection.BuildRuntimeStack(
            RuntimeInformation.FrameworkDescription, ResolveWinUiDisplayName(), windowsAppSdk);
        AppInfoArchText.Text = RuntimeInformation.ProcessArchitecture.ToString();
        AppInfoWindowsText.Text = Environment.OSVersion.Version.ToString();
        AppInfoInstallText.Text = SettingsAppInfoProjection.InstallKind(PackageHelper.IsPackaged);
        AppInfoChannelText.Text = SettingsAppInfoProjection.ResolveUpdateChannel(
            Environment.GetEnvironmentVariable("OPENCLAW_UPDATE_CHANNEL"));

        var buildDate = SettingsAppInfoProjection.FormatBuildDate(
            Assembly.GetEntryAssembly()?.Location, CultureInfo.CurrentCulture);
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
        GatewayUptimeText.Text = SettingsAppInfoProjection.FormatDuration(
            TimeSpan.FromMilliseconds(_sampledGatewayUptimeMs.Value + elapsedMs));
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

    private static string ResolveWinUiDisplayName()
    {
        var version = typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version;
        return version is { Major: > 0 }
            ? $"WinUI {version.Major}"
            : "WinUI";
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
            Text = $"• WSL distro: {AppIdentity.SetupDistroName} (and its disk image)\n" +
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
