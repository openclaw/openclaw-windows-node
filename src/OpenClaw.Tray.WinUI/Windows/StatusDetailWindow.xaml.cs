using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;
using Windows.UI;

namespace OpenClawTray.Windows;

public sealed partial class StatusDetailWindow : WindowEx
{
    public bool IsClosed { get; private set; }

    public event EventHandler? RefreshRequested;

    public StatusDetailWindow(GatewayCommandCenterState state)
    {
        InitializeComponent();
        Title = "Command Center — OpenClaw Tray";
        
        // Window configuration
        this.SetWindowSize(560, 720);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(state.ConnectionStatus));
        
        Closed += (s, e) => IsClosed = true;
        
        Logger.Info("[CommandCenter] Window opened");
        UpdateStatus(state);
    }

    public void UpdateStatus(GatewayCommandCenterState state)
    {
        Logger.Info($"[CommandCenter] UpdateStatus: connection={state.ConnectionStatus}, channels={state.Channels.Count}, sessions={state.Sessions.Count}, nodes={state.Nodes.Count}, warnings={state.Warnings.Count}");

        // Status
        StatusText.Text = LocalizationHelper.GetConnectionStatusText(state.ConnectionStatus);
        LastCheckText.Text = string.Format(LocalizationHelper.GetString("Status_LastCheckFormat"), state.LastRefresh.ToLocalTime().ToString("HH:mm:ss"));
        
        var (glyph, color) = state.ConnectionStatus switch
        {
            ConnectionStatus.Connected => ("\uE8FB", Color.FromArgb(255, 76, 175, 80)),    // Checkmark, Green
            ConnectionStatus.Connecting => ("\uE895", Color.FromArgb(255, 255, 193, 7)),   // Sync, Amber
            ConnectionStatus.Error => ("\uE783", Color.FromArgb(255, 244, 67, 54)),        // Error, Red
            _ => ("\uE8FB", Color.FromArgb(255, 158, 158, 158))                            // Gray
        };
        StatusIcon.Glyph = glyph;
        StatusIcon.Foreground = new SolidColorBrush(color);

        GatewayKindText.Text = state.Topology.DisplayName;
        GatewayUrlText.Text = string.IsNullOrWhiteSpace(state.Topology.GatewayUrl)
            ? "n/a"
            : state.Topology.GatewayUrl;
        GatewayTransportText.Text = state.Topology.Transport;
        GatewayDetailText.Text = state.Topology.Detail;

        if (state.Tunnel != null && state.Tunnel.Status != TunnelStatus.NotConfigured)
        {
            TunnelLabelText.Visibility = Visibility.Visible;
            TunnelDetailText.Visibility = Visibility.Visible;
            TunnelDetailText.Text = BuildTunnelDetail(state.Tunnel);
        }
        else
        {
            TunnelLabelText.Visibility = Visibility.Collapsed;
            TunnelDetailText.Visibility = Visibility.Collapsed;
        }

        if (state.GatewaySelf?.HasAnyDetails == true)
        {
            GatewayVersionLabelText.Visibility = Visibility.Visible;
            GatewayVersionText.Visibility = Visibility.Visible;
            GatewayVersionText.Text = BuildGatewayVersionText(state.GatewaySelf);

            GatewayUptimeLabelText.Visibility = Visibility.Visible;
            GatewayUptimeText.Visibility = Visibility.Visible;
            GatewayUptimeText.Text = BuildGatewayUptimeText(state.GatewaySelf);

            GatewayStateLabelText.Visibility = Visibility.Visible;
            GatewayStateText.Visibility = Visibility.Visible;
            GatewayStateText.Text = BuildGatewayStateText(state.GatewaySelf);
        }
        else
        {
            GatewayVersionLabelText.Visibility = Visibility.Collapsed;
            GatewayVersionText.Visibility = Visibility.Collapsed;
            GatewayUptimeLabelText.Visibility = Visibility.Collapsed;
            GatewayUptimeText.Visibility = Visibility.Collapsed;
            GatewayStateLabelText.Visibility = Visibility.Collapsed;
            GatewayStateText.Visibility = Visibility.Collapsed;
        }

        OverviewChannelsText.Text = $"Channels: {state.Channels.Count(c => c.CanStop)}/{state.Channels.Count} ready";
        OverviewSessionsText.Text = $"Sessions: {state.Sessions.Count}";
        OverviewNodesText.Text = $"Nodes: {state.Nodes.Count(n => n.IsOnline)}/{state.Nodes.Count} online";
        OverviewWarningsText.Text = $"Warnings: {state.Warnings.Count}";

        if (state.Warnings.Count > 0)
        {
            WarningsSection.Visibility = Visibility.Visible;
            WarningsList.ItemsSource = state.Warnings.Select(w => new WarningViewModel
            {
                Icon = w.Severity switch
                {
                    GatewayDiagnosticSeverity.Critical => "🔴",
                    GatewayDiagnosticSeverity.Warning => "🟡",
                    _ => "ℹ️"
                },
                Title = w.Title,
                Detail = w.Detail,
                RepairAction = w.RepairAction ?? "Copy fix",
                CopyText = w.CopyText ?? ""
            }).ToList();
        }
        else
        {
            WarningsSection.Visibility = Visibility.Collapsed;
        }

        if (state.PortDiagnostics.Count > 0)
        {
            PortDiagnosticsSection.Visibility = Visibility.Visible;
            PortDiagnosticsList.ItemsSource = state.PortDiagnostics.Select(p => new PortDiagnosticViewModel
            {
                StatusIcon = p.IsListening ? "🟢" : "⚪",
                Purpose = $"{p.Purpose} :{p.Port}",
                Detail = p.Detail,
                StatusText = p.StatusText
            }).ToList();
        }
        else
        {
            PortDiagnosticsSection.Visibility = Visibility.Collapsed;
        }

        PermissionsList.ItemsSource = state.Permissions.Select(p => new PermissionDiagnosticViewModel
        {
            StatusIcon = p.Status.Equals("optional", StringComparison.OrdinalIgnoreCase) ? "⚪" : "🔒",
            Name = $"{p.Name} ({p.Status})",
            Detail = p.Detail,
            SettingsUri = p.SettingsUri
        }).ToList();

        // Usage
        if (state.Usage != null)
        {
            UsageSection.Visibility = Visibility.Visible;
            TodayCostText.Text = state.UsageCost != null
                ? $"${state.UsageCost.Totals.TotalCost:F2} ({state.UsageCost.Days}d)"
                : $"${state.Usage.CostUsd:F2}";
            TodayRequestsText.Text = state.Usage.RequestCount > 0
                ? $"{state.Usage.RequestCount:N0} / {state.Usage.TotalTokens:N0}"
                : $"{state.Usage.TotalTokens:N0}";
            ProviderSummaryText.Text = string.IsNullOrWhiteSpace(state.Usage.ProviderSummary)
                ? LocalizationHelper.GetString("Status_NotAvailable")
                : state.Usage.ProviderSummary!;
        }
        else
        {
            UsageSection.Visibility = Visibility.Collapsed;
        }

        // Sessions
        if (state.Sessions.Count > 0)
        {
            SessionsList.ItemsSource = state.Sessions.Select(s => new
            {
                Channel = s.Channel ?? LocalizationHelper.GetString("StatusDisplay_Unknown"),
                LastMessage = s.RichDisplayText
            }).ToList();
            SessionsList.Visibility = Visibility.Visible;
            NoSessionsText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SessionsList.Visibility = Visibility.Collapsed;
            NoSessionsText.Visibility = Visibility.Visible;
        }

        // Channels
        ChannelsList.ItemsSource = state.Channels.Select(c =>
        {
            var (icon, brush) = ChannelHealth.IsHealthyStatus(c.Status)
                ? ("🟢", new SolidColorBrush(Color.FromArgb(255, 76, 175, 80)))
                : ChannelHealth.IsIntermediateStatus(c.Status)
                ? ("🟡", new SolidColorBrush(Color.FromArgb(255, 241, 196, 15)))
                : ("🔴", new SolidColorBrush(Color.FromArgb(255, 244, 67, 54)));

            return new ChannelViewModel
            {
                Name = c.Name,
                StatusIcon = icon,
                StatusText = c.Status ?? LocalizationHelper.GetString("StatusDisplay_Unknown"),
                DetailText = BuildChannelDetail(c),
                StatusBrush = brush
            };
        }).ToList();

        if (state.Nodes.Count > 0)
        {
            NodesList.ItemsSource = state.Nodes.Select(n => new NodeViewModel
            {
                StatusIcon = n.IsOnline ? "🟢" : "⚪",
                Name = string.IsNullOrWhiteSpace(n.DisplayName) ? n.NodeId : n.DisplayName,
                DetailText = $"{n.Platform ?? "unknown"} · {n.Capabilities.Count} cap · {n.Commands.Count} cmd",
                CommandText = BuildNodeCommandText(n)
            }).ToList();
            NodesList.Visibility = Visibility.Visible;
            NoNodesText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NodesList.Visibility = Visibility.Collapsed;
            NoNodesText.Visibility = Visibility.Visible;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        Logger.Info("[StatusDetail] Refresh requested");
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCopyWarningFix(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button { Tag: string copyText } ||
            string.IsNullOrWhiteSpace(copyText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(copyText);
        Clipboard.SetContent(package);
        Logger.Info("[CommandCenter] Copied diagnostic repair text");
    }

    private void OnOpenPermissionSettings(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button { Tag: string settingsUri } ||
            string.IsNullOrWhiteSpace(settingsUri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(settingsUri) { UseShellExecute = true });
            Logger.Info($"[CommandCenter] Opened permission settings: {settingsUri}");
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"[CommandCenter] Failed to open permission settings {settingsUri}: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            Logger.Warn($"[CommandCenter] Failed to open permission settings {settingsUri}: {ex.Message}");
        }
    }

    private class ChannelViewModel
    {
        public string Name { get; set; } = "";
        public string StatusIcon { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string DetailText { get; set; } = "";
        public SolidColorBrush StatusBrush { get; set; } = new(Colors.Gray);
    }

    private class WarningViewModel
    {
        public string Icon { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string RepairAction { get; set; } = "Copy fix";
        public string CopyText { get; set; } = "";
        public Visibility CopyVisibility => string.IsNullOrWhiteSpace(CopyText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private class NodeViewModel
    {
        public string StatusIcon { get; set; } = "";
        public string Name { get; set; } = "";
        public string DetailText { get; set; } = "";
        public string CommandText { get; set; } = "";
    }

    private class PortDiagnosticViewModel
    {
        public string StatusIcon { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Detail { get; set; } = "";
        public string StatusText { get; set; } = "";
    }

    private class PermissionDiagnosticViewModel
    {
        public string StatusIcon { get; set; } = "";
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
        public string SettingsUri { get; set; } = "";
    }

    private static string BuildChannelDetail(ChannelCommandCenterInfo channel)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(channel.Type))
            parts.Add(channel.Type!);
        if (channel.IsLinked)
            parts.Add(string.IsNullOrWhiteSpace(channel.AuthAge) ? "linked" : $"linked · {channel.AuthAge}");
        if (!string.IsNullOrWhiteSpace(channel.Error))
            parts.Add(channel.Error!);
        if (channel.CanStart)
            parts.Add("start available");
        if (channel.CanStop)
            parts.Add("stop available");
        return parts.Count == 0 ? "no details" : string.Join(" · ", parts);
    }

    private static string BuildNodeCommandText(NodeCapabilityHealthInfo node)
    {
        var parts = new List<string>();
        if (node.SafeDeclaredCommands.Count > 0)
            parts.Add($"{node.SafeDeclaredCommands.Count} safe");
        if (node.DangerousDeclaredCommands.Count > 0)
            parts.Add($"{node.DangerousDeclaredCommands.Count} opt-in");
        if (node.WindowsSpecificDeclaredCommands.Count > 0)
            parts.Add($"{node.WindowsSpecificDeclaredCommands.Count} Windows");
        if (node.MissingMacParityCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase))
            parts.Add("missing browser.proxy");
        return parts.Count == 0 ? "no command details" : string.Join(" · ", parts);
    }

    private static string BuildTunnelDetail(TunnelCommandCenterInfo tunnel)
    {
        var parts = new List<string> { tunnel.Status.ToString() };
        if (!string.IsNullOrWhiteSpace(tunnel.LocalEndpoint) &&
            !string.IsNullOrWhiteSpace(tunnel.RemoteEndpoint))
        {
            parts.Add($"{tunnel.LocalEndpoint} -> {tunnel.RemoteEndpoint}");
        }
        if (tunnel.StartedAt.HasValue && tunnel.Status == TunnelStatus.Up)
        {
            parts.Add($"started {tunnel.StartedAt.Value.ToLocalTime():HH:mm:ss}");
        }
        if (!string.IsNullOrWhiteSpace(tunnel.LastError))
        {
            parts.Add(tunnel.LastError!);
        }
        return string.Join(" · ", parts);
    }

    private static string BuildGatewayVersionText(GatewaySelfInfo gateway)
    {
        var parts = new List<string> { gateway.VersionText };
        if (gateway.Protocol.HasValue)
            parts.Add($"protocol {gateway.Protocol.Value}");
        if (!string.IsNullOrWhiteSpace(gateway.ConnectionId))
            parts.Add($"conn {gateway.ConnectionId}");
        return string.Join(" · ", parts);
    }

    private static string BuildGatewayUptimeText(GatewaySelfInfo gateway)
    {
        var parts = new List<string> { gateway.UptimeText };
        if (!string.IsNullOrWhiteSpace(gateway.AuthMode))
            parts.Add($"auth {gateway.AuthMode}");
        if (gateway.TickIntervalMs.HasValue)
            parts.Add($"tick {gateway.TickIntervalMs.Value}ms");
        return string.Join(" · ", parts);
    }

    private static string BuildGatewayStateText(GatewaySelfInfo gateway)
    {
        var parts = new List<string>();
        if (gateway.StateVersionPresence.HasValue || gateway.StateVersionHealth.HasValue)
            parts.Add($"presence {gateway.StateVersionPresence?.ToString() ?? "?"} / health {gateway.StateVersionHealth?.ToString() ?? "?"}");
        if (gateway.PresenceCount.HasValue)
            parts.Add($"{gateway.PresenceCount.Value} presence");
        if (gateway.MaxPayload.HasValue)
            parts.Add($"max payload {gateway.MaxPayload.Value:N0}");
        return parts.Count == 0 ? "waiting for gateway snapshot" : string.Join(" · ", parts);
    }
}
