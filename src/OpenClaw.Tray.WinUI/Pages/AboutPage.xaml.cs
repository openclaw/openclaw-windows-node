using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Windows;
using System;
using System.Diagnostics;

namespace OpenClawTray.Pages;

public sealed partial class AboutPage : Page
{
    private HubWindow? _hub;

    public AboutPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        TryLoadGatewayInfo();
    }

    public void RefreshGatewayInfo() => TryLoadGatewayInfo();

    private void TryLoadGatewayInfo()
    {
        var self = _hub?.LastGatewaySelf;
        if (_hub?.CurrentStatus == OpenClaw.Shared.ConnectionStatus.Connected && self != null)
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
            var context = $"OpenClaw Hub v0.1.0\n"
                + $"OS: {Environment.OSVersion}\n"
                + $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n"
                + $"Connection: {_hub?.CurrentStatus}\n"
                + $"Gateway: {_hub?.Settings?.GetEffectiveGatewayUrl() ?? "n/a"}\n";

            ClipboardHelper.CopyText(context);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to copy support context: {ex.Message}");
        }
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        _hub?.CheckForUpdatesAction?.Invoke();
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
        _hub?.OpenDashboardAction?.Invoke(null);
    }
}
