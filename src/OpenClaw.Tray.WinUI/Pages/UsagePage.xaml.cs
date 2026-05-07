using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class UsagePage : Page
{
    private HubWindow? _hub;

    public UsagePage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        ConnectionWarning.Visibility = hub.GatewayClient != null ? Visibility.Collapsed : Visibility.Visible;
        if (hub.GatewayClient != null)
        {
            // Apply cached data immediately, then request fresh
            if (hub.LastUsage != null) UpdateUsage(hub.LastUsage);
            if (hub.LastUsageCost != null) UpdateUsageCost(hub.LastUsageCost);
            if (hub.LastUsageStatus != null) UpdateUsageStatus(hub.LastUsageStatus);
            _ = hub.GatewayClient.RequestUsageAsync();
            _ = hub.GatewayClient.RequestUsageCostAsync(30);
            _ = hub.GatewayClient.RequestUsageStatusAsync();
        }
    }

    public void UpdateUsage(GatewayUsageInfo usage)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            RequestCountText.Text = usage.RequestCount.ToString();
            TokenCountText.Text = FormatLargeNumber(usage.TotalTokens);
            TotalCostText.Text = $"${usage.CostUsd:F2}";
        });
    }

    public void UpdateUsageCost(GatewayCostUsageInfo cost)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            TotalCostText.Text = $"${cost.Totals.TotalCost:F2}";
            TokenCountText.Text = FormatLargeNumber(cost.Totals.TotalTokens);

            DailyListView.ItemsSource = cost.Daily.Select(d => new DailyRow
            {
                Date = d.Date,
                Cost = $"${d.TotalCost:F2}",
            }).ToList();
        });
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            ProviderCountText.Text = status.Providers.Count.ToString();
            ProviderListView.ItemsSource = status.Providers.Select(p => new ProviderRow
            {
                Name = p.DisplayName,
                Requests = p.Plan ?? "",
                Tokens = p.Windows.Count > 0 ? $"{p.Windows[0].UsedPercent:F0}% used" : "",
                Cost = p.Error ?? "",
            }).ToList();
        });
    }

    private void OnPeriod7Days(object sender, RoutedEventArgs e)
    {
        Period7DaysButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Period30DaysButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
    }

    private void OnPeriod30Days(object sender, RoutedEventArgs e)
    {
        Period30DaysButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Period7DaysButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
    }

    private static string FormatLargeNumber(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K";
        return n.ToString();
    }

    private class ProviderRow
    {
        public string Name { get; set; } = "";
        public string Requests { get; set; } = "";
        public string Tokens { get; set; } = "";
        public string Cost { get; set; } = "";
    }

    private class DailyRow
    {
        public string Date { get; set; } = "";
        public string Cost { get; set; } = "";
    }
}
