using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;
using OpenClawTray.Controls;
using static OpenClawTray.Controls.WorkspacePageRenderer;

namespace OpenClawTray.Pages;

/// <summary>Native projections for Workspace pages without a reusable workflow page.</summary>
public sealed partial class WorkspaceContentPage : Page
{
    private readonly WorkspaceWindow _owner;
    private readonly WorkspaceDestination _destination;
    private bool _detached;
    private readonly WorkspacePageRenderer _renderer;
    private static string Text(string key) => WorkspaceWindow.Text(key);

    internal WorkspaceContentPage(WorkspaceWindow owner, WorkspaceDestination destination)
    {
        InitializeComponent();
        _owner = owner;
        _destination = destination;
        _renderer = new WorkspacePageRenderer((Style)Resources["WorkspaceCard"], Text, owner.ShowError);
        _owner.State.PropertyChanged += OnStateChanged;
        _owner.State.AgentEventAdded += OnAgentEvent;
        _owner.RefreshStateChanged += OnRefreshStateChanged;
        Unloaded += (_, _) => Detach();
        Render();
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        _owner.State.PropertyChanged -= OnStateChanged;
        _owner.State.AgentEventAdded -= OnAgentEvent;
        _owner.RefreshStateChanged -= OnRefreshStateChanged;
    }

    private void OnRefreshStateChanged()
    {
        LoadingRing.IsActive = _owner.IsRefreshing;
        LoadingRing.Visibility = _owner.IsRefreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !_owner.IsRefreshing && _owner.State.Status == ConnectionStatus.Connected;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Services.AppState.AgentsList) or nameof(Services.AppState.Sessions)
            or nameof(Services.AppState.Nodes) or nameof(Services.AppState.Status) or nameof(Services.AppState.AgentEvents))
            Render();
    }
    private void OnAgentEvent(AgentEventInfo _) { if (_destination.Page == WorkspacePageId.Activity) Render(); }
    private void OnRefresh(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(_owner.RefreshAsync, new AppLogger(), nameof(OnRefresh));
    private void OnOpenConnection(object sender, RoutedEventArgs e) => _owner.OpenCompanion(CompanionPageId.Connection);

    private void Render()
    {
        if (_detached) return;
        Sections.Children.Clear();
        Heading.Text = Text(_destination.Page.ToString());
        Description.Text = Text(_destination.Page + "Description");
        var hasLiveData = _destination.Page is WorkspacePageId.Agents or WorkspacePageId.AgentDetail
            or WorkspacePageId.WriterDetail or WorkspacePageId.Systems or WorkspacePageId.SystemDetail
            or WorkspacePageId.Activity;
        ConnectionInfo.IsOpen = hasLiveData && _owner.State.Status != ConnectionStatus.Connected;
        RefreshButton.Visibility = hasLiveData ? Visibility.Visible : Visibility.Collapsed;
        switch (_destination.Page)
        {
            case WorkspacePageId.Agents: RenderAgents(); break;
            case WorkspacePageId.AgentDetail:
            case WorkspacePageId.WriterDetail: RenderAgentDetail(); break;
            case WorkspacePageId.Systems: RenderSystems(); break;
            case WorkspacePageId.SystemDetail: RenderSystemDetail(); break;
            case WorkspacePageId.Dashboards:
            case WorkspacePageId.DashboardDetail:
            case WorkspacePageId.Canvas: RenderDashboards(); break;
            case WorkspacePageId.Plugins: RenderPlugins(); break;
            case WorkspacePageId.Activity: RenderActivity(); break;
            case WorkspacePageId.More: RenderMore(); break;
            case WorkspacePageId.Apps: RenderApps(); break;
            case WorkspacePageId.Portals: RenderPortals(); break;
            case WorkspacePageId.Tasks:
            case WorkspacePageId.Meetings:
                Sections.Children.Add(Card(Text("NotAvailable"), Text("NoNativeWorkflow"),
                    Button("OpenDashboard", () => ((Services.IAppCommands)Application.Current).OpenDashboard())));
                break;
        }
        OnRefreshStateChanged();
    }

    private void RenderAgents()
    {
        var agents = WorkspaceProjection.Agents(_owner.State.AgentsList, _owner.State.Sessions);
        if (agents.Count == 0)
        {
            Sections.Children.Add(Card(Text("NoAgents"), Text("AgentsEmpty"),
                Button("AgentSettings", () => _owner.OpenCompanion(CompanionPageId.Agents))));
            return;
        }

        AddCards(agents.Select(agent =>
        {
            var latest = WorkspaceProjection.Sessions(_owner.State.Sessions, agent.Id).FirstOrDefault();
            return Card(agent.Name,
                string.Format(CultureInfo.CurrentCulture, Text("AgentSummary"), agent.Id, agent.SessionCount),
                Label(_owner.State.Status == ConnectionStatus.Connected ? Text("Connected") : Text("ConnectionRequired")),
                Label(string.IsNullOrEmpty(agent.Workspace) ? Text("WorkspaceNotReported") : agent.Workspace),
                Label(latest?.Title ?? Text("NoSessions")),
                AsyncButton("OpenChat", () => _owner.StartAgentChatAsync(agent)),
                Button("Details", () => _owner.Navigate(new(WorkspacePageId.AgentDetail, agent.Id))));
        }));
        Sections.Children.Add(Button("AgentSettings", () => _owner.OpenCompanion(CompanionPageId.Agents)));
        Sections.Children.Add(Label(Text("RecentWork"), "SubtitleTextBlockStyle"));
        AddSessions(null);
    }

    private void RenderAgentDetail()
    {
        var agent = WorkspaceProjection.Agents(_owner.State.AgentsList, _owner.State.Sessions)
            .FirstOrDefault(agent => agent.Id == (_destination.ItemId ?? _owner.SelectedAgentId));
        if (agent is null)
        {
            Sections.Children.Add(Card(Text("NoAgents"), Text("SelectAgentFirst"),
                Button("Agents", () => _owner.Navigate(new(WorkspacePageId.Agents)))));
            return;
        }
        Heading.Text = agent.Name;
        Sections.Children.Add(Card(agent.Id,
            string.IsNullOrEmpty(agent.Workspace) ? Text("WorkspaceNotReported") : agent.Workspace,
            AsyncButton("OpenChat", () => _owner.StartAgentChatAsync(agent)),
            Button("AgentFiles", () => _owner.OpenAgentFiles(agent.Id)),
            Button("AgentSettings", () => _owner.OpenCompanion(CompanionPageId.Agents, agent.Id))));
        Sections.Children.Add(Label(Text("RecentWork"), "SubtitleTextBlockStyle"));
        AddSessions(agent.Id);
    }

    private void AddSessions(string? agentId)
    {
        var sessions = WorkspaceProjection.Sessions(_owner.State.Sessions, agentId);
        if (sessions.Count == 0)
        {
            Sections.Children.Add(Label(Text("NoSessions")));
            return;
        }
        foreach (var session in sessions.Take(10))
        {
            Sections.Children.Add(SessionRow(session.Title, () => _owner.SelectSession(session.Key)));
        }
    }

    private void RenderSystems()
    {
        var computer = new StackPanel { Spacing = 16 };
        computer.Children.Add(FluentIconCatalog.Build(FluentIconCatalog.System, 48));
        computer.Children.Add(Card(Environment.MachineName, Environment.OSVersion.VersionString,
            Button("DeviceAccess", () => _owner.OpenCompanion(CompanionPageId.Permissions)),
            Button("Details", () => _owner.Navigate(new(WorkspacePageId.SystemDetail, "local")))));
        AddCards([
            computer,
            Card(Text("Gateway"), _owner.State.Status.ToString(),
                Button("ManageConnection", () => _owner.OpenCompanion(CompanionPageId.Connection)),
                Button("Details", () => _owner.Navigate(new(WorkspacePageId.SystemDetail, "gateway"))),
                Button("ConnectionTimeline", _owner.OpenTimeline))
        ]);
        Sections.Children.Add(Label(Text("MetricsUnavailable")));
        Sections.Children.Add(Card(Text("DeviceAccess"), Text("DeviceAccessDescription"),
            Button("Permissions", () => _owner.OpenCompanion(CompanionPageId.Permissions)),
            Button("PairedDevices", () => _owner.OpenCompanion(CompanionPageId.Instances)),
            Button("PairDevice", () => _owner.OpenCompanion(CompanionPageId.Channels))));
        foreach (var node in _owner.State.Nodes)
        {
            Sections.Children.Add(Card(
                string.IsNullOrEmpty(node.DisplayName) ? node.NodeId : node.DisplayName,
                $"{node.Platform} · {node.Status}",
                Button("Details", () => _owner.Navigate(new(WorkspacePageId.SystemDetail, node.NodeId)))));
        }
    }

    private void RenderSystemDetail()
    {
        if (_destination.ItemId == "local")
        {
            Heading.Text = Environment.MachineName;
            Sections.Children.Add(Card(Text("ThisComputer"), Environment.OSVersion.VersionString,
                FluentIconCatalog.Build(FluentIconCatalog.System, 48),
                Label(Text("MetricsUnavailable")),
                Button("Permissions", () => _owner.OpenCompanion(CompanionPageId.Permissions))));
        }
        else if (_destination.ItemId == "gateway")
        {
            Sections.Children.Add(Card(Text("Gateway"), _owner.State.Status.ToString(),
                Button("ManageConnection", () => _owner.OpenCompanion(CompanionPageId.Connection)),
                Button("ConnectionTimeline", _owner.OpenTimeline)));
        }
        else
        {
            var node = _owner.State.Nodes.FirstOrDefault(node => node.NodeId == _destination.ItemId);
            Sections.Children.Add(node is null
                ? Card(Text("SystemMissing"), Text("SelectSystemFirst"),
                    Button("Systems", () => _owner.Navigate(new(WorkspacePageId.Systems))))
                : Card(string.IsNullOrWhiteSpace(node.DisplayName) ? node.NodeId : node.DisplayName,
                    $"{node.Platform} · {node.Status}", Label(node.NodeId),
                    Button("PairedDevices", () => _owner.OpenCompanion(CompanionPageId.Instances))));
        }
        Sections.Children.Add(Card(Text("DeviceAccess"), Text("DeviceAccessDescription"),
            Button("Permissions", () => _owner.OpenCompanion(CompanionPageId.Permissions)),
            Button("PairedDevices", () => _owner.OpenCompanion(CompanionPageId.Instances))));
    }

    private void RenderDashboards()
    {
        if (_destination.Page == WorkspacePageId.Dashboards)
        {
            AddCards([
                Card(Text("Gateway"), Text("PortalsDescription"),
                    FluentIconCatalog.Build(FluentIconCatalog.Canvas, 48),
                    Button("Details", () => _owner.Navigate(new(WorkspacePageId.DashboardDetail, "gateway"))),
                    Button("OpenDashboard", () => ((Services.IAppCommands)Application.Current).OpenDashboard())),
                Card(Text("Usage"), Text("GatewayInsights"),
                    Button("Usage", () => _owner.Navigate(new(WorkspacePageId.Usage))),
                    Button("Activity", () => _owner.Navigate(new(WorkspacePageId.Activity))))
            ]);
        }
        Sections.Children.Add(Card(Text("DashboardsUnavailable"), Text("DashboardsUnavailableDescription"),
            Button("OpenDashboard", () => ((Services.IAppCommands)Application.Current).OpenDashboard()),
            Button("Permissions", () => _owner.OpenCompanion(CompanionPageId.Permissions))));
        if (_destination.Page != WorkspacePageId.Dashboards)
        {
            AddCards([
                Card(Text("ProjectMetrics"), Text("NoDashboardSelected")),
                Card(Text("Priorities"), Text("NoNativeWorkflow")),
                Card(Text("Tasks"), Text("NoNativeWorkflow")),
                Card(Text("Activity"), Text("GatewayInsights"),
                    Button("Activity", () => _owner.Navigate(new(WorkspacePageId.Activity))))
            ]);
        }
    }

    private void RenderPlugins()
    {
        var tabs = new Pivot();
        var installed = new PivotItem { Header = Text("Installed") };
        installed.Content = Card(Text("PluginInventoryUnavailable"), Text("PluginsUnavailableDescription"),
            Button("Config", () => _owner.OpenCompanion(CompanionPageId.Config)),
            Button("Skills", () => _owner.Navigate(new(WorkspacePageId.Skills))));
        var discover = new PivotItem { Header = Text("Discover") };
        var install = new Button { Content = Text("Install"), IsEnabled = false };
        ToolTipService.SetToolTip(install, Text("PluginMutationsUnavailable"));
        discover.Content = Card(Text("PluginCatalogUnavailable"), Text("PluginMutationsUnavailable"), install,
            AsyncButton("Documentation", () => _owner.OpenLinkAsync("https://docs.openclaw.ai/tools/plugin")));
        tabs.Items.Add(installed);
        tabs.Items.Add(discover);
        Sections.Children.Add(tabs);
    }

    private void RenderActivity()
    {
        if (_owner.State.AgentEvents.Count == 0)
            Sections.Children.Add(Card(Text("NoActivity"), Text("ActivityEmpty")));
        else
            foreach (var activity in _owner.State.AgentEvents.Take(40))
                Sections.Children.Add(Card(activity.Stream, activity.Summary ?? activity.RunId));
        Sections.Children.Add(Button("AgentEvents", () => _owner.OpenCompanion(CompanionPageId.AgentEvents)));
    }

    private void RenderMore()
    {
        foreach (var page in new[]
        {
            WorkspacePageId.Sessions, WorkspacePageId.Usage, WorkspacePageId.Activity,
            WorkspacePageId.Tasks, WorkspacePageId.Meetings, WorkspacePageId.Apps,
            WorkspacePageId.Portals, WorkspacePageId.Notifications
        })
            Sections.Children.Add(_renderer.CreateActionButton(page.ToString(), () => _owner.Navigate(new(page))));
        Sections.Children.Add(Button("CommandCenter", () => _owner.OpenCommandCenter()));
        Sections.Children.Add(Button("Settings", () => _owner.OpenCompanion(CompanionPageId.Settings)));
        Sections.Children.Add(Button("Connection", () => _owner.OpenCompanion(CompanionPageId.Connection)));
        Sections.Children.Add(Button("Permissions", () => _owner.OpenCompanion(CompanionPageId.Permissions)));
        Sections.Children.Add(Button("Debug", () => _owner.OpenCompanion(CompanionPageId.Debug)));
        Sections.Children.Add(Button("Canvas", () => _owner.Navigate(new(WorkspacePageId.Canvas))));
        Sections.Children.Add(Button("Tray", _owner.OpenTray));
        Sections.Children.Add(AsyncButton("Setup", _owner.OpenSetupAsync));
    }

    private void RenderApps()
    {
        Sections.Children.Add(Card(Text("CompanionApp"), Text("AppsDescription"),
            Button("Settings", () => _owner.OpenCompanion(CompanionPageId.Settings)),
            AsyncButton("GetApps", () => _owner.OpenLinkAsync("https://docs.openclaw.ai/platforms"))));
    }

    private void RenderPortals()
    {
        Sections.Children.Add(Card(Text("Gateway"), Text("PortalsDescription"),
            Button("OpenDashboard", () => ((Services.IAppCommands)Application.Current).OpenDashboard()),
            AsyncButton("Documentation", () => _owner.OpenLinkAsync("https://docs.openclaw.ai"))));
    }

    private void AddCards(IEnumerable<FrameworkElement> cards) =>
        Sections.Children.Add(Cards(ActualWidth, cards));

    private Border Card(string title, string description, params FrameworkElement[] actions) =>
        _renderer.Card(title, description, actions);
    private Button Button(string key, Action action) => _renderer.CreateActionButton(key, action);
    private Button AsyncButton(string key, Func<Task> action) => _renderer.AsyncButton(key, action);
}
