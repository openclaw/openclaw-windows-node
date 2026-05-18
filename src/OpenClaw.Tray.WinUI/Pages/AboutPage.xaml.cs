using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class AboutPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;

    public AboutPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;
        TryLoadGatewayInfo();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
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
            GatewayModelText.Text = self.Protocol.HasValue ? $"protocol v{self.Protocol}" : "unknown";
            GatewayAuthText.Text = string.IsNullOrWhiteSpace(self.AuthMode) ? "unknown" : self.AuthMode;
            GatewayUptimeText.Text = self.UptimeText;
        }
        else
        {
            GatewayVersionText.Text = "—";
            GatewayModelText.Text = "—";
            GatewayAuthText.Text = "—";
            GatewayUptimeText.Text = "—";
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "openclaw-tray.log");
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open log file: {ex.Message}");
        }
    }

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenClawTray");
            Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open config folder: {ex.Message}");
        }
    }

    private async void OnCopySupportClick(object sender, RoutedEventArgs e)
    {
        try
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
                context = $"OpenClaw Hub v0.1.0\n"
                    + $"OS: {Environment.OSVersion}\n"
                    + $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n"
                    + $"Connection: {CurrentApp.AppState?.Status}\n"
                    + $"Gateway: {CurrentApp.Settings?.GetEffectiveGatewayUrl() ?? "n/a"}\n";
            }

            ClipboardHelper.CopyText(context);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to copy support context: {ex.Message}");
        }
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).CheckForUpdates();
    }

    private void OnMoreDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).Navigate("debug");
    }

    private void OnDocumentationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://openclaw.ai/docs") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open docs: {ex.Message}");
        }
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/openclaw/openclaw-windows-node") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open GitHub: {ex.Message}");
        }
    }

    private void OnDashboardClick(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).OpenDashboard(null);
    }
}
