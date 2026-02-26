using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using WinUIEx;
using Windows.UI;

namespace OpenClawTray.Windows;

public sealed partial class StatusDetailWindow : WindowEx
{
    public bool IsClosed { get; private set; }

    public event EventHandler? RefreshRequested;

    public StatusDetailWindow(
        ConnectionStatus status,
        ChannelHealth[] channels,
        SessionInfo[] sessions,
        GatewayUsageInfo? usage,
        DateTime lastCheck)
    {
        InitializeComponent();
        
        // Window configuration
        this.SetWindowSize(420, 550);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(status));
        
        Closed += (s, e) => IsClosed = true;
        
        UpdateStatus(status, channels, sessions, usage, lastCheck);
    }

    public void UpdateStatus(
        ConnectionStatus status,
        ChannelHealth[] channels,
        SessionInfo[] sessions,
        GatewayUsageInfo? usage,
        DateTime lastCheck)
    {
        // Status
        StatusText.Text = status.ToString();
        LastCheckText.Text = $"Last check: {lastCheck:HH:mm:ss}";
        
        var (glyph, color) = status switch
        {
            ConnectionStatus.Connected => ("\uE8FB", Color.FromArgb(255, 76, 175, 80)),    // Checkmark, Green
            ConnectionStatus.Connecting => ("\uE895", Color.FromArgb(255, 255, 193, 7)),   // Sync, Amber
            ConnectionStatus.Error => ("\uE783", Color.FromArgb(255, 244, 67, 54)),        // Error, Red
            _ => ("\uE8FB", Color.FromArgb(255, 158, 158, 158))                            // Gray
        };
        StatusIcon.Glyph = glyph;
        StatusIcon.Foreground = new SolidColorBrush(color);

        // Usage
        if (usage != null)
        {
            UsageSection.Visibility = Visibility.Visible;
            TodayCostText.Text = $"${usage.CostUsd:F2}";
            TodayRequestsText.Text = usage.RequestCount > 0
                ? $"{usage.RequestCount:N0} / {usage.TotalTokens:N0}"
                : $"{usage.TotalTokens:N0}";
            ProviderSummaryText.Text = string.IsNullOrWhiteSpace(usage.ProviderSummary)
                ? "n/a"
                : usage.ProviderSummary!;
        }
        else
        {
            UsageSection.Visibility = Visibility.Collapsed;
        }

        // Sessions
        if (sessions.Length > 0)
        {
            SessionsList.ItemsSource = sessions.Select(s => new
            {
                Channel = s.Channel ?? "Unknown",
                LastMessage = s.DisplayText
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
        ChannelsList.ItemsSource = channels.Select(c =>
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
                StatusText = c.Status ?? "Unknown",
                StatusBrush = brush
            };
        }).ToList();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private class ChannelViewModel
    {
        public string Name { get; set; } = "";
        public string StatusIcon { get; set; } = "";
        public string StatusText { get; set; } = "";
        public SolidColorBrush StatusBrush { get; set; } = new(Colors.Gray);
    }
}
