// Adapts the native annotated rail to ordinal message navigation.
// The ItemsView binding supplies Update(anchors) and SetScrollBar(bar); Dispose releases native subscriptions.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using Windows.System;

namespace OpenClawTray.Chat;

internal sealed record ChatHistoryAnchor(int RowIndex, string Key, string Sender, string Preview);

/// <summary>Owns message annotations and preview lookup, not transcript layout or scrolling.</summary>
internal sealed class ChatHistoryNavigation(ItemsView itemsView, Action<ChatHistoryAnchor> navigate) : IDisposable
{
    private AnnotatedScrollBar? _bar;
    private IReadOnlyList<ChatHistoryAnchor> _anchors = [];
    private IReadOnlyList<ChatHistoryAnchor> _ticks = [];
    private double _extent;
    private int _navigationVersion;
    private string? _selectedKey;
    private string? _hoveredKey;
    private readonly HashSet<int> _visibleRows = [];

    internal static IReadOnlyList<ChatHistoryAnchor> Project(IReadOnlyList<ReactorTimelineRow> rows) =>
        rows.Select((row, index) => (row, index))
            .Where(item => item.row.Entry?.Kind is ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant)
            .Select(item =>
            {
                var (row, index) = item;
                var user = row.Entry!.Kind == ChatTimelineItemKind.User;
                var text = row.Entry.Text;
                var length = Math.Min(text.Length, ChatVisuals.HistoryPreviewLength);
                if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]))
                    length--;
                return new ChatHistoryAnchor(index, row.Key,
                    user ? row.Props.Timeline.UserSenderLabel
                        : row.Props.AssistantIdentity?.DisplayName ?? row.Props.Timeline.AssistantSenderLabel,
                    length > 0 ? text[..length] : ChatAssistantMediaRenderer.LocalizedOrDefault(
                        "Chat_AssistantMedia_MediaAttachment", "Media attachment"));
            }).ToArray();

    /// <summary>Rebinds the Reactor-owned native rail without retaining retired controls.</summary>
    public void SetScrollBar(AnnotatedScrollBar? bar)
    {
        if (ReferenceEquals(_bar, bar))
            return;
        _navigationVersion++;
        if (_bar is not null)
        {
            _bar.Scrolling -= OnScrolling;
            _bar.DetailLabelRequested -= OnDetailLabelRequested;
            _bar.KeyDown -= OnKeyDown;
            _bar.PointerExited -= OnPointerExited;
            _bar.LayoutUpdated -= OnLayoutUpdated;
            _bar.Labels.Clear();
        }
        _bar = bar;
        if (_bar is not null)
        {
            _bar.Scrolling += OnScrolling;
            _bar.DetailLabelRequested += OnDetailLabelRequested;
            _bar.KeyDown += OnKeyDown;
            _bar.PointerExited += OnPointerExited;
            _bar.LayoutUpdated += OnLayoutUpdated;
            Refresh(force: true);
        }
    }

    /// <summary>Reconciles stable message identities, including prepended history and streaming previews.</summary>
    public void Update(IReadOnlyList<ChatHistoryAnchor> anchors)
    {
        var changed = !_anchors.SequenceEqual(anchors);
        _anchors = anchors;
        if (_hoveredKey is not null && !_anchors.Any(anchor => anchor.Key == _hoveredKey))
            _hoveredKey = null;
        if (_selectedKey is not null && !_anchors.Any(anchor => anchor.Key == _selectedKey))
        {
            _selectedKey = null;
            if (_bar is not null)
                AutomationProperties.SetItemStatus(_bar, "");
        }
        Refresh(changed);
    }

    public void Refresh(bool force = false)
    {
        // WinUI maps labels over Maximum + ViewportSize, not just ScrollableHeight.
        // Its zero-range calculation substitutes one DIP for Maximum - Minimum.
        var extent = itemsView.ScrollView is { ViewportHeight: > 0 } scroll
            ? Math.Max(1, scroll.ScrollableHeight) + scroll.ViewportHeight : 1;
        var scale = _bar?.XamlRoot?.RasterizationScale ?? 1;
        var height = GetLabelsGrid()?.ActualHeight ?? 0;
        // Budget physical pixels so independent native label rounding cannot shrink the clear gap.
        var tickPixels = Math.Round(ChatVisuals.HistoryTickHeight * scale, MidpointRounding.AwayFromZero);
        var pitchPixels = tickPixels + Math.Ceiling(ChatVisuals.HistoryTickMinimumGap * scale);
        var capacity = height * scale < tickPixels ? 0
            : 1 + (int)Math.Floor((Math.Floor(height * scale) - tickPixels) / pitchPixels);
        var count = Math.Min(_anchors.Count, capacity);
        if (!force && _extent == extent && _ticks.Count == count)
        {
            UpdateVisuals();
            return;
        }
        _extent = extent;
        if (_bar is null)
            return;
        var ticks = new ChatHistoryAnchor[count];
        _bar.Labels.Clear();
        for (var index = 0; index < count; index++)
        {
            var ordinal = count == 1 ? _anchors.Count - 1
                : (int)Math.Round((double)index * (_anchors.Count - 1) / (count - 1));
            ticks[index] = _anchors[ordinal];
            _bar.Labels.Add(new AnnotatedScrollBarLabel(ticks[index],
                count == 1 ? extent / 2 : extent * index / (count - 1)));
        }
        _ticks = ticks;
    }

    internal ChatHistoryAnchor? AnchorAt(double offset)
    {
        if (_ticks.Count == 0 || !double.IsFinite(offset))
            return null;
        var index = (int)Math.Round(Math.Clamp(offset / _extent, 0, 1) * (_ticks.Count - 1));
        return _ticks[index];
    }

    internal bool IsCurrent(int rowIndex, string key) =>
        _anchors.Any(anchor => anchor.RowIndex == rowIndex && anchor.Key == key);

    private void OnDetailLabelRequested(AnnotatedScrollBar sender, AnnotatedScrollBarDetailLabelRequestedEventArgs args)
    {
        var anchor = AnchorAt(args.ScrollOffset);
        args.Content = anchor;
        if (_hoveredKey != anchor?.Key)
        {
            _hoveredKey = anchor?.Key;
            UpdateVisuals();
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _hoveredKey = null;
        UpdateVisuals();
    }

    private void OnLayoutUpdated(object? sender, object args) => Refresh();

    private Grid? GetLabelsGrid() =>
        _bar is not null && VisualTreeHelper.GetChildrenCount(_bar) > 0
            && VisualTreeHelper.GetChild(_bar, 0) is FrameworkElement root
            ? root.FindName("PART_LabelsGrid") as Grid : null;

    // The web rail uses muted at 40%, muted for messages in view, and text for
    // hover/press. Native labels retain collision handling and ordinal hit testing.
    private void UpdateVisuals()
    {
        if (_bar is null || GetLabelsGrid() is not { } labels)
            return;

        _visibleRows.Clear();
        if (itemsView.ScrollView is { Content: ItemsRepeater repeater } scroll)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(repeater); index++)
            {
                if (VisualTreeHelper.GetChild(repeater, index) is not FrameworkElement
                    { IsLoaded: true, ActualHeight: > 0 } element)
                    continue;
                var rowIndex = repeater.GetElementIndex(element);
                if (rowIndex < 0)
                    continue;
                var bounds = element.TransformToVisual(scroll).TransformBounds(
                    new global::Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (bounds.Bottom > 0 && bounds.Top < scroll.ViewportHeight)
                    _visibleRows.Add(rowIndex);
            }
        }

        var hovered = -1;
        for (var index = 0; index < labels.Children.Count; index++)
        {
            if (labels.Children[index] is ContentPresenter
                { Content: AnnotatedScrollBarLabel { Content: ChatHistoryAnchor anchor } }
                && anchor.Key == _hoveredKey)
            {
                hovered = index;
                break;
            }
        }
        for (var index = 0; index < labels.Children.Count; index++)
        {
            if (labels.Children[index] is not ContentPresenter
                { Content: AnnotatedScrollBarLabel { Content: ChatHistoryAnchor anchor } } presenter
                || VisualTreeHelper.GetChildrenCount(presenter) == 0
                || VisualTreeHelper.GetChild(presenter, 0) is not Control tick)
                continue;
            tick.MaxWidth = Math.Max(0, _bar.ActualWidth - 8);
            var distance = hovered < 0 ? -1 : Math.Abs(index - hovered);
            var state = distance switch
            {
                0 => "PointerOver",
                1 => "HoverAdjacent",
                2 => "HoverNear",
                3 => "HoverFar",
                _ => "Normal",
            };
            VisualStateManager.GoToState(tick, state, false);
            VisualStateManager.GoToState(tick,
                distance == 0 ? "Highlighted" : _visibleRows.Contains(anchor.RowIndex) ? "Visible" : "Quiet", false);
        }
    }

    private void OnScrolling(AnnotatedScrollBar sender, AnnotatedScrollBarScrollingEventArgs args)
    {
        if (args.ScrollingEventKind is not (AnnotatedScrollBarScrollingEventKind.Click or AnnotatedScrollBarScrollingEventKind.Drag))
            return;
        args.Cancel = true;
        if (AnchorAt(args.ScrollOffset) is { } anchor)
        {
            _hoveredKey = anchor.Key;
            UpdateVisuals();
            Request(anchor);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_anchors.Count == 0)
            return;
        var current = _selectedKey is null ? _anchors.Count - 1
            : _anchors.ToList().FindIndex(anchor => anchor.Key == _selectedKey);
        var index = args.Key switch
        {
            VirtualKey.Home => 0,
            VirtualKey.End => _anchors.Count - 1,
            VirtualKey.Up => Math.Max(0, current - 1),
            VirtualKey.Down => Math.Min(_anchors.Count - 1, current + 1),
            VirtualKey.Enter or VirtualKey.Space => current,
            _ => -1,
        };
        if (index < 0)
            return;
        args.Handled = true;
        Request(_anchors[index]);
    }

    private void Request(ChatHistoryAnchor anchor)
    {
        _selectedKey = anchor.Key;
        if (_bar is not null)
            AutomationProperties.SetItemStatus(_bar, $"{anchor.Sender}: {anchor.Preview}");
        var version = ++_navigationVersion;
        // Native pointer capture raises its cancellation notification after Scrolling.
        // Start only after that event, resolving the live keyed row after any prepend.
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _navigationVersion && itemsView.IsLoaded && _bar is not null
                && _anchors.FirstOrDefault(candidate => candidate.Key == anchor.Key) is { } current)
                navigate(current);
        }))
            Services.Logger.Warn("Chat history navigation could not be dispatched.");
    }

    public void Dispose()
    {
        _navigationVersion++;
        SetScrollBar(null);
        _anchors = [];
        _ticks = [];
    }
}
