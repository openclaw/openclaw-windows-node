using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class AboutPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private readonly DispatcherTimer _gatewayUptimeRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppState? _appState;
    private long? _sampledGatewayUptimeMs;
    private DateTime _sampledGatewayUptimeUtc;

    private const string DocumentationUrl = "https://docs.openclaw.ai/platforms/windows";
    private const string GitHubUrl = "https://github.com/openclaw/openclaw-windows-node";

    public AboutPage()
    {
        InitializeComponent();
        _gatewayUptimeRefreshTimer.Tick += OnGatewayUptimeRefreshTimerTick;
        Unloaded += OnUnloaded;
    }

    public void Initialize()
    {
        StoreMigrationAction.Content = LocalizationHelper.GetString("Migration2_InnoAction");
        StoreMigrationCard.Visibility = InnoMigrationHandoff.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        PopulateAppInfo();
        InitializeGatewayInfo();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appState != null)
            _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
        _gatewayUptimeRefreshTimer.Stop();
    }

    private void OnStoreMigration(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(OnStoreMigrationAsync, new AppLogger(), nameof(OnStoreMigration));

    private async Task OnStoreMigrationAsync()
    {
        StoreMigrationAction.IsEnabled = false;
        try
        {
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = LocalizationHelper.GetString("Migration2_InnoConsentTitle"),
                Content = LocalizationHelper.GetString("Migration2_InnoConsent"),
                PrimaryButtonText = LocalizationHelper.GetString("Migration2_InnoAction"),
                CloseButtonText = LocalizationHelper.GetString("Migration_StoreNotNow"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                return;
            var messageKey = await InnoMigrationHandoff.GrantAndLaunchAsync();
            ShowStoreMigrationStatus(messageKey, messageKey == "Migration2_InnoGranted"
                ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                canDownloadSetup: messageKey == "Migration2_InnoRepairRequired");
        }
        catch (Exception exception)
        {
            Logger.Error($"Could not show migration consent: {exception.Message}");
            ShowStoreMigrationStatus("Migration2_InnoFailed", InfoBarSeverity.Error);
        }
        finally
        {
            StoreMigrationAction.IsEnabled = true;
        }
    }

    private void OnDownloadMigrationSetup(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(DownloadMigrationSetupAsync, new AppLogger(), nameof(OnDownloadMigrationSetup));

    private async Task DownloadMigrationSetupAsync()
    {
        StoreMigrationDownloadSetup.IsEnabled = false;
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(InnoMigrationHandoff.InstallerUri))
                throw new InvalidOperationException("Windows declined the installer download URI.");
        }
        catch (Exception exception)
        {
            Logger.Error($"Could not open migration Setup download: {exception.Message}");
            ShowStoreMigrationStatus("Migration2_InnoSetupLinkFailed", InfoBarSeverity.Error,
                canDownloadSetup: true, address: InnoMigrationHandoff.InstallerUri.AbsoluteUri);
        }
        finally
        {
            StoreMigrationDownloadSetup.IsEnabled = true;
        }
    }

    private void ShowStoreMigrationStatus(
        string messageKey,
        InfoBarSeverity severity,
        bool canDownloadSetup = false,
        string? address = null)
    {
        var message = LocalizationHelper.GetString(messageKey);
        var presented = StoreMigrationLinkFailure.Present(message, address);
        StoreMigrationStatus.Message = presented.Instruction;
        if (presented.SelectableAddress is null)
        {
            StoreMigrationSetupAddress.Text = "";
            StoreMigrationSetupAddress.Visibility = Visibility.Collapsed;
        }
        else
        {
            StoreMigrationSetupAddress.Text = presented.SelectableAddress;
            StoreMigrationSetupAddress.Visibility = Visibility.Visible;
        }
        StoreMigrationDownloadSetup.Visibility = canDownloadSetup ? Visibility.Visible : Visibility.Collapsed;
        StoreMigrationStatus.Severity = severity;
        StoreMigrationStatus.IsOpen = false;
        StoreMigrationStatus.Visibility = Visibility.Visible;
        FrameworkElementAutomationPeer.CreatePeerForElement(StoreMigrationStatus);
        StoreMigrationStatus.IsOpen = true;
    }

    private void OnStoreMigrationStatusClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (!sender.IsOpen)
            sender.Visibility = Visibility.Collapsed;
    }

    private void PopulateAppInfo()
    {
        AppInfoVersionText.Text = SettingsAppInfoProjection.ResolveDisplayVersion(
            AppVersionInfo.DisplayVersion, PackageHelper.PackageVersion);
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

    private void OnGatewayUptimeRefreshTimerTick(object? sender, object e) =>
        RefreshGatewayUptimeText();

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

    private void OnCheckUpdates(object sender, RoutedEventArgs e) =>
        ((IAppCommands)CurrentApp).CheckForUpdates();

    private void OnDocumentationLink(object sender, RoutedEventArgs e) =>
        OpenShellTarget(DocumentationUrl, "documentation");

    private void OnSupportLink(object sender, RoutedEventArgs e) =>
        OpenShellTarget(SupportLinks.OfficialSupportUrl, "support");

    private void OnGitHubLink(object sender, RoutedEventArgs e) =>
        OpenShellTarget(GitHubUrl, "GitHub");

    private void OnDashboardLink(object sender, RoutedEventArgs e) =>
        ((IAppCommands)CurrentApp).OpenDashboard(null);

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
}
