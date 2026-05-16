using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Windows.UI;

namespace OpenClawTray.Pages;

public sealed partial class SkillsPage : Page
{
    private HubWindow? _hub;
    private List<SkillData> _allSkills = new();

    public string? CurrentAgentId => GetSelectedAgentId();

    public SkillsPage()
    {
        InitializeComponent();
        EnabledHeaderBtn.Click += (s, e) =>
        {
            _enabledExpanded = !_enabledExpanded;
            EnabledChevron.Glyph = _enabledExpanded ? "\uE70E" : "\uE70D";
            EnabledPanel.Visibility = _enabledExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (!_enabledExpanded) SkillsScroller.ChangeView(null, 0, null, disableAnimation: false);
        };
        DisabledHeaderBtn.Click += (s, e) =>
        {
            _disabledExpanded = !_disabledExpanded;
            DisabledChevron.Glyph = _disabledExpanded ? "\uE70E" : "\uE70D";
            DisabledPanel.Visibility = _disabledExpanded ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        PopulateAgentFilter(hub);
        if (hub.GatewayClient != null)
        {
            _ = hub.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
        }
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

    private async void OnToggleSkillClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string skillKey) return;
        if (_hub?.GatewayClient == null) return;

        var skill = _allSkills.FirstOrDefault(s => s.SkillKey == skillKey);
        if (skill == null) return;

        bool newState = !skill.IsEnabled;
        btn.IsEnabled = false;
        var success = await _hub.GatewayClient.SetSkillEnabledAsync(skillKey, newState);
        btn.IsEnabled = true;

        if (success)
        {
            // Re-lookup after await — _allSkills may have been replaced by UpdateFromGateway
            var current = _allSkills.FirstOrDefault(s => s.SkillKey == skillKey);
            if (current != null)
            {
                current.IsEnabled = newState;
                RebuildCards();
            }
        }
    }

    public void UpdateFromGateway(JsonElement data)
    {
        OpenClawTray.Services.Logger.Info("[SkillsPage] Received gateway skills data");

        JsonElement skillsArray;
        if (data.TryGetProperty("skills", out var inner))
            skillsArray = inner;
        else if (data.TryGetProperty("payload", out var payload))
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("skills", out var nested))
                skillsArray = nested;
            else if (payload.ValueKind == JsonValueKind.Array)
                skillsArray = payload;
            else
                return;
        }
        else if (data.ValueKind == JsonValueKind.Array)
            skillsArray = data;
        else
            return;

        var skills = new List<SkillData>();

        foreach (var item in skillsArray.EnumerateArray())
        {
            var s = new SkillData();

            if (item.TryGetProperty("name", out var nameEl))
            {
                s.Name = nameEl.GetString() ?? "";
                s.Id = s.Name;
            }
            if (item.TryGetProperty("id", out var idEl))
                s.Id = idEl.GetString() ?? s.Id;
            if (item.TryGetProperty("emoji", out var emojiEl))
            {
                var emoji = emojiEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(emoji))
                    s.Name = $"{emoji} {s.Name}";
            }
            if (item.TryGetProperty("description", out var descEl))
                s.Description = descEl.GetString() ?? "";
            if (item.TryGetProperty("source", out var srcEl))
                s.Source = srcEl.GetString() ?? "";
            if (item.TryGetProperty("skillKey", out var keyEl))
                s.SkillKey = keyEl.GetString() ?? s.Id;
            else
                s.SkillKey = s.Id;

            if (item.TryGetProperty("disabled", out var disabledEl))
                s.IsEnabled = disabledEl.ValueKind != JsonValueKind.True;
            else if (item.TryGetProperty("enabled", out var enabledEl))
                s.IsEnabled = enabledEl.ValueKind == JsonValueKind.True;
            else
                s.IsEnabled = item.TryGetProperty("eligible", out var eligibleEl) && eligibleEl.ValueKind == JsonValueKind.True;

            skills.Add(s);
        }

        // Sort alphabetically
        skills.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));

        DispatcherQueue?.TryEnqueue(() =>
        {
            _allSkills = skills;
            RebuildCards();
        });
    }

    private bool _enabledExpanded = true;
    private bool _disabledExpanded = true;

    private void RebuildCards()
    {
        var enabled = _allSkills.Where(s => s.IsEnabled).ToList();
        var disabled = _allSkills.Where(s => !s.IsEnabled).ToList();

        EnabledPanel.Children.Clear();
        DisabledPanel.Children.Clear();

        foreach (var s in enabled)
            EnabledPanel.Children.Add(BuildCard(s));
        foreach (var s in disabled)
            DisabledPanel.Children.Add(BuildCard(s));

        EnabledHeaderText.Text = $"Enabled ({enabled.Count})";
        DisabledHeaderText.Text = $"Disabled ({disabled.Count})";
        DisabledHeaderBtn.Visibility = disabled.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DisabledPanel.Visibility = _disabledExpanded ? Visibility.Visible : Visibility.Collapsed;
        EnabledPanel.Visibility = _enabledExpanded ? Visibility.Visible : Visibility.Collapsed;

        var total = _allSkills.Count;
        CountText.Text = total > 0 ? $"({enabled.Count}/{total} enabled)" : "";

        if (total > 0)
        {
            SkillsGroups.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            SkillsGroups.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private Grid BuildCard(SkillData s)
    {
        var card = new Grid
        {
            Padding = new Thickness(16, 10, 16, 12),
            Margin = new Thickness(0, 2, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        double contentOpacity = s.IsEnabled ? 1.0 : 0.5;

        // Row 0, Col 0: Name + badge
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Opacity = contentOpacity };
        nameRow.Children.Add(new TextBlock { Text = s.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var badgeBg = s.IsEnabled
            ? new SolidColorBrush(Color.FromArgb(40, 76, 175, 80))
            : new SolidColorBrush(Color.FromArgb(40, 230, 168, 23));
        var badgeFg = s.IsEnabled
            ? new SolidColorBrush(Colors.LimeGreen)
            : new SolidColorBrush(Color.FromArgb(255, 230, 168, 23));
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
            Background = badgeBg, VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock { Text = s.IsEnabled ? "enabled" : "disabled", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = badgeFg, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(badge);
        Grid.SetRow(nameRow, 0);
        Grid.SetColumn(nameRow, 0);
        card.Children.Add(nameRow);

        // Row 0, Col 1: Source
        var source = new TextBlock
        {
            Text = s.Source, FontSize = 11, FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
            Opacity = contentOpacity
        };
        Grid.SetRow(source, 0);
        Grid.SetColumn(source, 1);
        card.Children.Add(source);

        // Row 0, Col 2: Toggle button
        var toggleBtn = new Button
        {
            Tag = s.SkillKey,
            Padding = new Thickness(6, 4, 6, 4), MinWidth = 0, MinHeight = 0
        };
        ToolTipService.SetToolTip(toggleBtn, s.IsEnabled ? "Disable" : "Enable");
        toggleBtn.Content = new FontIcon { Glyph = s.IsEnabled ? "\uE769" : "\uE768", FontSize = 12 };
        toggleBtn.Click += OnToggleSkillClick;
        Grid.SetRow(toggleBtn, 0);
        Grid.SetColumn(toggleBtn, 2);
        card.Children.Add(toggleBtn);

        // Row 1: Description
        if (!string.IsNullOrEmpty(s.Description))
        {
            var desc = new TextBlock
            {
                Text = s.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Opacity = contentOpacity
            };
            Grid.SetRow(desc, 1);
            Grid.SetColumnSpan(desc, 3);
            card.Children.Add(desc);
        }

        return card;
    }

    private class SkillData
    {
        public string Id { get; set; } = "";
        public string SkillKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
    }
}
