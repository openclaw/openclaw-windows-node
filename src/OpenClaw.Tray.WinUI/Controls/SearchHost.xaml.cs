using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;

namespace OpenClawTray.Controls;

[Bindable]
public sealed class SearchDisplayRow
{
    internal HubSearchItem Item { get; }
    internal long Generation { get; }
    internal SearchDisplayRow(HubSearchItem item, long generation) { Item = item; Generation = generation; }
    public string Title => Item.Title;
    public string? Detail => Item.Subtitle;
    public string Icon => Item.Icon;
    public string Badge => Item.OpensDashboard ? LocalizationHelper.GetString("Search_Dashboard") + " \u2197" : "";
}

[Bindable]
public sealed class SearchDisplayGroup : ObservableCollection<SearchDisplayRow>
{
    public string Title { get; }
    internal SearchDisplayGroup(string title) => Title = title;
}

public sealed partial class SearchHost : UserControl
{
    internal static Style CreatePresenterStyle() => new(typeof(FlyoutPresenter))
    {
        Setters = { new Setter(MaxWidthProperty, double.PositiveInfinity) }
    };

    private HubSearchController? _controller;
    private Action<HubSearchTarget>? _navigate;
    private Action? _close;
    private bool _executed;
    private bool _composing;
    private bool _resetting;
    private bool _revealingMore;
    private string? _renderedQuery;
    private IReadOnlyList<HubSearchItem> _displayedItems = [];
    private SearchDisplayGroup[] _displayedGroups = [];
    private long _displayedGeneration = -1;

    public SearchHost()
    {
        InitializeComponent();
        SearchBox.TextCompositionStarted += (_, _) => _composing = true;
        SearchBox.TextCompositionEnded += (_, _) =>
        {
            _composing = false;
            _renderedQuery = null;
            _displayedGeneration = -1;
            if (_controller?.IsOwner(this) == true) _controller.SetQuery(SearchBox.Text);
        };
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                Layout.Width = Math.Min(500, Math.Max(240, root.Size.Width - 48));
                ResultsList.MaxHeight = Math.Min(420, Math.Max(120, root.Size.Height - 220));
            }
            SearchBox.Focus(FocusState.Programmatic);
        };
        AutomationProperties.SetName(ResultsList, LocalizationHelper.GetString("Search_Results"));
    }

    internal void Begin(HubSearchController controller, Action<HubSearchTarget> navigate, Action close,
        string inputId)
    {
        End();
        _controller = controller;
        _navigate = navigate;
        _close = close;
        _executed = false;
        _composing = false;
        _renderedQuery = null;
        AutomationProperties.SetAutomationId(SearchBox, inputId);
        _resetting = true;
        SearchBox.Text = "";
        _resetting = false;
        _controller.Changed += Render;
        _controller.Open(this);
    }

    internal void End()
    {
        if (_controller is null) return;
        _controller.Changed -= Render;
        _controller.Close(this);
        _controller = null;
        _navigate = null;
        _close = null;
        // Flyout owns focus restoration. Moving focus here races a queued reopen.
    }

    internal void FocusInput()
    {
        if (_controller?.IsOwner(this) == true) SearchBox.Focus(FocusState.Programmatic);
    }

    private void Render()
    {
        if (_controller?.IsOwner(this) != true) return;
        var items = _controller.Results.Select(match => match.Item).ToArray();
        if (_revealingMore && _displayedGeneration == _controller.Generation)
        {
            // Keep the native grouped view and its scroll anchor while adding the new suffixes.
            foreach (var group in _displayedGroups)
                foreach (var item in items.Where(item => item.Group == group[0].Item.Group).Skip(group.Count))
                    group.Add(new SearchDisplayRow(item, _controller.Generation));
            _displayedItems = items;
            ResultsList.UpdateLayout();
            for (var index = 0; index < ResultsList.Items.Count; index++)
                if (ResultsList.ContainerFromIndex(index) is ListViewItem container)
                    AutomationProperties.SetAutomationId(container, $"SearchResult_{index}");
        }
        else if (_displayedGeneration != _controller.Generation || !_displayedItems.SequenceEqual(items))
        {
            var selected = (ResultsList.SelectedItem as SearchDisplayRow)?.Item.Id;
            var sameQuery = _renderedQuery == _controller.Query;
            var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
            var restoreFocus = sameQuery && _displayedGeneration == _controller.Generation &&
                IsResultFocused(focused);
            var inputFocus = sameQuery && ReferenceEquals(focused, SearchBox);
            var scroll = FindScrollViewer(ResultsList);
            var offset = sameQuery && !_revealingMore ? scroll?.VerticalOffset : null;
            if (!sameQuery) selected = null;
            _renderedQuery = _controller.Query;
            var rows = items.Select(item => new SearchDisplayRow(item, _controller.Generation)).ToArray();
            var groups = rows.GroupBy(row => row.Item.Group).Select(group =>
            {
                var display = new SearchDisplayGroup(L($"Search_Group_{group.Key}"));
                foreach (var row in group) display.Add(row);
                return display;
            }).ToArray();
            _displayedGroups = groups;
            ResultsList.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
            var survivingSelection = rows.FirstOrDefault(row => row.Item.Id == selected);
            ResultsList.SelectedItem = survivingSelection ?? rows.FirstOrDefault();
            _displayedItems = items;
            _displayedGeneration = _controller.Generation;
            if (offset is not null || restoreFocus)
            {
                ResultsList.UpdateLayout();
                if (offset.HasValue) scroll?.ChangeView(null, offset.Value, null, disableAnimation: true);
                if (restoreFocus && survivingSelection is { } current)
                {
                    ResultsList.ScrollIntoView(current);
                    ResultsList.UpdateLayout();
                    if (ResultsList.ContainerFromItem(current) is ListViewItem container)
                        container.Focus(FocusState.Keyboard);
                }
                else if (inputFocus || restoreFocus) SearchBox.Focus(FocusState.Programmatic);
            }
        }
        var statuses = _controller.Statuses.Select(status =>
            LocalizationHelper.Format($"Search_Status_{status.State}", L($"Search_Group_{status.Group}")));
        var statusText = string.Join("\n", statuses);
        if (_controller.HasDashboardCompatibilityWarning)
            statusText = string.Join("\n", new[] { statusText, L("Search_Compatibility") }
                .Where(text => text.Length > 0));
        if (statusText.Length == 0 && items.Length == 0)
            statusText = L("Search_Empty");
        SetInputStatus(statusText);
        MoreButton.Visibility = _controller.HasMoreResults ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = _controller.Statuses.Any(status => status.State == "Failed") ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string L(string key) => LocalizationHelper.GetString(key);

    private void SetInputStatus(string status)
    {
        if (AutomationProperties.GetItemStatus(SearchBox) == status) return;
        AutomationProperties.SetItemStatus(SearchBox, status);
        if (status.Length > 0)
            FrameworkElementAutomationPeer.FromElement(SearchBox)?.RaiseNotificationEvent(
                AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, status, "NavigationSearch");
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_resetting && !_composing && _controller is { } controller &&
            controller.IsOwner(this) && controller.Query != SearchBox.Text.Trim())
            controller.SetQuery(SearchBox.Text);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_composing || _controller?.IsOwner(this) != true) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape) { e.Handled = true; _close?.Invoke(); }
        else if (e.Key == VirtualKey.Enter)
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (!ctrl && !IsWithin(focused, SearchBox) && !IsResultFocused(focused)) return;
            e.Handled = true;
            if (!ctrl && _controller is { } controller && controller.Query != SearchBox.Text.Trim())
                controller.SetQuery(SearchBox.Text);
            if (!ctrl && ResultsList.SelectedItem is SearchDisplayRow row) Activate(row, alt);
        }

        else if (e.Key == VirtualKey.Down && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), SearchBox))
        {
            if (ResultsList.SelectedItem is not SearchDisplayRow row) return;
            ResultsList.ScrollIntoView(row);
            ResultsList.UpdateLayout();
            if (ResultsList.ContainerFromItem(row) is ListViewItem container)
                e.Handled = container.Focus(FocusState.Keyboard);
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject root)
    {
        for (; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, root)) return true;
        return false;
    }

    private bool IsResultFocused(DependencyObject? element)
    {
        for (; element is not null && !ReferenceEquals(element, ResultsList); element = VisualTreeHelper.GetParent(element))
            if (element is ListViewItem) return IsWithin(element, ResultsList);
        return false;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchDisplayRow row) Activate(row, false);
    }

    private void Activate(SearchDisplayRow row, bool dashboard)
    {
        if (_executed || _composing || _controller?.IsOwner(this) != true ||
            !_controller.CanActivate(row.Generation, row.Item)) return;
        if (dashboard && row.Item.DashboardPath is null)
        {
            SetInputStatus(L("Search_NoCounterpart"));
            return;
        }
        var target = dashboard ? new HubSearchTarget(HubSearchTargetKind.Dashboard, row.Item.DashboardPath!) : row.Item.Target;
        _executed = true;
        var navigate = _navigate;
        _close?.Invoke();
        navigate?.Invoke(target);
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_controller?.IsOwner(this) != true) return;
        var previous = _displayedItems.Select(item => item.Id).ToHashSet();
        // More can disappear during the rebuild. Move focus before WinUI picks an old row.
        SearchBox.Focus(FocusState.Programmatic);
        _revealingMore = true;
        try { _controller.ShowMore(); }
        finally { _revealingMore = false; }
        var added = ResultsList.Items.OfType<SearchDisplayRow>()
            .FirstOrDefault(row => !previous.Contains(row.Item.Id));
        if (added is null) return;
        ResultsList.SelectedItem = added;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_controller?.IsOwner(this) != true || _displayedGeneration != added.Generation ||
                !ReferenceEquals(ResultsList.SelectedItem, added)) return;
            ResultsList.UpdateLayout();
            ResultsList.ScrollIntoView(added, ScrollIntoViewAlignment.Leading);
            ResultsList.UpdateLayout();
            if (ResultsList.ContainerFromItem(added) is ListViewItem container)
            {
                container.Focus(FocusState.Keyboard);
                container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        });
    }
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_controller?.IsOwner(this) == true) _controller.Retry();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
            args.ItemContainer.ClearValue(AutomationProperties.AutomationIdProperty);
            return;
        }
        if (args.Item is not SearchDisplayRow row) return;
        AutomationProperties.SetAutomationId(args.ItemContainer, $"SearchResult_{args.ItemIndex}");
        AutomationProperties.SetName(args.ItemContainer, $"{row.Title}. {row.Detail}. {row.Badge}");
        if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root &&
            root.FindName("ResultTitle") is TextBlock title)
        {
            title.Inlines.Clear();
            var query = _controller?.Query ?? "";
            var index = query.Length == 0 ? -1 : row.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0) { title.Inlines.Add(new Run { Text = row.Title }); return; }
            title.Inlines.Add(new Run { Text = row.Title[..index] });
            title.Inlines.Add(new Run { Text = row.Title.Substring(index, query.Length), FontWeight = FontWeights.SemiBold });
            title.Inlines.Add(new Run { Text = row.Title[(index + query.Length)..] });
        }
    }
}
