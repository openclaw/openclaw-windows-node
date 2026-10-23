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
using System.Threading.Tasks;

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
    private DateTime _lastAppliedUsageCostUpdatedAtUtc = DateTime.MinValue;
    private long _refreshRevision;

    private const string DailyEmptyMessage = "No daily usage for this period";
    private const string ProviderEmptyMessage = "No providers configured";
    private static string LoadFailedMessage => LocalizationHelper.GetString("UsagePage_LoadFailed.Message");

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

        // A non-null client may still be reconnecting.
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
        // AppState's usage cache is not scoped to a Gateway or cost request.
        _dailyCostLoading.BeginInitialRefresh();
        _providerLoading.BeginInitialRefresh();
        UpdateDailyCostLoadingVisuals();
        UpdateProviderLoadingVisuals();
        RequestRefresh(client);
    }

    private void AttachClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(_trackedClient, client)) return;
        DetachClient();
        _dailyCostLoading.BeginInitialRefresh();
        _providerLoading.BeginInitialRefresh();
        DailyListView.ItemsSource = null;
        ProviderListView.ItemsSource = null;
        TotalCostText.Text = "—";
        TokenCountText.Text = "—";
        ProviderCountText.Text = "—";
        _trackedClient = client;
        if (_trackedClient != null)
        {
            _trackedClient.StatusChanged += OnClientStatusChanged;
        }
    }

    private void DetachClient()
    {
        _refreshRevision++;
        _lastAppliedUsageCostUpdatedAtUtc = DateTime.MinValue;
        _lastAppliedUsageCostUpdatedAtUtc = DateTime.MinValue;
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
        var dispatcher = DispatcherQueue;
        if (dispatcher == null) return;
        dispatcher.TryEnqueue(() =>
        {
            if (_trackedClient != client) return;
            if (!client.IsConnectedToGateway)
            {
                _refreshRevision++;
                _providerLoading.Fail();
                _dailyCostLoading.Fail();
                ShowDisconnected();
                UpdateProviderLoadingVisuals();
                UpdateDailyCostLoadingVisuals();
                return;
            }
            RecoverConnectedClient(client);
        });
    }

    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher == null) return;
        dispatcher.TryEnqueue(() =>
        {
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

    private void RecoverConnectedClient(IOperatorGatewayClient client)
    {
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
        _ = RefreshAsync(client);
    }

    private async Task RefreshAsync(IOperatorGatewayClient client)
    {
        var revision = ++_refreshRevision;
        var epoch = client.SessionMutationConnectionEpoch;
        var days = _currentPeriodDays;
        try
        {
            var result = await client.RefreshUsageAsync(days);
            if (!UsageCostApplicationPolicy.IsCurrentRefresh(
                revision, _refreshRevision, days, _currentPeriodDays,
                ReferenceEquals(client, _trackedClient), client.IsConnectedToGateway,
                epoch, client.SessionMutationConnectionEpoch))
                return;

            if (result.Legacy != null) UpdateUsage(result.Legacy);
            if (result.Status != null) UpdateUsageStatus(result.Status);
            else _providerLoading.Fail();
            var costApplied = result.Cost != null && UpdateUsageCost(result.Cost);
            if (!costApplied) _dailyCostLoading.Fail();
            if (result.StatusError != null || result.CostError != null || !costApplied)
            {
                ConnectionInfoBar.Title = LocalizationHelper.GetString("UsagePage_TextBlock_10.Text");
                ConnectionInfoBar.Message = string.Join(Environment.NewLine,
                    new[] { result.StatusError, result.CostError ?? (!costApplied ? LoadFailedMessage : null) }
                        .Where(error => error != null));
                ConnectionInfoBar.Severity = InfoBarSeverity.Warning;
                ConnectionInfoBar.IsOpen = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or
            OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            if (revision != _refreshRevision || !ReferenceEquals(client, _trackedClient) ||
                epoch != client.SessionMutationConnectionEpoch) return;
            _providerLoading.Fail();
            _dailyCostLoading.Fail();
            ShowDisconnected();
            ConnectionInfoBar.Title = LocalizationHelper.GetString("UsagePage_TextBlock_10.Text");
            ConnectionInfoBar.Message = ex.Message;
        }
        finally
        {
            if (revision == _refreshRevision && ReferenceEquals(client, _trackedClient))
            {
                UpdateProviderLoadingVisuals();
                UpdateDailyCostLoadingVisuals();
            }
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

    public bool UpdateUsageCost(GatewayCostUsageInfo cost)
    {
        if (!UsageCostApplicationPolicy.CompleteRefresh(
            cost.Days, _currentPeriodDays, cost.UpdatedAt, _lastAppliedUsageCostUpdatedAtUtc,
            cost.Daily.Count, _dailyCostLoading))
            return false;

        // Only this page's current response reaches here. Preserve compatibility
        // with older Gateways that default the period or count days inclusively.
        if (cost.Days > 0 && cost.Days != _currentPeriodDays)
        {
            SyncSelectorToServerDays(cost.Days);
        }

        _lastAppliedUsageCostUpdatedAtUtc = cost.UpdatedAt;
        ConnectionInfoBar.IsOpen = false;
        TotalCostText.Text = $"${cost.Totals.TotalCost:F2}";
        TokenCountText.Text = FormatLargeNumber(cost.Totals.TotalTokens);

        DailyListView.ItemsSource = cost.Daily.Select(d => new DailyRow
        {
            Date = d.Date,
            Cost = $"${d.TotalCost:F2}",
        }).ToList();
        UpdateDailyCostLoadingVisuals();
        return true;
    }

    private void SyncSelectorToServerDays(int days)
    {
        // Only the two SelectorBar items are valid targets; ignore anything
        // else (e.g. server-defaulted 14 days) and just keep the user's pick.
        if (days != 7 && days != 30) return;
        _currentPeriodDays = days;
        var target = days == 30 ? Period30DaysItem : Period7DaysItem;
        if (!ReferenceEquals(PeriodSelector.SelectedItem, target))
        {
            PeriodSelector.SelectionChanged -= OnPeriodSelectionChanged;
            try { PeriodSelector.SelectedItem = target; }
            finally { PeriodSelector.SelectionChanged += OnPeriodSelectionChanged; }
        }
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
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
        DailyListView.ItemsSource = null;
        TotalCostText.Text = "—";
        TokenCountText.Text = "—";
        _dailyCostLoading.BeginInitialRefresh();
        UpdateDailyCostLoadingVisuals();

        var client = CurrentApp.GatewayClient;
        if (client != null && client.IsConnectedToGateway)
        {
            RequestRefresh(client);
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
        DailyEmptyText.Text = failed ? LoadFailedMessage : DailyEmptyMessage;
        DailyEmptyText.Visibility = (_dailyCostLoading.ShouldShowEmpty || failed) ? Visibility.Visible : Visibility.Collapsed;
        PeriodSelector.IsEnabled = _dailyCostLoading.CanEdit;
    }

    private void UpdateProviderLoadingVisuals()
    {
        ProviderLoadingPanel.Visibility = _providerLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
        ProviderListView.Visibility = _providerLoading.ShouldShowContent ? Visibility.Visible : Visibility.Collapsed;
        bool failed = !_providerLoading.IsRefreshing && !_providerLoading.HasLoaded;
        ProviderEmptyText.Text = failed ? LoadFailedMessage : ProviderEmptyMessage;
        ProviderEmptyText.Visibility = (_providerLoading.ShouldShowEmpty || failed) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDisconnected()
    {
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
