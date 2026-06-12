using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class AboutPage : Page
{
    private const string DocumentationUrl = "https://docs.openclaw.ai/platforms/windows";
    private const string GitHubUrl = "https://github.com/openclaw/openclaw-windows-node";

    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private readonly DispatcherTimer _uptimeRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppState? _appState;
    private long? _sampledGatewayUptimeMs;
    private DateTime _sampledGatewayUptimeUtc;

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = AppVersionInfo.DisplayVersion;
        RuntimeStackText.Text = BuildRuntimeStackDisplayText();
        _uptimeRefreshTimer.Tick += OnUptimeRefreshTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryLoadGatewayInfo();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _uptimeRefreshTimer.Stop();
        if (_appState != null)
        {
            _appState.PropertyChanged -= OnAppStateChanged;
            _appState = null;
        }
    }

    public void Initialize()
    {
        var appState = CurrentApp.AppState!;
        if (!ReferenceEquals(_appState, appState))
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            _appState = appState;
            _appState.PropertyChanged += OnAppStateChanged;
        }

        TryLoadGatewayInfo();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Status):
            case nameof(AppState.GatewaySelf):
                RefreshGatewayInfo();
                break;
        }
    }

    public void RefreshGatewayInfo() => TryLoadGatewayInfo();

    private void TryLoadGatewayInfo()
    {
        var self = CurrentApp.AppState?.GatewaySelf;
        if (CurrentApp.AppState?.Status == OpenClaw.Shared.ConnectionStatus.Connected && self != null)
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
            _uptimeRefreshTimer.Stop();
            GatewayVersionText.Text = "—";
            GatewayProtocolText.Text = "—";
            GatewayAuthText.Text = "—";
            GatewayUptimeText.Text = "—";
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        OpenShellTarget(Logger.LogFilePath, "log file");
    }

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsManager.SettingsDirectoryPath);
            OpenShellTarget(SettingsManager.SettingsDirectoryPath, "config folder");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            Logger.Warn($"Failed to open config folder: {ex.Message}");
        }
    }

    private void OnCopySupportClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnCopySupportClickAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnCopySupportClick));

    private async Task OnCopySupportClickAsync()
    {
        // Unified with the richer CommandCenterTextHelper.BuildSupportContext
        // that the Diagnostics page uses, sourced from App's authoritative
        // CommandCenterState builder. Falls back to a minimal local
        // string only when the state isn't available yet (cold start).
        string context;
        var state = CurrentApp.BuildCommandCenterState();
        if (state != null)
        {
            context = OpenClawTray.Helpers.CommandCenterTextHelper.BuildSupportContext(state);
        }
        else
        {
            context = $"OpenClaw Hub {AppVersionInfo.DisplayVersion}\n"
                + $"OS: {Environment.OSVersion}\n"
                + $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n"
                + $"Connection: {CurrentApp.AppState?.Status}\n"
                + $"Gateway: {CurrentApp.Settings?.GetEffectiveGatewayUrl() ?? "n/a"}\n";
        }

        ClipboardHelper.CopyText(context);
        await Task.CompletedTask;
    }

    private void OnMoreDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).Navigate("debug");
    }

    private void OnDocumentationClick(object sender, RoutedEventArgs e)
    {
        OpenShellTarget(DocumentationUrl, "documentation");
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        OpenShellTarget(GitHubUrl, "GitHub");
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

    private void CaptureGatewayUptimeSample(GatewaySelfInfo self)
    {
        if (!self.UptimeMs.HasValue)
        {
            _sampledGatewayUptimeMs = null;
            _uptimeRefreshTimer.Stop();
            return;
        }

        if (_sampledGatewayUptimeMs != self.UptimeMs.Value)
        {
            _sampledGatewayUptimeMs = self.UptimeMs.Value;
            _sampledGatewayUptimeUtc = DateTime.UtcNow;
        }

        if (!_uptimeRefreshTimer.IsEnabled)
            _uptimeRefreshTimer.Start();
    }

    private void OnUptimeRefreshTimerTick(object? sender, object e)
    {
        RefreshGatewayUptimeText();
    }

    private void RefreshGatewayUptimeText()
    {
        if (CurrentApp.AppState?.Status != OpenClaw.Shared.ConnectionStatus.Connected ||
            !_sampledGatewayUptimeMs.HasValue)
        {
            _uptimeRefreshTimer.Stop();
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

    private void OnDashboardClick(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).OpenDashboard(null);
    }
}
