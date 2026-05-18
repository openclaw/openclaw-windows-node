using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class UsagePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;
    // Default matches the XAML-selected Period7DaysItem (IsSelected="True").
    private int _currentPeriodDays = 7;
    private readonly AsyncListLoadingState _providerLoading = new();
    private readonly AsyncListLoadingState _dailyCostLoading = new();
    private DateTime _lastAppliedUsageCostUpdatedAtUtc = DateTime.MinValue;

    public UsagePage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;
        var client = CurrentApp.GatewayClient;
        if (client != null)
        {
            ConnectionInfoBar.IsOpen = false;
            // Apply cached data immediately, then request fresh.
            if (_appState?.Usage != null) UpdateUsage(_appState.Usage);
            // Only apply cached cost data when its period matches the current
            // selection — otherwise the daily list briefly shows e.g. 30-day
            // data while the selector reads "7 Days".
            if (_appState?.UsageCost != null && _appState.UsageCost.Days == _currentPeriodDays)
            {
                UpdateUsageCost(_appState.UsageCost);
                _dailyCostLoading.BeginRefresh();
            }
            else
            {
                _dailyCostLoading.BeginInitialRefresh();
            }
            if (_appState?.UsageStatus != null) UpdateUsageStatus(_appState.UsageStatus);
            else _providerLoading.BeginInitialRefresh();
            UpdateDailyCostLoadingVisuals();
            UpdateProviderLoadingVisuals();
            _ = client.RequestUsageAsync();
            _ = client.RequestUsageCostAsync(_currentPeriodDays);
            _ = client.RequestUsageStatusAsync();
        }
        else
        {
            _providerLoading.Fail();
            _dailyCostLoading.Fail();
            ShowDisconnected();
            UpdateProviderLoadingVisuals();
            UpdateDailyCostLoadingVisuals();
        }
    }

    private void OnOpenConnectionClick(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Usage):
                if (_appState?.Usage != null) UpdateUsage(_appState.Usage);
                break;
            case nameof(AppState.UsageCost):
                if (_appState?.UsageCost != null) UpdateUsageCost(_appState.UsageCost);
                break;
            case nameof(AppState.UsageStatus):
                if (_appState?.UsageStatus != null) UpdateUsageStatus(_appState.UsageStatus);
                break;
        }
    }

    public void UpdateUsage(GatewayUsageInfo usage)
    {
        RequestCountText.Text = usage.RequestCount.ToString();
        // Note: TotalCostText and TokenCountText are owned by UpdateUsageCost
        // (period-scoped), not UpdateUsage (all-time). Writing them from both
        // sources caused a race where the last response to arrive won — see
        // Hanselman review #1 (HIGH).
    }

    public void UpdateUsageCost(GatewayCostUsageInfo cost)
    {
        if (cost.Days != _currentPeriodDays)
            return;

        if (cost.UpdatedAt < _lastAppliedUsageCostUpdatedAtUtc)
            return;

        _lastAppliedUsageCostUpdatedAtUtc = cost.UpdatedAt;
        TotalCostText.Text = $"${cost.Totals.TotalCost:F2}";
        TokenCountText.Text = FormatLargeNumber(cost.Totals.TotalTokens);

        DailyListView.ItemsSource = cost.Daily.Select(d => new DailyRow
        {
            Date = d.Date,
            Cost = $"${d.TotalCost:F2}",
        }).ToList();
        _dailyCostLoading.Complete(cost.Daily.Count);
        UpdateDailyCostLoadingVisuals();
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        ProviderCountText.Text = status.Providers.Count.ToString();
        ProviderListView.ItemsSource = status.Providers.Select(p => new ProviderRow
        {
            Name = p.DisplayName,
            Plan = p.Plan ?? "",
            Usage = p.Windows.Count > 0 ? $"{p.Windows[0].UsedPercent:F0}% used" : "",
            Status = p.Error ?? "",
        }).ToList();

        bool hasProviders = status.Providers.Count > 0;
        _providerLoading.Complete(status.Providers.Count);
        UpdateProviderLoadingVisuals();
    }

    private void OnPeriodSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var days = ReferenceEquals(sender.SelectedItem, Period30DaysItem) ? 30 : 7;
        SelectPeriod(days);
    }

    private void SelectPeriod(int days)
    {
        if (days == _currentPeriodDays) return;
        _currentPeriodDays = days;
        _lastAppliedUsageCostUpdatedAtUtc = DateTime.MinValue;
        DailyListView.ItemsSource = null;
        TotalCostText.Text = "—";
        TokenCountText.Text = "—";
        _dailyCostLoading.BeginInitialRefresh();
        UpdateDailyCostLoadingVisuals();

        if (CurrentApp.GatewayClient != null)
        {
            _ = CurrentApp.GatewayClient.RequestUsageCostAsync(days);
        }
        else
        {
            _dailyCostLoading.Fail();
            ShowDisconnected();
            UpdateDailyCostLoadingVisuals();
        }
    }

    private void UpdateDailyCostLoadingVisuals()
    {
        DailyLoadingPanel.Visibility = _dailyCostLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
        DailyListView.Visibility = _dailyCostLoading.ShouldShowContent ? Visibility.Visible : Visibility.Collapsed;
        DailyEmptyText.Visibility = _dailyCostLoading.ShouldShowEmpty ? Visibility.Visible : Visibility.Collapsed;
        PeriodSelector.IsEnabled = _dailyCostLoading.CanEdit;
    }

    private void UpdateProviderLoadingVisuals()
    {
        ProviderLoadingPanel.Visibility = _providerLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
        ProviderListView.Visibility = _providerLoading.ShouldShowContent ? Visibility.Visible : Visibility.Collapsed;
        ProviderEmptyText.Visibility = _providerLoading.ShouldShowEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDisconnected()
    {
        ConnectionInfoBar.Title = "Gateway disconnected";
        ConnectionInfoBar.Message = "Connect to a gateway to load usage data.";
        ConnectionInfoBar.Severity = InfoBarSeverity.Warning;
        ConnectionInfoBar.IsOpen = true;
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
        public string Plan { get; set; } = "";
        public string Usage { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private class DailyRow
    {
        public string Date { get; set; } = "";
        public string Cost { get; set; } = "";
    }
}
