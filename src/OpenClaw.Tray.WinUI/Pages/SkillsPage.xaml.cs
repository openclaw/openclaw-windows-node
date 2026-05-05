using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Windows;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class SkillsPage : Page
{
    private HubWindow? _hub;
    private bool _hasLiveData;

    public SkillsPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        PopulateAgentFilter(hub);
        if (hub.GatewayClient != null)
        {
            NotWiredInfoBar.IsOpen = false;
            _ = hub.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
        }
        LoadSampleSkills();
    }

    private void PopulateAgentFilter(HubWindow hub)
    {
        AgentFilterCombo.SelectionChanged -= OnAgentFilterChanged;
        AgentFilterCombo.Items.Clear();
        AgentFilterCombo.Items.Add(new ComboBoxItem { Content = "All Agents", Tag = "" });
        foreach (var id in hub.GetAgentIds())
            AgentFilterCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
        AgentFilterCombo.SelectedIndex = 0;
        AgentFilterCombo.SelectionChanged += OnAgentFilterChanged;
    }

    private string? GetSelectedAgentId()
    {
        if (AgentFilterCombo.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag as string;
            return string.IsNullOrEmpty(tag) ? null : tag;
        }
        return null;
    }

    private void OnAgentFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        var client = _hub?.GatewayClient;
        if (client != null)
            _ = client.RequestSkillsStatusAsync(GetSelectedAgentId());
    }

    private void LoadSampleSkills()
    {
        if (_hasLiveData) return;

        var skills = new List<SkillViewModel>
        {
            new()
            {
                Id = "github",
                Name = "GitHub Integration",
                Version = "v2.1",
                Description = "Connect OpenClaw to GitHub for issue tracking, PR reviews, and repository management.",
                StatusText = "Active",
                StatusBackground = new SolidColorBrush(Colors.Green),
                ActionLabel = "Update",
            },
            new()
            {
                Id = "email",
                Name = "Email Digest",
                Version = "v1.3",
                Description = "Automatically summarize and send email digests of daily activity and session outcomes.",
                StatusText = "Active",
                StatusBackground = new SolidColorBrush(Colors.Green),
                ActionLabel = "Update",
            },
            new()
            {
                Id = "calendar",
                Name = "Calendar Sync",
                Version = "v0.9",
                Description = "Sync scheduled tasks and cron jobs with your calendar provider for visibility.",
                StatusText = "Inactive",
                StatusBackground = new SolidColorBrush(Colors.Gray),
                ActionLabel = "Enable",
            },
        };

        SkillsList.ItemsSource = skills;
        SkillsList.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
    }

    private void OnSkillActionClick(object sender, RoutedEventArgs e)
    {
        var skillId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(skillId) || _hub?.GatewayClient == null) return;

        // Determine action based on button content
        var label = (sender as Button)?.Content as string;
        if (label == "Update")
        {
            _ = _hub.GatewayClient.UpdateSkillAsync(skillId);
        }
        else
        {
            _ = _hub.GatewayClient.InstallSkillAsync(skillId);
        }
    }

    public void UpdateFromGateway(JsonElement data)
    {
        OpenClawTray.Services.Logger.Info("[SkillsPage] Received gateway skills data");

        if (!data.TryGetProperty("payload", out var payload))
            return;

        // payload may be { "skills": [...] } or directly an array
        JsonElement skillsArray;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("skills", out var inner))
            skillsArray = inner;
        else if (payload.ValueKind == JsonValueKind.Array)
            skillsArray = payload;
        else
            return;

        var skills = new List<SkillViewModel>();

        foreach (var item in skillsArray.EnumerateArray())
        {
            var vm = new SkillViewModel();

            if (item.TryGetProperty("id", out var idEl))
                vm.Id = idEl.GetString() ?? "";

            if (item.TryGetProperty("name", out var nameEl))
                vm.Name = nameEl.GetString() ?? "";

            if (item.TryGetProperty("version", out var verEl))
                vm.Version = verEl.GetString() ?? "";

            if (item.TryGetProperty("description", out var descEl))
                vm.Description = descEl.GetString() ?? "";

            if (item.TryGetProperty("enabled", out var enabledEl))
            {
                bool enabled = enabledEl.ValueKind == JsonValueKind.True;
                vm.StatusText = enabled ? "Active" : "Inactive";
                vm.StatusBackground = new SolidColorBrush(enabled ? Colors.Green : Colors.Gray);
                vm.ActionLabel = enabled ? "Update" : "Install";
            }

            skills.Add(vm);
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            _hasLiveData = true;
            NotWiredInfoBar.IsOpen = false;

            if (skills.Count > 0)
            {
                SkillsList.ItemsSource = skills;
                SkillsList.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                SkillsList.ItemsSource = null;
                SkillsList.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
            }
        });
    }

    private class SkillViewModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string StatusText { get; set; } = "";
        public SolidColorBrush StatusBackground { get; set; } = new(Colors.Gray);
        public string ActionLabel { get; set; } = "Install";
    }
}
