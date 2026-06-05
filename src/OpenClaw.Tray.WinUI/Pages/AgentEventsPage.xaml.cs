using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;

namespace OpenClawTray.Pages;

public sealed partial class AgentEventsPage : Page
{
    private const int MaxEvents = 400;
    private readonly List<AgentEventInfo> _allEvents = new();
    private string? _agentIdFilter;
    private bool _filterDirty;
    private AppState? _appState;

    /// <summary>Set by HubWindow so Clear can also clear the central cache.</summary>
    public Action? ClearCentralCache { get; set; }

    public int EventCount => _allEvents.Count;

    // UI-only policy: true when expanding the row reveals content beyond the summary line.
    private static bool CanExpand(AgentEventInfo evt)
    {
        if (evt.IsAssistantStream)
        {
            var full = evt.FullAssistantText;
            return !string.IsNullOrEmpty(full) && full != evt.SummaryLine;
        }
        return evt.ShowDataJson;
    }

    /// <summary>Filter events to a specific agent by session key prefix.</summary>
    public void SetAgentFilter(string? agentId)
    {
        _agentIdFilter = agentId;
        ApplyFilter();
    }

    public void PopulateAgentFilter(HubWindow hub)
    {
        AgentFilterCombo.SelectionChanged -= OnAgentFilterComboChanged;
        AgentFilterCombo.Items.Clear();
        AgentFilterCombo.Items.Add(new ComboBoxItem { Content = LocalizationHelper.GetString("AgentEventsPage_AllAgents"), Tag = "" });
        foreach (var id in hub.GetAgentIds())
            AgentFilterCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
        AgentFilterCombo.SelectedIndex = 0;
        AgentFilterCombo.SelectionChanged += OnAgentFilterComboChanged;
    }

    private void OnAgentFilterComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (AgentFilterCombo.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag as string;
            SetAgentFilter(string.IsNullOrEmpty(tag) ? null : tag);
        }
    }

    private bool _initialized;

    public AgentEventsPage()
    {
        InitializeComponent();
        _initialized = true;
        Unloaded += (_, _) =>
        {
            if (_appState != null)
            {
                _appState.AgentEventAdded -= OnAgentEventAdded;
                _appState.PropertyChanged -= OnAppStateChanged;
            }
        };
    }

    public void Initialize(HubWindow hub)
    {
        _appState = ((App)Application.Current!).AppState!;
        _appState.AgentEventAdded += OnAgentEventAdded;
        _appState.PropertyChanged += OnAppStateChanged;
        PopulateAgentFilter(hub);
        UpdateLiveBadge();
    }

    private void UpdateLiveBadge()
    {
        var connected = _appState?.Status == ConnectionStatus.Connected;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (connected)
            {
                LiveDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(255, 255, 68, 68));
                LiveText.Text = "Live";
                LiveText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(255, 255, 68, 68));
                LiveBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(32, 255, 68, 68));
            }
            else
            {
                LiveDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(255, 128, 128, 128));
                LiveText.Text = "Offline";
                LiveText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(255, 128, 128, 128));
                LiveBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(32, 128, 128, 128));
            }
        });
    }

    private void OnAppStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.AgentEvents) && _appState?.AgentEvents.Count == 0)
        {
            _allEvents.Clear();
            ApplyFilter();
        }
        else if (e.PropertyName == nameof(AppState.Status))
        {
            UpdateLiveBadge();
        }
    }

    private void OnAgentEventAdded(AgentEventInfo evt)
    {
        AddEvent(evt);
    }

    public void AddEvent(AgentEventInfo evt)
    {
        // Deduplicate by RunId + Seq
        if (_allEvents.Any(e => e.RunId == evt.RunId && e.Seq == evt.Seq))
            return;

        // For assistant events, replace earlier streaming chunks with the latest one
        // (each chunk contains the full accumulated text, so only the latest matters)
        if (evt.Stream.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            _allEvents.RemoveAll(e =>
                e.Stream.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                e.RunId == evt.RunId && e.Seq < evt.Seq);
        }

        _allEvents.Insert(0, evt);
        if (_allEvents.Count > MaxEvents)
            _allEvents.RemoveRange(MaxEvents, _allEvents.Count - MaxEvents);

        // Debounce UI updates — mark dirty, update on next idle
        if (!_filterDirty)
        {
            _filterDirty = true;
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _filterDirty = false;
                ApplyFilter();
            });
        }
    }


    private void ApplyFilter()
    {
        IEnumerable<AgentEventInfo> filtered = _allEvents;

        // Filter by agent
        if (!string.IsNullOrEmpty(_agentIdFilter))
            filtered = filtered.Where(e => e.SessionKey != null &&
                e.SessionKey.StartsWith($"agent:{_agentIdFilter}:", StringComparison.OrdinalIgnoreCase));

        var list = filtered.ToList();
        EventsList.ItemsSource = list;
        EventsList.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = list.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = $"({_allEvents.Count})";
        StatusText.Text = string.Format(LocalizationHelper.GetString("AgentEventsPage_EventsStatus"), list.Count, _allEvents.Count);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _allEvents.Clear();
        ClearCentralCache?.Invoke();
        ApplyFilter();
    }

    private void EventsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not AgentEventInfo evt || args.ItemContainer?.ContentTemplateRoot is not Grid grid)
            return;

        if (grid.Children[0] is Grid headerGrid && headerGrid.Children[0] is Border badge)
        {
            var hex = evt.BadgeColorHex;
            try
            {
                var r = Convert.ToByte(hex[3..5], 16);
                var g = Convert.ToByte(hex[5..7], 16);
                var b = Convert.ToByte(hex[7..9], 16);
                var color = Microsoft.UI.ColorHelper.FromArgb(255, r, g, b);
                badge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(40, r, g, b));
                if (badge.Child is TextBlock badgeText)
                    badgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }
            catch
            {
                badge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(40, 100, 100, 100));
            }

            if (headerGrid.Children.Count > 2 && headerGrid.Children[2] is FontIcon chevron)
            {
                chevron.Visibility = CanExpand(evt) ? Visibility.Visible : Visibility.Collapsed;
                chevron.Glyph = evt.IsExpanded ? "\uE70E" : "\uE70D";
            }
        }

        if (grid.Children.Count > 1 && grid.Children[1] is TextBlock summaryBlock)
        {
            summaryBlock.Visibility = evt.HasSummary ? Visibility.Visible : Visibility.Collapsed;
            if (evt.IsAssistantStream)
            {
                summaryBlock.Text = evt.IsExpanded ? (evt.FullAssistantText ?? evt.SummaryLine) : evt.SummaryLine;
                summaryBlock.MaxLines = evt.IsExpanded ? 0 : 3;
            }
            else
            {
                summaryBlock.Text = evt.SummaryLine;
                summaryBlock.MaxLines = evt.IsExpanded ? 0 : 3;
            }
        }

        if (grid.Children.Count > 2 && grid.Children[2] is Grid detailGrid)
        {
            detailGrid.Visibility = (evt.IsExpanded && evt.ShowDataJson)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EventsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AgentEventInfo evt) return;
        if (!CanExpand(evt)) return;
        evt.IsExpanded = !evt.IsExpanded;

        if (sender is ListView listView)
        {
            var container = listView.ContainerFromItem(e.ClickedItem) as ListViewItem;
            if (container?.ContentTemplateRoot is Grid grid)
            {
                if (grid.Children[0] is Grid headerGrid
                    && headerGrid.Children.Count > 2 && headerGrid.Children[2] is FontIcon chevron)
                    chevron.Glyph = evt.IsExpanded ? "\uE70E" : "\uE70D";

                if (grid.Children.Count > 1 && grid.Children[1] is TextBlock summaryBlock)
                {
                    summaryBlock.Text = evt.IsAssistantStream && evt.IsExpanded
                        ? (evt.FullAssistantText ?? evt.SummaryLine)
                        : evt.SummaryLine;
                    summaryBlock.MaxLines = evt.IsExpanded ? 0 : 3;
                }

                if (grid.Children.Count > 2 && grid.Children[2] is Grid detailGrid)
                    detailGrid.Visibility = (evt.IsExpanded && evt.ShowDataJson)
                        ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
