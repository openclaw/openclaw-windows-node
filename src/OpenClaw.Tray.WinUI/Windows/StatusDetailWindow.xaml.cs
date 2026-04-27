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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;
using Windows.UI;

namespace OpenClawTray.Windows;

public sealed partial class StatusDetailWindow : WindowEx
{
    private const double MaxCostTrendBarWidth = 160;

    public bool IsClosed { get; private set; }

    public event EventHandler? RefreshRequested;
    public event EventHandler? ActivityStreamRequested;
    public event EventHandler<string>? ChannelToggleRequested;
    private GatewayCommandCenterState _state;

    public StatusDetailWindow(GatewayCommandCenterState state)
    {
        _state = state;
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
        _state = state;
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
        if (state.Usage != null || state.UsageCost != null || state.UsageStatus != null)
        {
            UsageSection.Visibility = Visibility.Visible;
            TodayCostText.Text = state.UsageCost != null
                ? $"${state.UsageCost.Totals.TotalCost:F2} ({state.UsageCost.Days}d)"
                : $"${state.Usage?.CostUsd ?? 0:F2}";
            TodayRequestsText.Text = state.Usage?.RequestCount > 0
                ? $"{state.Usage.RequestCount:N0} / {state.Usage.TotalTokens:N0}"
                : $"{state.Usage?.TotalTokens ?? state.UsageCost?.Totals.TotalTokens ?? 0:N0}";
            var providerSummary = string.IsNullOrWhiteSpace(state.Usage?.ProviderSummary)
                ? BuildProviderSummary(state.UsageStatus)
                : state.Usage.ProviderSummary;
            ProviderSummaryText.Text = string.IsNullOrWhiteSpace(providerSummary)
                ? LocalizationHelper.GetString("Status_NotAvailable")
                : providerSummary;

            if (state.UsageCost?.Daily.Count > 0)
            {
                CostTrendList.ItemsSource = BuildCostTrend(state.UsageCost);
                CostTrendSection.Visibility = Visibility.Visible;
            }
            else
            {
                CostTrendSection.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            UsageSection.Visibility = Visibility.Collapsed;
            CostTrendSection.Visibility = Visibility.Collapsed;
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
        ChannelSummaryText.Text = BuildChannelSummary(state.Channels);
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
                StatusBrush = brush,
                ActionText = c.CanStop ? "Stop" : c.CanStart ? "Start" : "N/A",
                ActionEnabled = c.CanStart || c.CanStop
            };
        }).ToList();

        if (state.Nodes.Count > 0)
        {
            NodesList.ItemsSource = state.Nodes.Select(n => new NodeViewModel
            {
                StatusIcon = n.IsOnline ? "🟢" : "⚪",
                Name = string.IsNullOrWhiteSpace(n.DisplayName) ? n.NodeId : n.DisplayName,
                DetailText = $"{n.Platform ?? "unknown"} · {n.Capabilities.Count} cap · {n.Commands.Count} cmd",
                CommandText = BuildNodeCommandText(n),
                SummaryText = BuildNodeSummary(n)
            }).ToList();
            NodesList.Visibility = Visibility.Visible;
            NoNodesText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NodesList.Visibility = Visibility.Collapsed;
            NoNodesText.Visibility = Visibility.Visible;
        }

        if (state.RecentActivity.Count > 0)
        {
            RecentActivityList.ItemsSource = state.RecentActivity.Select(a => new ActivitySummaryViewModel
            {
                Title = a.Title,
                DetailText = BuildActivityDetail(a),
                TimeAgo = GetTimeAgo(a.Timestamp),
                Category = a.Category
            }).ToList();
            RecentActivityList.Visibility = Visibility.Visible;
            NoRecentActivityText.Visibility = Visibility.Collapsed;
        }
        else
        {
            RecentActivityList.Visibility = Visibility.Collapsed;
            NoRecentActivityText.Visibility = Visibility.Visible;
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

    private void OnCopyChannelSummary(object sender, RoutedEventArgs e)
    {
        CopyText(BuildChannelSummaryText(_state.Channels), "[CommandCenter] Copied channel summary");
    }

    private void OnToggleChannel(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button { Tag: string channelName } ||
            string.IsNullOrWhiteSpace(channelName))
        {
            return;
        }

        ChannelToggleRequested?.Invoke(this, channelName);
    }

    private void OnCopyNodeSummary(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button { Tag: string summary } ||
            string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        CopyText(summary, "[CommandCenter] Copied node summary");
    }

    private void OnOpenActivityStream(object sender, RoutedEventArgs e)
    {
        ActivityStreamRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCopyActivitySummary(object sender, RoutedEventArgs e)
    {
        CopyText(BuildActivitySummary(_state.RecentActivity), "[CommandCenter] Copied activity summary");
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        OpenFolder(Path.GetDirectoryName(Logger.LogFilePath), "logs");
    }

    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e)
    {
        OpenFolder(Path.GetDirectoryName(DiagnosticsJsonlService.FilePath), "diagnostics");
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        OpenFolder(SettingsManager.SettingsDirectoryPath, "config");
    }

    private void OnCopySupportContext(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(BuildSupportContext(_state));
        Clipboard.SetContent(package);
        Logger.Info("[CommandCenter] Copied support context");
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

    private static void OpenFolder(string? folderPath, string label)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Logger.Warn($"[CommandCenter] Cannot open {label} folder because no path is configured");
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
            Logger.Info($"[CommandCenter] Opened {label} folder: {folderPath}");
        }
        catch (IOException ex)
        {
            Logger.Warn($"[CommandCenter] Failed to open {label} folder {folderPath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warn($"[CommandCenter] Failed to open {label} folder {folderPath}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"[CommandCenter] Failed to open {label} folder {folderPath}: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            Logger.Warn($"[CommandCenter] Failed to open {label} folder {folderPath}: {ex.Message}");
        }
    }

    private static string BuildSupportContext(GatewayCommandCenterState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw Windows Tray Support Context");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Connection: {state.ConnectionStatus}");
        builder.AppendLine($"Topology: {state.Topology.DisplayName}");
        builder.AppendLine($"Transport: {state.Topology.Transport}");
        builder.AppendLine($"Gateway version: {state.GatewaySelf?.ServerVersion ?? "unknown"}");
        builder.AppendLine($"Gateway uptime ms: {state.GatewaySelf?.UptimeMs?.ToString() ?? "unknown"}");
        builder.AppendLine($"Channels: {state.Channels.Count}");
        builder.AppendLine($"Sessions: {state.Sessions.Count}");
        builder.AppendLine($"Nodes: {state.Nodes.Count}");
        builder.AppendLine($"Warnings: {state.Warnings.Count}");
        foreach (var warning in state.Warnings.Take(10))
        {
            builder.AppendLine($"- {warning.Severity}: {warning.Title}");
        }
        builder.AppendLine($"Recent activity: {state.RecentActivity.Count}");
        foreach (var item in state.RecentActivity.Take(10))
        {
            builder.AppendLine($"- {item.Timestamp:O} [{item.Category}] {item.Title}");
        }
        builder.AppendLine($"Ports: {state.PortDiagnostics.Count}");
        foreach (var port in state.PortDiagnostics)
        {
            builder.AppendLine($"- {port.Purpose}: {port.Port} {port.StatusText}");
        }
        builder.AppendLine($"Log file: {Logger.LogFilePath}");
        builder.AppendLine($"Diagnostics JSONL: {DiagnosticsJsonlService.FilePath ?? "not configured"}");
        builder.AppendLine($"Settings folder: {SettingsManager.SettingsDirectoryPath}");
        builder.AppendLine("Excluded: tokens, bootstrap tokens, command arguments, screenshots, recordings, camera data, microphone data, base64 payloads, and message payloads.");
        return builder.ToString();
    }

    private class ChannelViewModel
    {
        public string Name { get; set; } = "";
        public string StatusIcon { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string DetailText { get; set; } = "";
        public SolidColorBrush StatusBrush { get; set; } = new(Colors.Gray);
        public string ActionText { get; set; } = "";
        public bool ActionEnabled { get; set; }
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
        public string SummaryText { get; set; } = "";
    }

    private class CostTrendDayViewModel
    {
        public string DateLabel { get; set; } = "";
        public double BarWidth { get; set; }
        public string DetailText { get; set; } = "";
    }

    private class ActivitySummaryViewModel
    {
        public string Title { get; set; } = "";
        public string DetailText { get; set; } = "";
        public string TimeAgo { get; set; } = "";
        public string Category { get; set; } = "";
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

    private static string BuildChannelSummary(IReadOnlyCollection<ChannelCommandCenterInfo> channels)
    {
        if (channels.Count == 0)
            return "No channels reported by gateway health.";

        var running = channels.Count(c => c.CanStop);
        var startable = channels.Count(c => c.CanStart);
        var errors = channels.Count(c => string.Equals(c.Status, "error", StringComparison.OrdinalIgnoreCase));
        return $"{running}/{channels.Count} running · {startable} startable · {errors} error";
    }

    private static string BuildChannelSummaryText(IReadOnlyCollection<ChannelCommandCenterInfo> channels)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Channels: {BuildChannelSummary(channels)}");
        foreach (var channel in channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {channel.Name}: {channel.Status ?? "unknown"} ({BuildChannelDetail(channel)})");
        }

        return builder.ToString();
    }

    private static string BuildProviderSummary(GatewayUsageStatusInfo? usageStatus)
    {
        if (usageStatus?.Providers.Count > 0)
        {
            return string.Join(" · ", usageStatus.Providers.Select(provider =>
            {
                var displayName = string.IsNullOrWhiteSpace(provider.DisplayName)
                    ? provider.Provider
                    : provider.DisplayName;
                return string.IsNullOrWhiteSpace(provider.Plan)
                    ? displayName
                    : $"{displayName} ({provider.Plan})";
            }));
        }

        return "";
    }

    private static List<CostTrendDayViewModel> BuildCostTrend(GatewayCostUsageInfo usageCost)
    {
        var days = usageCost.Daily
            .OrderBy(day => day.Date, StringComparer.Ordinal)
            .TakeLast(30)
            .ToList();
        var maxCost = days.Max(day => day.TotalCost);

        return days.Select(day =>
        {
            var width = maxCost > 0
                ? Math.Max(4, day.TotalCost / maxCost * MaxCostTrendBarWidth)
                : 4;
            var missing = day.MissingCostEntries > 0
                ? $" · {day.MissingCostEntries} missing"
                : "";

            return new CostTrendDayViewModel
            {
                DateLabel = FormatCostDate(day.Date),
                BarWidth = width,
                DetailText = $"${day.TotalCost:F2} · {day.TotalTokens:N0} tok{missing}"
            };
        }).ToList();
    }

    private static string FormatCostDate(string date)
    {
        return DateTime.TryParseExact(
            date,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToString("MMM d", CultureInfo.CurrentCulture)
            : date;
    }

    private static string BuildActivityDetail(CommandCenterActivityInfo activity)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(activity.Details))
            details.Add(activity.Details);
        if (!string.IsNullOrWhiteSpace(activity.SessionKey))
            details.Add($"session: {activity.SessionKey}");
        if (!string.IsNullOrWhiteSpace(activity.NodeId))
            details.Add($"node: {ShortId(activity.NodeId)}");
        if (!string.IsNullOrWhiteSpace(activity.DashboardPath))
            details.Add($"dashboard: {activity.DashboardPath}");

        return details.Count == 0 ? activity.Category : string.Join(" · ", details);
    }

    private static string BuildActivitySummary(IReadOnlyCollection<CommandCenterActivityInfo> activity)
    {
        if (activity.Count == 0)
            return "No recent OpenClaw tray activity.";

        var builder = new StringBuilder();
        builder.AppendLine("Recent OpenClaw tray activity");
        foreach (var item in activity)
        {
            var details = BuildActivityDetail(item);
            builder.AppendLine($"{item.Timestamp:O} [{item.Category}] {item.Title} - {details}");
        }

        return builder.ToString();
    }

    private static string GetTimeAgo(DateTime timestamp)
    {
        var diff = DateTime.Now - timestamp;
        if (diff.TotalMinutes < 1) return LocalizationHelper.GetString("TimeAgo_JustNow");
        if (diff.TotalMinutes < 60) return string.Format(LocalizationHelper.GetString("TimeAgo_MinutesFormat"), (int)diff.TotalMinutes);
        if (diff.TotalHours < 24) return string.Format(LocalizationHelper.GetString("TimeAgo_HoursFormat"), (int)diff.TotalHours);
        if (diff.TotalDays < 7) return string.Format(LocalizationHelper.GetString("TimeAgo_DaysFormat"), (int)diff.TotalDays);
        return timestamp.ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Length <= 12 ? value : value[..12] + "...";
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

    private static string BuildNodeSummary(NodeCapabilityHealthInfo node)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.IsNullOrWhiteSpace(node.DisplayName) ? node.NodeId : node.DisplayName);
        builder.AppendLine($"Node ID: {node.NodeId}");
        builder.AppendLine($"Platform: {node.Platform ?? "unknown"}");
        builder.AppendLine($"Status: {(node.IsOnline ? "online" : "offline")}");
        builder.AppendLine($"Capabilities: {string.Join(", ", node.Capabilities.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))}");
        builder.AppendLine($"Commands: {string.Join(", ", node.Commands.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))}");
        if (node.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings:");
            foreach (var warning in node.Warnings)
            {
                builder.AppendLine($"- {warning.Title}: {warning.Detail}");
            }
        }

        return builder.ToString();
    }

    private static void CopyText(string text, string logMessage)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Logger.Info(logMessage);
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
