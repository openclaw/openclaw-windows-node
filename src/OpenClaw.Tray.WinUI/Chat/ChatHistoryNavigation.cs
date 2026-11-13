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
    private int _navigationVersion;
    private string? _selectedKey;
    private string? _hoveredKey;
    private readonly HashSet<int> _visibleRows = [];
    private bool _visualUpdateQueued;
    private double _labelHeight;
    private double _scale;
    private int _presenterCount = -1;
    private UIElement? _firstPresenter;
    private PointerEventHandler? _pointerPositionChanged;

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
        _pointerPositionChanged ??= OnPointerPositionChanged;
        _navigationVersion++;
        CancelVisualUpdate();
        if (_bar is not null)
        {
            _bar.Scrolling -= OnScrolling;
            _bar.DetailLabelRequested -= OnDetailLabelRequested;
            _bar.KeyDown -= OnKeyDown;
            _bar.PointerExited -= OnPointerExited;
            _bar.LayoutUpdated -= OnLayoutUpdated;
            _bar.Loaded -= OnLoaded;
            _bar.Unloaded -= OnUnloaded;
            _bar.RemoveHandler(UIElement.PointerEnteredEvent, _pointerPositionChanged);
            _bar.RemoveHandler(UIElement.PointerMovedEvent, _pointerPositionChanged);
            _bar.RemoveHandler(UIElement.PointerPressedEvent, _pointerPositionChanged);
            _bar.Labels.Clear();
        }
        _bar = bar;
        _presenterCount = -1;
        _firstPresenter = null;
        _hoveredKey = null;
        if (_bar is not null)
        {
            // This is an ordinal navigator, not a pixel scrollbar. Virtualization changes
            // the transcript's estimated extent, which must not recreate native labels.
            _bar.ScrollController.SetValues(0, 1, 0, 0);
            _bar.ScrollController.SetIsScrollable(true);
            _bar.Scrolling += OnScrolling;
            _bar.DetailLabelRequested += OnDetailLabelRequested;
            _bar.KeyDown += OnKeyDown;
            _bar.PointerExited += OnPointerExited;
            _bar.LayoutUpdated += OnLayoutUpdated;
            _bar.Loaded += OnLoaded;
            _bar.Unloaded += OnUnloaded;
            _bar.AddHandler(UIElement.PointerEnteredEvent, _pointerPositionChanged, true);
            _bar.AddHandler(UIElement.PointerMovedEvent, _pointerPositionChanged, true);
            _bar.AddHandler(UIElement.PointerPressedEvent, _pointerPositionChanged, true);
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
        if (changed)
        {
            UpdatePreviewContent();
            if (_selectedKey is { } key && _bar is not null
                && _anchors.FirstOrDefault(anchor => anchor.Key == key) is { } selected)
                AutomationProperties.SetItemStatus(_bar, $"{selected.Sender}: {selected.Preview}");
        }
    }

    public void Refresh(bool force = false)
    {
        var scale = _bar?.XamlRoot?.RasterizationScale ?? 1;
        var height = GetLabelsGrid()?.ActualHeight ?? 0;
        _labelHeight = height;
        _scale = scale;
        // Budget physical pixels so independent native label rounding cannot shrink the clear gap.
        var tickPixels = Math.Round(ChatVisuals.HistoryTickHeight * scale, MidpointRounding.AwayFromZero);
        var pitchPixels = tickPixels + Math.Ceiling(ChatVisuals.HistoryTickMinimumGap * scale);
        var capacity = height * scale < tickPixels ? 0
            : 1 + (int)Math.Floor((Math.Floor(height * scale) - tickPixels) / pitchPixels);
        var count = Math.Min(_anchors.Count, capacity);
        if (!force && _ticks.Count == count)
        {
            QueueVisualUpdate();
            return;
        }
        if (_bar is null)
            return;
        var ticks = new ChatHistoryAnchor[count];
        var replaceLabels = _ticks.Count != count || _bar.Labels.Count != count;
        for (var index = 0; index < count; index++)
        {
            var ordinal = count == 1 ? _anchors.Count - 1
                : (int)Math.Round((double)index * (_anchors.Count - 1) / (count - 1));
            ticks[index] = _anchors[ordinal];
            replaceLabels |= index >= _ticks.Count
                || ticks[index].Key != _ticks[index].Key || ticks[index].RowIndex != _ticks[index].RowIndex;
        }
        _ticks = ticks;
        if (replaceLabels)
        {
            _bar.Labels.Clear();
            for (var index = 0; index < count; index++)
                _bar.Labels.Add(new AnnotatedScrollBarLabel(ticks[index],
                    count == 1 ? 0.5 : (double)index / (count - 1)));
        }
        QueueVisualUpdate();
    }

    internal ChatHistoryAnchor? AnchorAt(double offset)
    {
        if (_ticks.Count == 0 || !double.IsFinite(offset))
            return null;
        var index = (int)Math.Round(Math.Clamp(offset, 0, 1) * (_ticks.Count - 1));
        return _ticks[index];
    }

    internal bool IsCurrent(int rowIndex, string key) =>
        _anchors.Any(anchor => anchor.RowIndex == rowIndex && anchor.Key == key);

    internal ChatHistoryAnchor? AnchorAtPointer(double verticalPosition)
    {
        var span = (GetLabelsGrid()?.ActualHeight ?? 0)
            - (GetTemplatePart<FrameworkElement>("PART_VerticalThumb")?.ActualHeight ?? 0);
        return span > 0 ? AnchorAt(verticalPosition / span) : null;
    }

    private void OnPointerPositionChanged(object sender, PointerRoutedEventArgs args)
    {
        if (GetLabelsGrid() is not { } labels)
            return;
        // Native touch tooltips shift their lookup offset as well as their placement.
        // Routed input supplies the actual position after the native preview request.
        UpdatePointerPreview(args.GetCurrentPoint(labels).Position.Y);
    }

    internal void UpdatePointerPreview(double verticalPosition)
    {
        _hoveredKey = AnchorAtPointer(verticalPosition)?.Key;
        UpdatePreviewContent();
        QueueVisualUpdate();
    }

    private void UpdatePreviewContent()
    {
        if (GetTemplatePart<ContentPresenter>("PART_TooltipContentPresenter") is { } presenter)
            presenter.Content = _anchors.FirstOrDefault(anchor => anchor.Key == _hoveredKey);
    }

    private void OnDetailLabelRequested(AnnotatedScrollBar sender, AnnotatedScrollBarDetailLabelRequestedEventArgs args)
    {
        var anchor = AnchorAt(args.ScrollOffset);
        args.Content = anchor;
        if (_hoveredKey != anchor?.Key)
        {
            _hoveredKey = anchor?.Key;
            QueueVisualUpdate();
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _hoveredKey = null;
        QueueVisualUpdate();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => Refresh(force: true);

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelVisualUpdate();
        _presenterCount = -1;
        _firstPresenter = null;
    }

    private void OnLayoutUpdated(object? sender, object args)
    {
        var labels = GetLabelsGrid();
        if ((labels?.ActualHeight ?? 0) != _labelHeight
            || (_bar?.XamlRoot?.RasterizationScale ?? 1) != _scale)
            Refresh();
        else if (labels is not null && (labels.Children.Count != _presenterCount
            || !ReferenceEquals(labels.Children.FirstOrDefault(), _firstPresenter)))
            QueueVisualUpdate();
    }

    private void QueueVisualUpdate()
    {
        if (_visualUpdateQueued || _bar is not { IsLoaded: true })
            return;
        _visualUpdateQueued = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        CancelVisualUpdate();
        UpdateVisuals();
    }

    private void CancelVisualUpdate()
    {
        if (_visualUpdateQueued)
            CompositionTarget.Rendering -= OnRendering;
        _visualUpdateQueued = false;
    }

    private Grid? GetLabelsGrid() => GetTemplatePart<Grid>("PART_LabelsGrid");

    private T? GetTemplatePart<T>(string name) where T : FrameworkElement =>
        _bar is not null && VisualTreeHelper.GetChildrenCount(_bar) > 0
            && VisualTreeHelper.GetChild(_bar, 0) is FrameworkElement root
            ? root.FindName(name) as T : null;

    private void CollectVisibleRows()
    {
        _visibleRows.Clear();
        if (itemsView.ScrollView is not { Content: ItemsRepeater repeater } scroll)
            return;
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

    // The web rail uses muted at 40%, muted for messages in view, and text for
    // hover/press. Native labels retain collision handling and ordinal hit testing.
    private void UpdateVisuals()
    {
        if (_bar is null || GetLabelsGrid() is not { } labels)
            return;

        CollectVisibleRows();

        var hovered = -1;
        var ready = true;
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
            {
                ready = false;
                continue;
            }
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
        _presenterCount = ready ? labels.Children.Count : -1;
        _firstPresenter = ready ? labels.Children.FirstOrDefault() : null;
    }

    private void OnScrolling(AnnotatedScrollBar sender, AnnotatedScrollBarScrollingEventArgs args)
    {
        if (args.ScrollingEventKind is not (AnnotatedScrollBarScrollingEventKind.Click or AnnotatedScrollBarScrollingEventKind.Drag))
            return;
        args.Cancel = true;
        if (AnchorAt(args.ScrollOffset) is { } anchor)
        {
            _hoveredKey = anchor.Key;
            QueueVisualUpdate();
            Request(anchor);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_anchors.Count == 0)
            return;
        CollectVisibleRows();
        var index = KeyboardIndex(args.Key, _anchors, _selectedKey, _visibleRows);
        if (index < 0)
            return;
        args.Handled = true;
        Request(_anchors[index]);
    }

    internal static int KeyboardIndex(VirtualKey key, IReadOnlyList<ChatHistoryAnchor> anchors,
        string? selectedKey, IReadOnlySet<int> visibleRows)
    {
        if (anchors.Count == 0)
            return -1;
        var current = -1;
        var firstVisible = -1;
        for (var index = 0; index < anchors.Count; index++)
        {
            if (anchors[index].Key == selectedKey)
                current = index;
            if (firstVisible < 0 && visibleRows.Contains(anchors[index].RowIndex))
                firstVisible = index;
        }
        if (current < 0)
            current = firstVisible >= 0 ? firstVisible : anchors.Count - 1;
        return key switch
        {
            VirtualKey.Home => 0,
            VirtualKey.End => anchors.Count - 1,
            VirtualKey.Up => Math.Max(0, current - 1),
            VirtualKey.Down => Math.Min(anchors.Count - 1, current + 1),
            VirtualKey.Enter or VirtualKey.Space => current,
            _ => -1,
        };
    }

    internal void ResetSelection()
    {
        _selectedKey = null;
        if (_bar is not null)
            AutomationProperties.SetItemStatus(_bar, "");
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
