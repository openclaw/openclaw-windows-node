using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Windows;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class BindingsPage : Page
{
    private HubWindow? _hub;

    public BindingsPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        // Use cached config if available
        if (hub.LastConfig.HasValue)
            ParseBindings(hub.LastConfig.Value);
        // Request fresh config
        if (hub.GatewayClient != null)
            _ = hub.GatewayClient.RequestConfigAsync();
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
            if (config.ValueKind == JsonValueKind.Object &&
                config.TryGetProperty("bindings", out var bindingsEl) &&
                bindingsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in bindingsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var vm = new BindingViewModel();

                    if (item.TryGetProperty("channel", out var ch))
                        vm.Channel = ch.GetString() ?? "";
                    if (item.TryGetProperty("accountId", out var acc))
                        vm.AccountId = acc.GetString() ?? "*";
                    if (item.TryGetProperty("agentId", out var agent))
                        vm.AgentId = agent.GetString() ?? "main";
                    if (item.TryGetProperty("peer", out var peer))
                        vm.Peer = peer.GetString();
                    if (item.TryGetProperty("priority", out var prio) && prio.TryGetInt32(out var prioVal))
                        vm.Priority = prioVal;

                    bindings.Add(vm);
                }
            }
        }
        catch { }

        if (bindings.Count == 0)
        {
            SingleAgentInfoBar.IsOpen = true;
            BindingsList.ItemsSource = null;
        }
        else
        {
            SingleAgentInfoBar.IsOpen = false;
            BindingsList.ItemsSource = bindings;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hub?.GatewayClient != null)
        {
            _ = _hub.GatewayClient.RequestConfigAsync();
        }
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
