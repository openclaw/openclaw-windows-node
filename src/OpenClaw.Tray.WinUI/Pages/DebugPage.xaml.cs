using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinDataTransfer = global::Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class DebugPage : Page
{
    private HubWindow? _hub;

    private static readonly string LocalAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray");
    private static readonly string RoamingAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");
    private static readonly string LogPath = Path.Combine(LocalAppData, "openclaw-tray.log");
    private static readonly string DeviceKeyPath = Path.Combine(LocalAppData, "device-key-ed25519.json");

    public DebugPage() { InitializeComponent(); }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        LoadLog();
        LoadConnectionStatus();
        LoadDeviceIdentity();
    }

    // ── Log Viewer ───────────────────────────────────────────────────

    private void LoadLog()
    {
        try
        {
            if (File.Exists(LogPath))
            {
                var lines = File.ReadLines(LogPath).TakeLast(100).ToArray();
                LogText.Text = string.Join("\n", lines);

                // Auto-scroll to bottom
                DispatcherQueue?.TryEnqueue(() =>
                {
                    LogScrollViewer.UpdateLayout();
                    LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
                });
            }
            else
            {
                LogText.Text = "No log file found.";
            }
        }
        catch (Exception ex)
        {
            LogText.Text = $"Failed to read log: {ex.Message}";
        }
    }

    private void OnRefreshLog(object sender, RoutedEventArgs e) => LoadLog();

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var dp = new WinDataTransfer.DataPackage();
        dp.SetText(LogText.Text ?? "");
        WinDataTransfer.Clipboard.SetContent(dp);
    }

    // ── Connection Status ────────────────────────────────────────────

    private void LoadConnectionStatus()
    {
        if (_hub == null) return;

        var statusText = _hub.CurrentStatus switch
        {
            ConnectionStatus.Connected => "🟢 Connected",
            ConnectionStatus.Connecting => "🟡 Connecting",
            ConnectionStatus.Disconnected => "🔴 Disconnected",
            ConnectionStatus.Error => "❌ Error",
            _ => "Unknown"
        };
        OperatorStatusText.Text = statusText;

        GatewayUrlText.Text = _hub.Settings?.GetEffectiveGatewayUrl() ?? "—";
        NodeModeText.Text = _hub.Settings?.EnableNodeMode == true ? "Enabled" : "Disabled";
    }

    // ── Device Identity ──────────────────────────────────────────────

    private void LoadDeviceIdentity()
    {
        try
        {
            if (File.Exists(DeviceKeyPath))
            {
                var json = File.ReadAllText(DeviceKeyPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("deviceId", out var id))
                {
                    var deviceId = id.GetString() ?? "Unknown";
                    DeviceIdText.Text = deviceId.Length > 16 ? deviceId[..16] + "…" : deviceId;
                    DeviceIdText.Tag = deviceId; // store full ID for copy
                }
                else
                {
                    DeviceIdText.Text = "Not found";
                }

                if (doc.RootElement.TryGetProperty("publicKey", out var pk))
                    PublicKeyText.Text = pk.GetString() ?? "Unknown";
                else
                    PublicKeyText.Text = "Not found";
            }
            else
            {
                DeviceIdText.Text = "No device key file";
                PublicKeyText.Text = "—";
            }
        }
        catch (Exception ex)
        {
            DeviceIdText.Text = $"Error: {ex.Message}";
            PublicKeyText.Text = "—";
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        var fullId = DeviceIdText.Tag as string ?? DeviceIdText.Text;
        var dp = new WinDataTransfer.DataPackage();
        dp.SetText(fullId);
        WinDataTransfer.Clipboard.SetContent(dp);
    }

    // ── Debug Actions ────────────────────────────────────────────────

    private void OnOpenLogFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LogPath))
                Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RoamingAppData);
            Process.Start(new ProcessStartInfo(RoamingAppData) { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LocalAppData);
            Process.Start(new ProcessStartInfo(LocalAppData) { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenConnectionStatus(object sender, RoutedEventArgs e)
    {
        _hub?.OpenConnectionStatusAction?.Invoke();
    }

    private void OnCopySupportContext(object sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            $"Gateway URL: {GatewayUrlText.Text}",
            $"Status: {OperatorStatusText.Text}",
            $"Node Mode: {NodeModeText.Text}",
            $"Device ID: {DeviceIdText.Tag as string ?? DeviceIdText.Text}",
            $"Machine: {Environment.MachineName}",
            $"OS: {Environment.OSVersion}",
            $"Time: {DateTime.UtcNow:u}"
        };

        var dp = new WinDataTransfer.DataPackage();
        dp.SetText(string.Join("\n", lines));
        WinDataTransfer.Clipboard.SetContent(dp);

        if (sender is Button btn)
        {
            btn.Content = "✓ Copied";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (t, a) => { btn.Content = "📋 Copy Support Context"; timer.Stop(); };
            timer.Start();
        }
    }

    private void OnRelaunchOnboarding(object sender, RoutedEventArgs e)
    {
        _hub?.OpenSetupAction?.Invoke();
    }
}
