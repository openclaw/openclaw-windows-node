using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class GatewayContainerPage : Page
{
    private HubWindow? _hub;

    public GatewayContainerPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        EnsureCurrentTabLoaded();
    }

    /// <summary>Select a child tab by tag name (e.g. "channels", "nodes").</summary>
    public void SelectTab(string tag)
    {
        foreach (var item in GatewayTabs.TabItems)
        {
            if (item is TabViewItem tab && tab.Tag as string == tag)
            {
                GatewayTabs.SelectedItem = tab;
                return;
            }
        }
    }

    private void GatewayTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EnsureCurrentTabLoaded();
    }

    private void EnsureCurrentTabLoaded()
    {
        if (_hub == null) return;
        if (GatewayTabs.SelectedItem is not TabViewItem tab) return;

        var tag = tab.Tag as string;
        switch (tag)
        {
            case "channels":
                NavigateAndInit<ChannelsPage>(ChannelsFrame, p => p.Initialize(_hub));
                break;
            case "nodes":
                NavigateAndInit<NodesPage>(NodesFrame, p =>
                {
                    p.Initialize(_hub);
                    if (_hub.LastNodePairList != null) p.UpdatePairingRequests(_hub.LastNodePairList);
                    if (_hub.LastDevicePairList != null) p.UpdateDevicePairingRequests(_hub.LastDevicePairList);
                });
                break;
            case "instances":
                NavigateAndInit<InstancesPage>(InstancesFrame, p =>
                {
                    p.Initialize(_hub);
                    if (_hub.LastPresence != null) p.UpdatePresenceData(_hub.LastPresence);
                });
                break;
            case "config":
                NavigateAndInit<ConfigPage>(ConfigFrame, p =>
                {
                    p.Initialize(_hub);
                    if (_hub.LastConfig.HasValue) p.UpdateConfig(_hub.LastConfig.Value);
                });
                break;
            case "usage":
                NavigateAndInit<UsagePage>(UsageFrame, p => p.Initialize(_hub));
                break;
            case "bindings":
                NavigateAndInit<BindingsPage>(BindingsFrame, p => p.Initialize(_hub));
                break;
        }
    }

    private static void NavigateAndInit<T>(Frame frame, Action<T> init) where T : Page
    {
        if (frame.Content is T existing)
        {
            init(existing);
            return;
        }
        frame.Navigate(typeof(T));
        if (frame.Content is T page) init(page);
    }

    // --- Forwarding methods called by HubWindow ---

    public void ForwardUpdateChannelHealth(ChannelHealth[] channels)
    {
        if (ChannelsFrame.Content is ChannelsPage p) p.UpdateChannels(channels);
    }

    public void ForwardUpdateNodes(GatewayNodeInfo[] nodes)
    {
        if (NodesFrame.Content is NodesPage p) p.UpdateNodes(nodes);
    }

    public void ForwardUpdateNodePairList(PairingListInfo data)
    {
        if (NodesFrame.Content is NodesPage p) p.UpdatePairingRequests(data);
    }

    public void ForwardUpdateDevicePairList(DevicePairingListInfo data)
    {
        if (NodesFrame.Content is NodesPage p) p.UpdateDevicePairingRequests(data);
    }

    public void ForwardUpdatePresence(PresenceEntry[] data)
    {
        if (InstancesFrame.Content is InstancesPage p) p.UpdatePresenceData(data);
    }

    public void ForwardUpdateConfig(JsonElement config)
    {
        if (ConfigFrame.Content is ConfigPage p) p.UpdateConfig(config);
        if (BindingsFrame.Content is BindingsPage b) b.UpdateConfig(config);
    }

    public void ForwardUpdateUsage(GatewayUsageInfo usage)
    {
        if (UsageFrame.Content is UsagePage p) p.UpdateUsage(usage);
    }

    public void ForwardUpdateUsageCost(GatewayCostUsageInfo cost)
    {
        if (UsageFrame.Content is UsagePage p) p.UpdateUsageCost(cost);
    }

    public void ForwardUpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        if (UsageFrame.Content is UsagePage p) p.UpdateUsageStatus(status);
    }
}
