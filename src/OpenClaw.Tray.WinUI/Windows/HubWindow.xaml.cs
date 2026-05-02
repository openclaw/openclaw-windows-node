using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using OpenClawTray.Services;
using System;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class HubWindow : WindowEx
{
    public bool IsClosed { get; private set; }

    // Shared state accessible by pages
    public SettingsManager? Settings { get; set; }
    public OpenClawGatewayClient? GatewayClient { get; set; }
    public ConnectionStatus CurrentStatus { get; set; }
    public Action<string?>? OpenDashboardAction { get; set; }
    public Action? ConnectAction { get; set; }
    public Action? DisconnectAction { get; set; }

    // Cached gateway data — pages read these on navigation
    public SessionInfo[]? LastSessions { get; private set; }
    public ChannelHealth[]? LastChannels { get; private set; }
    public GatewayUsageInfo? LastUsage { get; private set; }
    public GatewayCostUsageInfo? LastUsageCost { get; private set; }
    public GatewayUsageStatusInfo? LastUsageStatus { get; private set; }
    public GatewayNodeInfo[]? LastNodes { get; private set; }

    public System.Text.Json.JsonElement? LastConfig { get; private set; }

    // Event for settings saved (App.xaml.cs subscribes)
    public event EventHandler? SettingsSaved;

    public void RaiseSettingsSaved() => SettingsSaved?.Invoke(this, EventArgs.Empty);

    public HubWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Closed += (s, e) => IsClosed = true;

        this.SetWindowSize(900, 650);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        // Don't select a nav item here — Settings/GatewayClient aren't set yet.
        // ShowHub() in App.xaml.cs calls NavigateToDefault() after setting properties.
    }

    /// <summary>
    /// Navigate to the default page (Chat). Call after setting Settings/GatewayClient.
    /// </summary>
    public void NavigateToDefault()
    {
        if (ContentFrame.Content == null)
        {
            NavView.SelectedItem = NavView.MenuItems[1]; // Chat
        }
    }

    /// <summary>
    /// Navigate to a specific page by tag name (e.g. "home", "chat", "settings").
    /// </summary>
    public void NavigateTo(string tag)
    {
        var pageType = TagToPageType(tag);
        if (pageType == null) return;

        foreach (var item in NavView.MenuItems)
            if (item is NavigationViewItem navItem && navItem.Tag as string == tag)
            { NavView.SelectedItem = navItem; return; }
        foreach (var item in NavView.FooterMenuItems)
            if (item is NavigationViewItem navItem && navItem.Tag as string == tag)
            { NavView.SelectedItem = navItem; return; }

        ContentFrame.Navigate(pageType);
    }

    public void UpdateStatus(ConnectionStatus status)
    {
        CurrentStatus = status;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                UpdateTitleBarStatus(status);
                if (ContentFrame?.Content is HomePage homePage)
                {
                    homePage.UpdateConnectionStatus(status, Settings?.GetEffectiveGatewayUrl());
                }
            });
        }
        catch { }
    }

    private void UpdateTitleBarStatus(ConnectionStatus status)
    {
        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            ConnectionStatus.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            ConnectionStatus.Error => (Microsoft.UI.Colors.Red, "Error"),
            _ => (Microsoft.UI.Colors.Gray, "Disconnected")
        };

        TitleStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        TitleStatusText.Text = text;

        // Add gateway version if available
        if (status == ConnectionStatus.Connected && GatewayClient != null)
        {
            var self = _lastGatewaySelf;
            if (self != null && !string.IsNullOrEmpty(self.ServerVersion))
                TitleStatusText.Text = $"Connected · v{self.ServerVersion}";
            if (self?.PresenceCount is > 0)
                TitleStatusText.Text += $" · {self.PresenceCount} clients";
        }
    }

    private GatewaySelfInfo? _lastGatewaySelf;

    public void UpdateGatewaySelf(GatewaySelfInfo self)
    {
        _lastGatewaySelf = self;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                UpdateTitleBarStatus(CurrentStatus);
            });
        }
        catch { }
    }

    public void UpdateSessions(SessionInfo[] sessions)
    {
        LastSessions = sessions;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is SessionsPage page) page.UpdateSessions(sessions);
            if (ContentFrame?.Content is HomePage home) home.UpdateSessionCount(sessions.Length);
        });
    }

    public void UpdateChannelHealth(ChannelHealth[] channels)
    {
        LastChannels = channels;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is ChannelsPage page) page.UpdateChannels(channels);
        });
    }

    public void UpdateUsage(GatewayUsageInfo usage)
    {
        LastUsage = usage;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage page) page.UpdateUsage(usage);
        });
    }

    public void UpdateUsageCost(GatewayCostUsageInfo cost)
    {
        LastUsageCost = cost;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage page) page.UpdateUsageCost(cost);
        });
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        LastUsageStatus = status;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage page) page.UpdateUsageStatus(status);
        });
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        LastNodes = nodes;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is NodesPage page) page.UpdateNodes(nodes);
            if (ContentFrame?.Content is HomePage home) home.UpdateNodes(nodes);
        });
    }

    public void UpdateCronList(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is CronPage page) page.UpdateFromGateway(data);
            });
        }
        catch { }
    }

    public void UpdateConfig(System.Text.Json.JsonElement config)
    {
        LastConfig = config;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is ConfigPage page) page.UpdateConfig(config);
        });
    }

    public void UpdateSkillsStatus(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is SkillsPage page) page.UpdateFromGateway(data);
            });
        }
        catch { }
    }

    // Agent events ring buffer (max 400, cached centrally)
    private const int MaxAgentEvents = 400;
    private readonly System.Collections.Generic.List<AgentEventInfo> _agentEvents = new();
    public System.Collections.Generic.IReadOnlyList<AgentEventInfo> LastAgentEvents => _agentEvents;

    public void ClearAgentEvents() => _agentEvents.Clear();

    public void UpdateAgentEvent(AgentEventInfo evt)
    {
        _agentEvents.Insert(0, evt);
        if (_agentEvents.Count > MaxAgentEvents)
            _agentEvents.RemoveRange(MaxAgentEvents, _agentEvents.Count - MaxAgentEvents);

        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is AgentEventsPage page) page.AddEvent(evt);
            });
        }
        catch { }
    }

    // Pairing data
    public PairingListInfo? LastNodePairList { get; private set; }
    public DevicePairingListInfo? LastDevicePairList { get; private set; }
    public ModelsListInfo? LastModelsList { get; private set; }

    public void UpdateNodePairList(PairingListInfo data)
    {
        LastNodePairList = data;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is NodesPage page) page.UpdatePairingRequests(data);
            });
        }
        catch { }
    }

    public void UpdateDevicePairList(DevicePairingListInfo data)
    {
        LastDevicePairList = data;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is NodesPage page) page.UpdateDevicePairingRequests(data);
            });
        }
        catch { }
    }

    public void UpdateModelsList(ModelsListInfo data)
    {
        LastModelsList = data;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is SessionsPage page) page.UpdateModelsList(data);
            });
        }
        catch { }
    }

    public PresenceEntry[]? LastPresence { get; private set; }

    public void UpdatePresence(PresenceEntry[] data)
    {
        LastPresence = data;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is InstancesPage page) page.UpdatePresenceData(data);
            });
        }
        catch { }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag as string;
            var pageType = TagToPageType(tag);
            if (pageType != null)
            {
                ContentFrame.Navigate(pageType);
                InitializeCurrentPage();
            }
        }
    }

    private void InitializeCurrentPage()
    {
        switch (ContentFrame.Content)
        {
            case HomePage home: home.Initialize(this); break;
            case ChatPage chat: chat.Initialize(this); break;
            case SessionsPage sessions:
                sessions.Initialize(this);
                if (LastModelsList != null) sessions.UpdateModelsList(LastModelsList);
                break;
            case ChannelsPage channels: channels.Initialize(this); break;
            case UsagePage usage: usage.Initialize(this); break;
            case NodesPage nodes:
                nodes.Initialize(this);
                if (LastNodePairList != null) nodes.UpdatePairingRequests(LastNodePairList);
                if (LastDevicePairList != null) nodes.UpdateDevicePairingRequests(LastDevicePairList);
                break;
            case CronPage cron: cron.Initialize(this); break;
            case SkillsPage skills: skills.Initialize(this); break;
            case ConfigPage config:
                config.Initialize(this);
                if (LastConfig.HasValue) config.UpdateConfig(LastConfig.Value);
                break;
            case InstancesPage instances:
                instances.Initialize(this);
                if (LastPresence != null) instances.UpdatePresenceData(LastPresence);
                break;
            case PermissionsPage permissions: permissions.Initialize(this); break;
            case ActivityPage activity: activity.Initialize(this); break;
            case AgentEventsPage agentEvents:
                agentEvents.ClearCentralCache = ClearAgentEvents;
                // Hydrate from cached events (list is newest-first, add in reverse so oldest are added first)
                for (int i = LastAgentEvents.Count - 1; i >= 0; i--)
                    agentEvents.AddEvent(LastAgentEvents[i]);
                break;
            case SettingsPage settings: settings.Initialize(this); break;
            case DebugPage debug: debug.Initialize(this); break;
            case AboutPage about: about.Initialize(this); break;
        }
    }

    private static Type? TagToPageType(string? tag) => tag switch
    {
        "general" => typeof(HomePage),
        "chat" => typeof(ChatPage),
        "channels" => typeof(ChannelsPage),
        "sessions" => typeof(SessionsPage),
        "instances" => typeof(InstancesPage),
        "cron" => typeof(CronPage),
        "skills" => typeof(SkillsPage),
        "config" => typeof(ConfigPage),
        "permissions" => typeof(PermissionsPage),
        "usage" => typeof(UsagePage),
        "nodes" => typeof(NodesPage),
        "activity" => typeof(ActivityPage),
        "agentevents" => typeof(AgentEventsPage),
        "settings" => typeof(SettingsPage),
        "debug" => typeof(DebugPage),
        "info" => typeof(AboutPage),
        // Legacy tags for deep link compatibility
        "home" => typeof(HomePage),
        "about" => typeof(AboutPage),
        _ => null
    };
}
