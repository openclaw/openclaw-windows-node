using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;

namespace OpenClawTray.Pages;

public sealed partial class AgentEventsPage : Page
{
    private const int MaxEvents = 400;
    private readonly List<AgentEventInfo> _allEvents = new();
    private string _activeFilter = "all";

    /// <summary>Set by HubWindow so Clear can also clear the central cache.</summary>
    public Action? ClearCentralCache { get; set; }

    public int EventCount => _allEvents.Count;

    public AgentEventsPage()
    {
        InitializeComponent();
    }

    public void AddEvent(AgentEventInfo evt)
    {
        _allEvents.Insert(0, evt); // Newest first
        if (_allEvents.Count > MaxEvents)
            _allEvents.RemoveRange(MaxEvents, _allEvents.Count - MaxEvents);

        ApplyFilter();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        var tag = clicked.Tag?.ToString() ?? "all";

        // Uncheck all others, check clicked
        foreach (var child in ((StackPanel)clicked.Parent).Children)
        {
            if (child is ToggleButton tb)
                tb.IsChecked = tb == clicked;
        }

        _activeFilter = tag;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = _activeFilter == "all"
            ? _allEvents
            : _allEvents.Where(e => e.Stream.Equals(_activeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        EventsList.ItemsSource = filtered;
        EventsList.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = filtered.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = $"({_allEvents.Count})";
        StatusText.Text = $"{filtered.Count} of {_allEvents.Count} events";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _allEvents.Clear();
        ClearCentralCache?.Invoke();
        ApplyFilter();
    }
}
