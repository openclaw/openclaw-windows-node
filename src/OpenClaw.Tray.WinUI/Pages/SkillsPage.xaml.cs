using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class SkillsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;
    private List<SkillData> _allSkills = new();
    private SetupNativeNavigationRequest? _nativeSetupRequest;
    private CancellationTokenSource? _nativeLifetime;
    private Task? _nativeLoad;
    private Exception? _nativeLoadError;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _nativeSetupRequest = e.Parameter as SetupNativeNavigationRequest;
        base.OnNavigatedTo(e);
    }

    private IOperatorGatewayClient RequireNativeClient() =>
        _nativeSetupRequest!.GetConnectedClient(CurrentApp.Registry, CurrentApp.ConnectionManager);

    internal async Task WaitForNativeSetupAsync(SetupNativeNavigationRequest request, CancellationToken ct)
    {
        while (_nativeLoad is null || !IsLoaded)
        {
            RequireNativeClient();
            if (!ReferenceEquals(_nativeSetupRequest, request)) throw new SetupNativeOwnershipException();
            await Task.Delay(50, ct);
        }
        await _nativeLoad.WaitAsync(ct);
        RequireNativeClient();
        if (!ReferenceEquals(_nativeSetupRequest, request) || CurrentAgentId != request.Completion.Verification.AgentId)
            throw new SetupNativeOwnershipException();
        if (_nativeLoadError is { } error) throw error;
    }

    public string? CurrentAgentId => GetSelectedAgentId();

    public SkillsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _nativeLifetime?.Cancel();
            _nativeLifetime?.Dispose();
            _nativeLifetime = null;
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        if (_nativeSetupRequest is { } request)
        {
            _nativeLifetime?.Cancel();
            _nativeLifetime?.Dispose();
            _nativeLifetime = new();
            PopulateAgentFilter();
            _nativeLoad = LoadNativeAsync(request, _nativeLifetime.Token);
            return;
        }
        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        PopulateAgentFilter();

        // Show cached data immediately if available
        if (_allSkills.Count == 0 && _appState.SkillsData.HasValue)
        {
            UpdateFromGateway(_appState.SkillsData.Value);
        }
        else if (_allSkills.Count > 0)
        {
            RebuildCards();
        }
        else
        {
            // No cached data — show loading spinner
            LoadingState.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }

        if (CurrentApp.GatewayClient != null)
        {
            _ = CurrentApp.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
        }
    }

    private async Task LoadNativeAsync(SetupNativeNavigationRequest request, CancellationToken ct)
    {
        _nativeLoadError = null;
        NativeSetupError.IsOpen = false;
        NativeSetupError.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Visible;
        SkillsGroups.Visibility = EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            var data = await SetupNativeSkills.LoadAsync(request, RequireNativeClient, ct);
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request, _nativeSetupRequest)) throw new SetupNativeOwnershipException();
            UpdateFromGateway(data);
        }
        catch (Exception error)
        {
            _nativeLoadError = error;
            if (ct.IsCancellationRequested) return;
            LoadingState.Visibility = Visibility.Collapsed;
            NativeSetupError.Message = LocalizationHelper.GetString(error is SetupNativeOwnershipException
                ? "Onboarding_Ready_LaunchChanged" : "Onboarding_Ready_LaunchUnavailable");
            NativeSetupError.Visibility = Visibility.Visible;
            NativeSetupError.IsOpen = true;
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_nativeSetupRequest is not null) return;
        switch (e.PropertyName)
        {
            case nameof(AppState.SkillsData):
                if (_appState!.SkillsData.HasValue) UpdateFromGateway(_appState.SkillsData.Value);
                break;
        }
    }

    private void PopulateAgentFilter()
    {
        AgentFilterCombo.SelectionChanged -= OnAgentFilterChanged;
        AgentFilterCombo.Items.Clear();
        if (_nativeSetupRequest is { } request)
        {
            var agent = request.Completion.Verification.AgentId!;
            AgentFilterCombo.Items.Add(new ComboBoxItem { Content = agent, Tag = agent });
            AgentFilterCombo.SelectedIndex = 0;
            AgentFilterCombo.IsEnabled = false;
            return;
        }
        AgentFilterCombo.IsEnabled = true;
        AgentFilterCombo.Items.Add(new ComboBoxItem { Content = "All Agents", Tag = "" });
        foreach (var id in CurrentApp.AppState?.GetAgentIds() ?? new List<string> { "main" })
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
        var client = CurrentApp.GatewayClient;
        if (client != null)
            _ = client.RequestSkillsStatusAsync(GetSelectedAgentId());
    }

    private void OnToggleSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnToggleSkillClickAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnToggleSkillClick));

    private async Task OnToggleSkillClickAsync(object sender)
    {
        if (sender is not Button btn || btn.Tag is not string skillKey) return;
        var client = _nativeSetupRequest is null ? CurrentApp.GatewayClient : RequireNativeClient();
        if (client == null) return;

        var skill = _allSkills.FirstOrDefault(s => s.SkillKey == skillKey);
        if (skill == null) return;

        bool newState = !skill.IsEnabled;
        btn.IsEnabled = false;
        var success = await client.SetSkillEnabledAsync(skillKey, newState);
        btn.IsEnabled = true;
        if (_nativeSetupRequest is not null && !ReferenceEquals(client, RequireNativeClient()))
            throw new SetupNativeOwnershipException();

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

        _allSkills = skills;
        RebuildCards();
    }

    private void RebuildCards()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        var enabled = _allSkills.Where(s => s.IsEnabled).ToList();
        var disabled = _allSkills.Where(s => !s.IsEnabled).ToList();

        EnabledPanel.Children.Clear();
        DisabledPanel.Children.Clear();

        foreach (var s in enabled)
            EnabledPanel.Children.Add(BuildCard(s));
        foreach (var s in disabled)
            DisabledPanel.Children.Add(BuildCard(s));

        EnabledHeaderText.Text = LocalizationHelper.Format("SkillsPage_EnabledHeaderFormat", enabled.Count);
        DisabledHeaderText.Text = LocalizationHelper.Format("SkillsPage_DisabledHeaderFormat", disabled.Count);
        DisabledExpander.Visibility = disabled.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = _allSkills.Count;
        CountText.Text = total > 0 ? LocalizationHelper.Format("SkillsPage_CountFormat", enabled.Count, total) : "";

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

        var badgeBgKey = s.IsEnabled ? "SystemFillColorSuccessBackgroundBrush" : "ControlFillColorSecondaryBrush";
        var badgeFgKey = s.IsEnabled ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush";
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            MinHeight = 20,
            Padding = new Thickness(8, 2, 8, 2),
            Background = (Brush)Application.Current.Resources[badgeBgKey],
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = LocalizationHelper.GetString(s.IsEnabled ? "SkillsPage_BadgeEnabled" : "SkillsPage_BadgeDisabled"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources[badgeFgKey],
            VerticalAlignment = VerticalAlignment.Center
        };
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
