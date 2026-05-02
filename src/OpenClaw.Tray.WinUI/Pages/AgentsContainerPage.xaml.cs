using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class AgentsContainerPage : Page
{
    private HubWindow? _hub;

    public AgentsContainerPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        // Navigate the currently selected tab's frame
        EnsureCurrentTabLoaded();
    }

    /// <summary>Select a child tab by tag name (e.g. "sessions", "skills").</summary>
    public void SelectTab(string tag)
    {
        foreach (var item in AgentTabs.TabItems)
        {
            if (item is TabViewItem tab && tab.Tag as string == tag)
            {
                AgentTabs.SelectedItem = tab;
                return;
            }
        }
    }

    private void AgentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EnsureCurrentTabLoaded();
    }

    private void EnsureCurrentTabLoaded()
    {
        if (_hub == null) return;
        if (AgentTabs.SelectedItem is not TabViewItem tab) return;

        var tag = tab.Tag as string;
        switch (tag)
        {
            case "sessions":
                NavigateAndInit<SessionsPage>(SessionsFrame, p =>
                {
                    p.Initialize(_hub);
                    if (_hub.LastModelsList != null) p.UpdateModelsList(_hub.LastModelsList);
                });
                break;
            case "agentevents":
                if (AgentEventsFrame.Content is AgentEventsPage)
                {
                    // Already initialized — don't re-hydrate
                    break;
                }
                NavigateAndInit<AgentEventsPage>(AgentEventsFrame, p =>
                {
                    p.ClearCentralCache = _hub.ClearAgentEvents;
                    for (int i = _hub.LastAgentEvents.Count - 1; i >= 0; i--)
                        p.AddEvent(_hub.LastAgentEvents[i]);
                });
                break;
            case "skills":
                NavigateAndInit<SkillsPage>(SkillsFrame, p => p.Initialize(_hub));
                break;
            case "cron":
                NavigateAndInit<CronPage>(CronFrame, p => p.Initialize(_hub));
                break;
            case "workspace":
                NavigateAndInit<WorkspacePage>(WorkspaceFrame, p => p.Initialize(_hub));
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

    public void ForwardUpdateSessions(SessionInfo[] sessions)
    {
        if (SessionsFrame.Content is SessionsPage p) p.UpdateSessions(sessions);
    }

    public void ForwardUpdateModelsList(ModelsListInfo data)
    {
        if (SessionsFrame.Content is SessionsPage p) p.UpdateModelsList(data);
    }

    public void ForwardAddAgentEvent(AgentEventInfo evt)
    {
        if (AgentEventsFrame.Content is AgentEventsPage p) p.AddEvent(evt);
    }

    public void ForwardUpdateSkillsStatus(JsonElement data)
    {
        if (SkillsFrame.Content is SkillsPage p) p.UpdateFromGateway(data);
    }

    public void ForwardUpdateCronList(JsonElement data)
    {
        if (CronFrame.Content is CronPage p) p.UpdateFromGateway(data);
    }

    public void ForwardUpdateAgentFilesList(JsonElement data)
    {
        if (WorkspaceFrame.Content is WorkspacePage p) p.UpdateAgentFilesList(data);
    }

    public void ForwardUpdateAgentFileContent(JsonElement data)
    {
        if (WorkspaceFrame.Content is WorkspacePage p) p.UpdateAgentFileContent(data);
    }
}
