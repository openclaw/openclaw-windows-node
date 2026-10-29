using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class UsagePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;
    private IOperatorGatewayClient? _trackedClient;
    // Default matches the XAML-selected Period7DaysItem (IsSelected="True").
    private int _currentPeriodDays = 7;
    private readonly AsyncListLoadingState _providerLoading = new();
    private readonly AsyncListLoadingState _dailyCostLoading = new();
    private bool _isUnsupportedRangeWarningVisible;
    private bool _hasMatchingUsageCost;
    private DateTime _lastAppliedUsageCostUpdatedAtUtc = DateTime.MinValue;

    private const string DailyEmptyMessage = "No daily usage for this period";
    private const string ProviderEmptyMessage = "No providers configured";
    private const string DisconnectedListMessage = "Couldn't load. Check your gateway connection.";

    public UsagePage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            if (CurrentApp.ConnectionManager != null)
                CurrentApp.ConnectionManager.OperatorClientChanged -= OnOperatorClientChanged;
            DetachClient();
        };
    }

    public void Initialize()
    {
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        if (CurrentApp.ConnectionManager != null)
        {
            CurrentApp.ConnectionManager.OperatorClientChanged -= OnOperatorClientChanged;
            CurrentApp.ConnectionManager.OperatorClientChanged += OnOperatorClientChanged;
        }

        var client = CurrentApp.GatewayClient;
        AttachClient(client);

        // A non-null client may still be disconnected (e.g. WebSocket reconnecting).
        // The underlying RequestUsage*/RequestUsageCost*/RequestUsageStatus calls
        // silently no-op when !IsConnectedToGateway, so without this check the
        // page would spin its progress rings forever — see user reports of
        // "page not loading, no values".
        if (client == null || !client.IsConnectedToGateway)
        {
            _providerLoading.Fail();
            _dailyCostLoading.Fail();
            ShowDisconnected();
            UpdateProviderLoadingVisuals();
            UpdateDailyCostLoadingVisuals();
            return;
        }

        ConnectionInfoBar.IsOpen = false;
        // Apply cached data immediately, then request fresh.
        if (_appState?.Usage != null) UpdateUsage(_appState.Usage);
        // Only apply cached cost data when its period matches the current
        // selection — otherwise the daily list briefly shows e.g. 30-day
        // data while the selector reads "7 Days".
        var cachedCost = _appState?.GetUsageCostForPeriod(_currentPeriodDays);
        if (cachedCost == null &&
            _appState?.UsageCost is { } latestCost &&
            ShouldApplyUsageCost(latestCost))
        {
            cachedCost = latestCost;
        }
        if (cachedCost != null)
        {
            UpdateUsageCost(cachedCost);
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
        RequestRefresh(client);
    }

    private void AttachClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(_trackedClient, client)) return;
        DetachClient();
        _trackedClient = client;
        if (_trackedClient != null)
        {
            _trackedClient.StatusChanged += OnClientStatusChanged;
        }
    }

    private void DetachClient()
    {
        if (_trackedClient != null)
        {
            _trackedClient.StatusChanged -= OnClientStatusChanged;
            _trackedClient = null;
        }
    }

    private void OnClientStatusChanged(object? sender, ConnectionStatus status)
    {
        // Recover automatically when the gateway comes online while the page
        // is open (otherwise the user is stuck on the disconnected info bar
        // with stale cards until they navigate away and back).
        if (sender is not IOperatorGatewayClient client) return;
        if (!client.IsConnectedToGateway) return;

        var dispatcher = DispatcherQueue;
        if (dispatcher == null) return;
        dispatcher.TryEnqueue(() =>
        {
            if (_trackedClient != client) return;
            RecoverConnectedClient(client);
        });
    }

    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher == null) return;
        dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(e.OldClient, e.NewClient))
            {
                ResetUsageCostState();
            }

            AttachClient(e.NewClient);
            if (e.NewClient is { IsConnectedToGateway: true })
            {
                RecoverConnectedClient(e.NewClient);
            }
            else
            {
                _providerLoading.Fail();
                _dailyCostLoading.Fail();
                ShowDisconnected();
                UpdateProviderLoadingVisuals();
                UpdateDailyCostLoadingVisuals();
            }
        });
    }

    private void ResetUsageCostState()
    {
        _lastAppliedUsageCostUpdatedAtUtc = DateTime.MinValue;
        _hasMatchingUsageCost = false;
        _isUnsupportedRangeWarningVisible = false;
        DailyListView.ItemsSource = null;
        TotalCostText.Text = "—";
        TokenCountText.Text = "—";
        _dailyCostLoading.BeginInitialRefresh();
        _dailyCostLoading.Fail();
        ShowDisconnected();
        UpdateDailyCostLoadingVisuals();
    }

    private void RecoverConnectedClient(IOperatorGatewayClient client)
    {
        _isUnsupportedRangeWarningVisible = false;
        OpenConnectionButton.Visibility = Visibility.Collapsed;
        ConnectionInfoBar.IsOpen = false;
        if (!_dailyCostLoading.HasLoaded) _dailyCostLoading.BeginInitialRefresh();
        else _dailyCostLoading.BeginRefresh();
        if (!_providerLoading.HasLoaded) _providerLoading.BeginInitialRefresh();
        else _providerLoading.BeginRefresh();
        UpdateDailyCostLoadingVisuals();
        UpdateProviderLoadingVisuals();
        RequestRefresh(client);
    }

    private void RequestRefresh(IOperatorGatewayClient client)
    {
        _ = client.RequestUsageStatusAsync();
        _ = client.RequestUsageCostAsync(_currentPeriodDays);
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
                if (_appState?.UsageCost is { } usageCost) UpdateUsageCost(usageCost);
                else ResetUsageCostState();
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
        switch (UsageCostApplicationPolicy.Decide(
            cost.Days,
            _currentPeriodDays,
            _hasMatchingUsageCost,
            cost.UpdatedAt,
            _lastAppliedUsageCostUpdatedAtUtc))
        {
            case UsageCostApplicationPolicy.Decision.Ignore:
            case UsageCostApplicationPolicy.Decision.IgnoreStale:
                _dailyCostLoading.Complete(_dailyCostLoading.ItemCount);
                UpdateDailyCostLoadingVisuals();
                return;
            case UsageCostApplicationPolicy.Decision.StopForUnsupportedRange:
                StopForUnsupportedRange(cost);
                return;
        }

        _lastAppliedUsageCostUpdatedAtUtc = cost.UpdatedAt;
        _hasMatchingUsageCost = true;
        _isUnsupportedRangeWarningVisible = false;
        ConnectionInfoBar.IsOpen = false;
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

    private bool ShouldApplyUsageCost(GatewayCostUsageInfo cost)
    {
        return UsageCostApplicationPolicy.ShouldApply(cost.Days, _currentPeriodDays);
    }

    private void StopForUnsupportedRange(GatewayCostUsageInfo cost)
    {
        _hasMatchingUsageCost = false;
        _isUnsupportedRangeWarningVisible = true;
        OpenConnectionButton.Visibility = Visibility.Collapsed;
        ConnectionInfoBar.Title = LocalizationHelper.GetString("UsagePage_UnsupportedRange.Title");
        ConnectionInfoBar.Message = string.Format(
            LocalizationHelper.GetString("UsagePage_UnsupportedRange.Message"),
            cost.Days);
        ConnectionInfoBar.Severity = InfoBarSeverity.Warning;
        ConnectionInfoBar.IsOpen = true;
        _dailyCostLoading.Complete(0);
        UpdateDailyCostLoadingVisuals();
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        if (!_isUnsupportedRangeWarningVisible)
            ConnectionInfoBar.IsOpen = false;
        ProviderCountText.Text = status.Providers.Count.ToString();
        ProviderListView.ItemsSource = status.Providers.Select(p => new ProviderRow
        {
            Name = p.DisplayName,
            Plan = p.Plan ?? "",
            Usage = p.Windows.Count > 0 ? $"{p.Windows[0].UsedPercent:F0}% used" : "",
            Status = p.Error ?? "",
        }).ToList();

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
        _isUnsupportedRangeWarningVisible = false;
        _hasMatchingUsageCost = false;
        ConnectionInfoBar.IsOpen = false;
        DailyListView.ItemsSource = null;
        TotalCostText.Text = "—";
        TokenCountText.Text = "—";
        var cachedCost = _appState?.GetUsageCostForPeriod(days);
        if (cachedCost == null &&
            _appState?.UsageCost is { } latestCost &&
            ShouldApplyUsageCost(latestCost))
        {
            cachedCost = latestCost;
        }
        if (cachedCost != null)
        {
            UpdateUsageCost(cachedCost);
            _dailyCostLoading.BeginRefresh();
        }
        else
        {
            _dailyCostLoading.BeginInitialRefresh();
        }
        UpdateDailyCostLoadingVisuals();

        var client = CurrentApp.GatewayClient;
        if (client != null && client.IsConnectedToGateway)
        {
            _ = client.RequestUsageCostAsync(days);
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
        // After Fail() the state is !IsRefreshing && !HasLoaded, which leaves the
        // card visually empty (no spinner, no rows, no message). Surface a
        // message in that case so the page never looks frozen.
        bool failed = !_dailyCostLoading.IsRefreshing && !_dailyCostLoading.HasLoaded;
        DailyEmptyText.Text = failed ? DisconnectedListMessage : DailyEmptyMessage;
        bool showEmpty = !_isUnsupportedRangeWarningVisible
            && (_dailyCostLoading.ShouldShowEmpty || failed);
        DailyEmptyText.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        PeriodSelector.IsEnabled = _dailyCostLoading.CanEdit;
    }

    private void UpdateProviderLoadingVisuals()
    {
        ProviderLoadingPanel.Visibility = _providerLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
        ProviderListView.Visibility = _providerLoading.ShouldShowContent ? Visibility.Visible : Visibility.Collapsed;
        bool failed = !_providerLoading.IsRefreshing && !_providerLoading.HasLoaded;
        ProviderEmptyText.Text = failed ? DisconnectedListMessage : ProviderEmptyMessage;
        ProviderEmptyText.Visibility = (_providerLoading.ShouldShowEmpty || failed) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDisconnected()
    {
        _isUnsupportedRangeWarningVisible = false;
        OpenConnectionButton.Visibility = Visibility.Visible;
        ConnectionInfoBar.Title = LocalizationHelper.GetString("UsagePage_GatewayDisconnected.Title");
        ConnectionInfoBar.Message = LocalizationHelper.GetString("UsagePage_GatewayDisconnected.Message");
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
