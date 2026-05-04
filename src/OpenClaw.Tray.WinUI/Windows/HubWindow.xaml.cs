using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class HubWindow : WindowEx
{
    public bool IsClosed { get; private set; }

    // Shared state accessible by pages
    public SettingsManager? Settings { get; set; }
    public OpenClawGatewayClient? GatewayClient { get; set; }
    public ConnectionStatus CurrentStatus { get; set; }
    private string _currentAgentId = "main";
    public string CurrentAgentId => _currentAgentId;

    // Legacy compatibility alias
    public string SelectedAgentId => _currentAgentId;
    public Action<string?>? OpenDashboardAction { get; set; }
    public Action? ConnectAction { get; set; }
    public Action? DisconnectAction { get; set; }
    public Action? ReconnectAction { get; set; }

    // Node service state (set by App.xaml.cs in ShowHub)
    public bool NodeIsConnected { get; set; }
    public bool NodeIsPaired { get; set; }
    public bool NodeIsPendingApproval { get; set; }
    public string? NodeShortDeviceId { get; set; }
    public string? NodeFullDeviceId { get; set; }

    // Cached gateway data — pages read these on navigation
    public SessionInfo[]? LastSessions { get; private set; }
    public ChannelHealth[]? LastChannels { get; private set; }
    public GatewayUsageInfo? LastUsage { get; private set; }
    public GatewayCostUsageInfo? LastUsageCost { get; private set; }
    public GatewayUsageStatusInfo? LastUsageStatus { get; private set; }
    public GatewayNodeInfo[]? LastNodes { get; private set; }

    public System.Text.Json.JsonElement? LastConfig { get; private set; }
    public System.Text.Json.JsonElement? LastConfigSchema { get; private set; }

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
    /// Navigate to the default page (Conversations). Call after setting Settings/GatewayClient.
    /// </summary>
    public void NavigateToDefault()
    {
        if (ContentFrame.Content == null)
        {
            // Navigate to Home (first item)
            NavView.SelectedItem = NavView.MenuItems[0];
        }
    }

    /// <summary>
    /// Navigate to a specific page by tag name (e.g. "home", "sessions", "channels").
    /// </summary>
    public void NavigateTo(string tag)
    {
        // Map legacy tags
        if (tag == "general") tag = "home";
        // "chat" tag opens the ChatPage (WebView2) directly
        if (tag == "about") tag = "info";
        // Map legacy flat agent tags to current agent scope
        if (tag == "sessions") tag = $"agent:{_currentAgentId}:sessions";
        if (tag == "agentevents") tag = $"agent:{_currentAgentId}:agentevents";
        if (tag == "skills") tag = $"agent:{_currentAgentId}:skills";
        if (tag == "cron") tag = $"agent:{_currentAgentId}:cron";
        if (tag == "workspace") tag = $"agent:{_currentAgentId}:workspace";

        // Search all nav items including nested
        if (FindAndSelectNavItem(NavView.MenuItems, tag)) return;
        if (FindAndSelectNavItem(NavView.FooterMenuItems, tag)) return;

        // Fallback: navigate directly
        if (tag.StartsWith("agent:")) _currentAgentId = ParseAgentIdFromTag(tag);
        var pageType = TagToPageType(tag);
        if (pageType != null)
        {
            ContentFrame.Navigate(pageType);
            InitializeCurrentPage();
        }
    }

    private bool FindAndSelectNavItem(IList<object> items, string tag)
    {
        foreach (var item in items)
        {
            if (item is NavigationViewItem navItem)
            {
                if (navItem.Tag as string == tag) { NavView.SelectedItem = navItem; return true; }
                if (navItem.MenuItems.Count > 0 && FindAndSelectNavItem(navItem.MenuItems, tag)) return true;
            }
        }
        return false;
    }

    public void UpdateStatus(ConnectionStatus status)
    {
        CurrentStatus = status;
        if (status == ConnectionStatus.Disconnected)
            _lastGatewaySelf = null;
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
                if (ContentFrame?.Content is ConnectionPage connectionPage)
                {
                    connectionPage.UpdateStatus(status);
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
    public GatewaySelfInfo? LastGatewaySelf => _lastGatewaySelf;

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
            if (ContentFrame?.Content is SessionsPage sp) sp.UpdateSessions(sessions);
            else if (ContentFrame?.Content is ConversationsPage convos) convos.UpdateSessions(sessions);
            else if (ContentFrame?.Content is HomePage home) home.UpdateSessions(sessions);
        });
    }

    public void UpdateChannelHealth(ChannelHealth[] channels)
    {
        LastChannels = channels;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is ChannelsPage cp) cp.UpdateChannels(channels);
        });
    }

    public void UpdateUsage(GatewayUsageInfo usage)
    {
        LastUsage = usage;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage up) up.UpdateUsage(usage);
        });
    }

    public void UpdateUsageCost(GatewayCostUsageInfo cost)
    {
        LastUsageCost = cost;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage up) up.UpdateUsageCost(cost);
        });
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        LastUsageStatus = status;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage up) up.UpdateUsageStatus(status);
        });
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        LastNodes = nodes;
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is NodesPage np) np.UpdateNodes(nodes);
            else if (ContentFrame?.Content is HomePage home) home.UpdateNodes(nodes);
        });
    }

    public void UpdateCronList(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is CronPage cp) cp.UpdateFromGateway(data);
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
            if (ContentFrame?.Content is ConfigPage cp) cp.UpdateConfig(config);
            else if (ContentFrame?.Content is BindingsPage bp) bp.UpdateConfig(config);
        });
    }

    public void UpdateConfigSchema(System.Text.Json.JsonElement schema)
    {
        LastConfigSchema = schema;
        if (IsClosed) return;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is ConfigPage cp) cp.UpdateConfigSchema(schema);
            });
        }
        catch { }
    }

    public void UpdateSkillsStatus(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is SkillsPage sp) sp.UpdateFromGateway(data);
            });
        }
        catch { }
    }

    public void UpdateAgentsList(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                // Rebuild nav sidebar agent items
                RebuildAgentNavItems(data);
                if (ContentFrame?.Content is HomePage home) home.UpdateAgentsList(data);
            });
        }
        catch { }
    }

    private void RebuildAgentNavItems(System.Text.Json.JsonElement data)
    {
        if (!data.TryGetProperty("agents", out var agentsEl) ||
            agentsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        AgentsNavItem.MenuItems.Clear();

        foreach (var agent in agentsEl.EnumerateArray())
        {
            var id = agent.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var name = agent.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

            var agentItem = new NavigationViewItem
            {
                Content = name ?? id,
                Tag = $"agent:{id}",
                IsExpanded = true,
                Icon = new FontIcon { Glyph = "\uE99A" }
            };

            agentItem.MenuItems.Add(CreateAgentSubItem(id, "sessions", "Sessions", "\uE8F2"));
            agentItem.MenuItems.Add(CreateAgentSubItem(id, "agentevents", "Agent Events", "\uE943"));
            agentItem.MenuItems.Add(CreateAgentSubItem(id, "skills", "Skills", "\uE945"));
            agentItem.MenuItems.Add(CreateAgentSubItem(id, "workspace", "Workspace", "\uE8B7"));

            AgentsNavItem.MenuItems.Add(agentItem);
        }
    }

    private static NavigationViewItem CreateAgentSubItem(string agentId, string page, string label, string glyph)
    {
        return new NavigationViewItem
        {
            Content = label,
            Tag = $"agent:{agentId}:{page}",
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    public void UpdateAgentFilesList(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is WorkspacePage wp) wp.UpdateAgentFilesList(data);
                if (ContentFrame?.Content is AgentsContainerPage agents) agents.ForwardUpdateAgentFilesList(data);
            });
        }
        catch { }
    }

    public void UpdateAgentFileContent(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is WorkspacePage wp) wp.UpdateAgentFileContent(data);
                if (ContentFrame?.Content is AgentsContainerPage agents) agents.ForwardUpdateAgentFileContent(data);
            });
        }
        catch { }
    }

    // Agent events ring buffer (max 400, cached centrally)
    // All mutations happen on the UI thread via DispatcherQueue
    private const int MaxAgentEvents = 400;
    private readonly System.Collections.Generic.List<AgentEventInfo> _agentEvents = new();
    public System.Collections.Generic.IReadOnlyList<AgentEventInfo> LastAgentEvents => _agentEvents;

    public void ClearAgentEvents()
    {
        DispatcherQueue?.TryEnqueue(() => _agentEvents.Clear());
    }

    public void UpdateAgentEvent(AgentEventInfo evt)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                _agentEvents.Insert(0, evt);
                if (_agentEvents.Count > MaxAgentEvents)
                    _agentEvents.RemoveRange(MaxAgentEvents, _agentEvents.Count - MaxAgentEvents);
                if (ContentFrame?.Content is AgentsContainerPage agents) agents.ForwardAddAgentEvent(evt);
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
                if (ContentFrame?.Content is NodesPage np) np.UpdatePairingRequests(data);
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
                if (ContentFrame?.Content is NodesPage np) np.UpdateDevicePairingRequests(data);
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
                if (ContentFrame?.Content is SessionsPage sp) sp.UpdateModelsList(data);
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
                if (ContentFrame?.Content is InstancesPage ip) ip.UpdatePresenceData(data);
            });
        }
        catch { }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag as string;
            if (tag?.StartsWith("agent:") == true)
                _currentAgentId = ParseAgentIdFromTag(tag);
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
            case ConnectionPage connection: connection.Initialize(this); break;
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
                if (LastConfigSchema.HasValue) config.UpdateConfigSchema(LastConfigSchema.Value);
                if (LastConfig.HasValue) config.UpdateConfig(LastConfig.Value);
                break;
            case InstancesPage instances:
                instances.Initialize(this);
                if (LastPresence != null) instances.UpdatePresenceData(LastPresence);
                break;
            case PermissionsPage permissions: permissions.Initialize(this); break;
            case CapabilitiesPage capabilities: capabilities.Initialize(this); break;
            case ConversationsPage convos: convos.Initialize(this); break;
            case ActivityPage activity: activity.Initialize(this); break;
            case AgentEventsPage agentEvents:
                agentEvents.ClearCentralCache = ClearAgentEvents;
                agentEvents.SetAgentFilter(_currentAgentId);
                if (agentEvents.EventCount == 0)
                {
                    for (int i = LastAgentEvents.Count - 1; i >= 0; i--)
                        agentEvents.AddEvent(LastAgentEvents[i]);
                }
                break;
            case WorkspacePage workspace: workspace.Initialize(this); break;
            case BindingsPage bindings:
                bindings.Initialize(this);
                if (LastConfig.HasValue) bindings.UpdateConfig(LastConfig.Value);
                break;
            case SettingsPage settings: settings.Initialize(this); break;
            case DebugPage debug: debug.Initialize(this); break;
            case AboutPage about: about.Initialize(this); break;
        }
    }

    private static Type? TagToPageType(string? tag) => tag switch
    {
        "home" => typeof(HomePage),
        "chat" => typeof(ChatPage),
        "connection" => typeof(ConnectionPage),
        "channels" => typeof(ChannelsPage),
        "nodes" => typeof(NodesPage),
        "instances" => typeof(InstancesPage),
        "config" => typeof(ConfigPage),
        "usage" => typeof(UsagePage),
        "bindings" => typeof(BindingsPage),
        "capabilities" => typeof(CapabilitiesPage),
        "permissions" => typeof(PermissionsPage),
        "activity" => typeof(ActivityPage),
        "settings" => typeof(SettingsPage),
        "debug" => typeof(DebugPage),
        "info" => typeof(AboutPage),
        // Legacy tags
        "general" => typeof(HomePage),
        "sessions" => typeof(SessionsPage),
        "agentevents" => typeof(AgentEventsPage),
        "skills" => typeof(SkillsPage),
        "cron" => typeof(CronPage),
        "workspace" => typeof(WorkspacePage),
        "about" => typeof(AboutPage),
        // Agent-scoped pages
        _ when tag?.StartsWith("agent:") == true => ResolveAgentPageType(tag),
        _ => null
    };

    private static Type? ResolveAgentPageType(string tag)
    {
        var parts = tag.Split(':');
        if (parts.Length < 3) return null;
        return parts[2] switch
        {
            "sessions" => typeof(SessionsPage),
            "agentevents" => typeof(AgentEventsPage),
            "skills" => typeof(SkillsPage),
            "cron" => typeof(CronPage),
            "workspace" => typeof(WorkspacePage),
            _ => null
        };
    }

    private static string ParseAgentIdFromTag(string? tag)
    {
        if (tag == null || !tag.StartsWith("agent:")) return "main";
        var parts = tag.Split(':');
        return parts.Length >= 2 ? parts[1] : "main";
    }

    // ── Command Search (Ctrl+K / Ctrl+F) — title bar AutoSuggestBox ──

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            global::Windows.System.VirtualKey.Control).HasFlag(
            global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && (e.Key == global::Windows.System.VirtualKey.K || e.Key == global::Windows.System.VirtualKey.F))
        {
            e.Handled = true;
            TitleSearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            TitleSearchBox.Text = "";
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var commands = BuildCommandList();
        var query = sender.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? commands.Take(8).ToList()
            : commands.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(10).ToList();
        sender.ItemsSource = filtered;
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is CommandItem cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            ExecuteCommand(cmd);
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is CommandItem cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            ExecuteCommand(cmd);
        }
    }

    internal List<CommandItem> BuildCommandList()
    {
        var agentId = _currentAgentId;
        var commands = new List<CommandItem>
        {
            // Navigation
            new() { Icon = "🏠", Title = "Go to Home", Subtitle = "Home page", Tag = "home" },
            new() { Icon = "💬", Title = "Go to Chat", Subtitle = "Open chat", Tag = "chat" },
            new() { Icon = "🧠", Title = $"Go to Sessions ({agentId})", Subtitle = "Agent sessions", Tag = $"agent:{agentId}:sessions" },
            new() { Icon = "🧠", Title = $"Go to Agent Events ({agentId})", Subtitle = "Agent event log", Tag = $"agent:{agentId}:agentevents" },
            new() { Icon = "🧠", Title = $"Go to Skills ({agentId})", Subtitle = "Registered skills", Tag = $"agent:{agentId}:skills" },
            new() { Icon = "🧠", Title = $"Go to Cron ({agentId})", Subtitle = "Scheduled tasks", Tag = $"agent:{agentId}:cron" },
            new() { Icon = "🧠", Title = $"Go to Workspace ({agentId})", Subtitle = "Workspace files", Tag = $"agent:{agentId}:workspace" },
            new() { Icon = "📡", Title = "Go to Channels", Subtitle = "Gateway channels", Tag = "channels" },
            new() { Icon = "📡", Title = "Go to Nodes", Subtitle = "Connected nodes", Tag = "nodes" },
            new() { Icon = "📡", Title = "Go to Instances", Subtitle = "Gateway instances", Tag = "instances" },
            new() { Icon = "📡", Title = "Go to Config", Subtitle = "Gateway configuration", Tag = "config" },
            new() { Icon = "📡", Title = "Go to Usage", Subtitle = "Usage statistics", Tag = "usage" },
            new() { Icon = "📡", Title = "Go to Bindings", Subtitle = "Gateway bindings", Tag = "bindings" },
            new() { Icon = "🖥️", Title = "Go to Capabilities", Subtitle = "Device capabilities", Tag = "capabilities" },
            new() { Icon = "🛡️", Title = "Go to Permissions", Subtitle = "Exec policy & allowlists", Tag = "permissions" },
            new() { Icon = "🕐", Title = "Go to Activity", Subtitle = "Activity stream", Tag = "activity" },
            new() { Icon = "⚙️", Title = "Go to Settings", Subtitle = "Application settings", Tag = "settings" },
            new() { Icon = "🐛", Title = "Go to Debug", Subtitle = "Debug information", Tag = "debug" },
            new() { Icon = "ℹ️", Title = "Go to Info", Subtitle = "About this app", Tag = "info" },

            // Actions
            new() { Icon = "💬", Title = "Open Chat Window", Subtitle = "Open standalone chat", Tag = "chat" },
            new() { Icon = "🌐", Title = "Open Dashboard", Subtitle = "Open web dashboard", Execute = () => OpenDashboardAction?.Invoke(null) },
            new() { Icon = "📤", Title = "Quick Send", Subtitle = "Send a quick message", Execute = () => QuickSendAction?.Invoke() },
        };

        // Toggle commands
        if (Settings != null)
        {
            commands.Add(new CommandItem
            {
                Icon = "🔌", Title = "Toggle Node Mode",
                Subtitle = Settings.EnableNodeMode ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.EnableNodeMode = !Settings.EnableNodeMode; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "📷", Title = "Toggle Camera",
                Subtitle = Settings.NodeCameraEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeCameraEnabled = !Settings.NodeCameraEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🎨", Title = "Toggle Canvas",
                Subtitle = Settings.NodeCanvasEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeCanvasEnabled = !Settings.NodeCanvasEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🖥️", Title = "Toggle Screen Capture",
                Subtitle = Settings.NodeScreenEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeScreenEnabled = !Settings.NodeScreenEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🌐", Title = "Toggle Browser Control",
                Subtitle = Settings.NodeBrowserProxyEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeBrowserProxyEnabled = !Settings.NodeBrowserProxyEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
        }

        // Dynamic session commands
        if (LastSessions != null)
        {
            foreach (var session in LastSessions)
            {
                var key = session.Key;
                commands.Add(new CommandItem
                {
                    Icon = "🧠", Title = $"Go to session: {key}",
                    Subtitle = "Open in dashboard",
                    Execute = () => OpenDashboardAction?.Invoke($"sessions/{key}")
                });
            }
        }

        return commands;
    }

    private void ExecuteCommand(CommandItem cmd)
    {
        if (cmd.Execute != null)
        {
            cmd.Execute();
            return;
        }

        if (!string.IsNullOrEmpty(cmd.Tag))
        {
            NavigateTo(cmd.Tag);
        }
    }

    /// <summary>Action to open the QuickSend dialog, set by App.xaml.cs.</summary>
    public Action? QuickSendAction { get; set; }
}
