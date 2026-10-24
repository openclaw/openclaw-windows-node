using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class BindingsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;
    private IOperatorGatewayClient? _trackedClient;
    private readonly AsyncListLoadingState _bindingsLoading = new();
    private bool _observing;
    private int _configGeneration;
    private bool _configFailureVisible;

    public BindingsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _observing = false;
            EndConfigWatch();
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            if (CurrentApp.ConnectionManager != null)
                CurrentApp.ConnectionManager.OperatorClientChanged -= OnOperatorClientChanged;
            DetachClient();
        };
    }

    public void Initialize()
    {
        _observing = true;
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        if (CurrentApp.ConnectionManager != null)
        {
            CurrentApp.ConnectionManager.OperatorClientChanged -= OnOperatorClientChanged;
            CurrentApp.ConnectionManager.OperatorClientChanged += OnOperatorClientChanged;
        }

        // Use cached config if available
        if (_appState?.Config.HasValue == true)
        {
            ParseBindings(_appState.Config.Value);
            _bindingsLoading.BeginRefresh();
            UpdateLoadingVisuals();
        }
        else
        {
            _bindingsLoading.BeginInitialRefresh();
            UpdateLoadingVisuals();
        }

        // A non-null client can still be disconnected. Wait until the
        // handshake is ready so this page's own config.get can finish.
        var client = CurrentApp.GatewayClient;
        AttachClient(client);
        if (client is { IsConnectedToGateway: true })
        {
            RequestConfig(client);
        }
        else
        {
            EndConfigWatch();
            _bindingsLoading.Fail();
            ShowDisconnected();
            UpdateLoadingVisuals();
        }
    }

    private void OnOpenConnectionClick(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Config):
                if (_appState!.Config.HasValue)
                    UpdateConfig(_appState.Config.Value);
                break;
        }
    }

    public void UpdateConfig(JsonElement config)
    {
        ParseBindings(config);
    }

    private void ParseBindings(JsonElement config)
    {
        var bindings = new List<BindingViewModel>();

        try
        {
            foreach (var route in ConfigEditorModel.ReadBindingRoutes(config))
            {
                bindings.Add(new BindingViewModel
                {
                    Channel = route.Channel,
                    AccountId = route.AccountId,
                    AgentId = route.AgentId,
                    Peer = route.Peer,
                    Priority = route.Priority,
                });
            }
        }
        catch (Exception ex)
        {
            EndConfigWatch();
            _bindingsLoading.Fail();
            ShowLoadFailure(ex);
            UpdateLoadingVisuals();
            return;
        }

        EndConfigWatch();
        if (_configFailureVisible)
        {
            _configFailureVisible = false;
            ConnectionInfoBar.IsOpen = false;
        }

        if (bindings.Count == 0)
        {
            BindingsList.ItemsSource = null;
        }
        else
        {
            BindingsList.ItemsSource = bindings;
        }

        _bindingsLoading.Complete(bindings.Count);
        UpdateLoadingVisuals();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client is { IsConnectedToGateway: true })
        {
            _bindingsLoading.BeginRefresh();
            UpdateLoadingVisuals();
            RequestConfig(client);
        }
        else
        {
            EndConfigWatch();
            _bindingsLoading.Fail();
            ShowDisconnected();
            UpdateLoadingVisuals();
        }
    }

    private void RequestConfig(IOperatorGatewayClient client)
    {
        ConnectionInfoBar.IsOpen = false;
        _configFailureVisible = false;
        var generation = BeginConfigWatch();
        _ = LoadConfigAsync(client, generation);
    }

    private int BeginConfigWatch() => ++_configGeneration;

    private void EndConfigWatch() => _configGeneration++;

    private async Task LoadConfigAsync(IOperatorGatewayClient client, int generation)
    {
        try
        {
            var payload = await client.SendWizardRequestAsync(
                "config.get",
                parameters: null,
                timeoutMs: BindingsConfigLoadDeadline.CurrentMilliseconds).ConfigureAwait(false);
            ApplyConfigLoad(client, generation, payload, failed: false);
        }
        catch (Exception)
        {
            ApplyConfigLoad(client, generation, default, failed: true);
        }
    }

    private void ApplyConfigLoad(
        IOperatorGatewayClient client,
        int generation,
        JsonElement payload,
        bool failed)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher == null)
            return;

        dispatcher.TryEnqueue(() =>
        {
            if (!_observing || generation != _configGeneration || !ReferenceEquals(_trackedClient, client))
                return;

            if (failed)
            {
                if (!_bindingsLoading.IsRefreshing)
                    return;

                EndConfigWatch();
                _bindingsLoading.Fail();
                ShowConfigRequestFailure();
                UpdateLoadingVisuals();
                return;
            }

            if (_appState != null)
                _appState.Config = payload;
            else
                UpdateConfig(payload);
        });
    }

    private void AttachClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(_trackedClient, client)) return;
        DetachClient();
        _trackedClient = client;
        if (_trackedClient != null)
            _trackedClient.StatusChanged += OnClientStatusChanged;
    }

    private void DetachClient()
    {
        if (_trackedClient == null) return;
        _trackedClient.StatusChanged -= OnClientStatusChanged;
        _trackedClient = null;
    }

    private void OnClientStatusChanged(object? sender, ConnectionStatus status)
    {
        if (sender is not IOperatorGatewayClient client) return;
        var dispatcher = DispatcherQueue;
        if (dispatcher == null) return;
        dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_trackedClient, client)) return;
            var connected = client.IsConnectedToGateway;
            // A queued Connecting or Disconnected callback can run after the client has reconnected.
            // Only the Connected event recovers, so one reconnect requests the config once.
            if (connected && status != ConnectionStatus.Connected) return;
            switch (BindingsConnectionRecovery.DecideQueued(_observing, connected))
            {
                case BindingsConnectionRecovery.Decision.Recover:
                    RecoverConnectedClient(client);
                    break;
                case BindingsConnectionRecovery.Decision.ShowDisconnected:
                    EndConfigWatch();
                    _bindingsLoading.Fail();
                    ShowDisconnected();
                    UpdateLoadingVisuals();
                    break;
            }
        });
    }

    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher == null) return;
        dispatcher.TryEnqueue(() =>
        {
            var connected = e.NewClient is { IsConnectedToGateway: true };
            if (BindingsConnectionRecovery.DecideQueued(_observing, connected) ==
                BindingsConnectionRecovery.Decision.Ignore)
            {
                return;
            }

            AttachClient(e.NewClient);
            if (connected)
                RecoverConnectedClient(e.NewClient!);
            else
            {
                EndConfigWatch();
                _bindingsLoading.Fail();
                ShowDisconnected();
                UpdateLoadingVisuals();
            }
        });
    }

    private void RecoverConnectedClient(IOperatorGatewayClient client)
    {
        if (!_bindingsLoading.HasLoaded) _bindingsLoading.BeginInitialRefresh();
        else _bindingsLoading.BeginRefresh();
        UpdateLoadingVisuals();
        RequestConfig(client);
    }

    private void UpdateLoadingVisuals()
    {
        LoadingState.Visibility = _bindingsLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
        BindingsList.Visibility = _bindingsLoading.ShouldShowContent ? Visibility.Visible : Visibility.Collapsed;
        SingleAgentInfoBar.IsOpen = _bindingsLoading.ShouldShowEmpty;
        RefreshButton.IsEnabled = CurrentApp.GatewayClient is { IsConnectedToGateway: true } && _bindingsLoading.CanEdit;
    }

    private void ShowDisconnected()
    {
        ConnectionInfoBar.Title = LocalizationHelper.GetString("BindingsPage_GatewayDisconnected.Title");
        ConnectionInfoBar.Message = LocalizationHelper.GetString("BindingsPage_GatewayDisconnected.Message");
        ConnectionInfoBar.Severity = InfoBarSeverity.Warning;
        ConnectionInfoBar.IsOpen = true;
    }

    private void ShowLoadFailure(Exception ex)
    {
        _configFailureVisible = false;
        ConnectionInfoBar.Title = LocalizationHelper.GetString("BindingsPage_CouldNotLoadBindings");
        ConnectionInfoBar.Message = ex.Message;
        ConnectionInfoBar.Severity = InfoBarSeverity.Error;
        ConnectionInfoBar.IsOpen = true;
    }

    private void ShowConfigRequestFailure()
    {
        _configFailureVisible = true;
        ConnectionInfoBar.Title = LocalizationHelper.GetString("BindingsPage_CouldNotLoadBindings");
        ConnectionInfoBar.Message = LocalizationHelper.GetString("BindingsPage_ConfigRequestFailed.Message");
        ConnectionInfoBar.Severity = InfoBarSeverity.Error;
        ConnectionInfoBar.IsOpen = true;
    }

    private class BindingViewModel
    {
        public string Channel { get; set; } = "";
        public string AccountId { get; set; } = "*";
        public string? Peer { get; set; }
        public string AgentId { get; set; } = "main";
        public int? Priority { get; set; }

        public string ChannelIcon => Channel switch
        {
            "whatsapp" => "📱",
            "telegram" => "✈️",
            "discord" => "🎮",
            "slack" => "💼",
            "signal" => "🔒",
            _ => "📡"
        };

        public string PeerDisplay => string.IsNullOrEmpty(Peer) ? "All" : Peer;
        public string RouteDisplay => $"{ChannelIcon} {Channel} ({AccountId}) → {AgentId}";
    }
}
