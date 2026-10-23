using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Chat.Markdown;

[GenerateReactorWrapper(typeof(NativeChatMessageText), AutoDiscover = false, RegisterAssembly = false)]
[WrapContent(nameof(ContentControl.Content))]
internal partial record NativeChatMessageTextElement;

/// <summary>
/// Owns selection in one realized message, not the ItemsView or transcript.
/// Native body controls keep their layout; only their selection is coordinated.
/// </summary>
internal sealed class NativeChatMessageText : ContentControl, IDisposable
{
    internal static readonly DependencyProperty ListRowProperty = DependencyProperty.RegisterAttached(
        "ListRow", typeof(bool), typeof(NativeChatMessageText), new PropertyMetadata(false));
    private readonly Dictionary<FrameworkElement, ChatTextSurface> _surfaces = [];
    private readonly Dictionary<FrameworkElement, (int Start, int End)> _elementRanges = [];
    private readonly List<ChatTextSurface> _ordered = [];
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly KeyEventHandler _keyDownHandler;
    private Func<string, bool>? _tryCopy;
    private ChatMessageTextAutomationPeer? _peer;
    private bool _disposed;
    private bool _suspended;
    private bool _refreshPending;
    private bool _dragging;
    private Expander? _pressedDisclosure;
    private Point _summaryPressPoint;
    private bool _summaryDragged;
    private int _generation;
    private int _announcedAnchor;
    private int _announcedFocus;
    private uint _pointerId;
    private Point _pointerPosition;
    private Canvas? _caretLayer;
    private Border? _caret;
    private readonly UISettings _uiSettings = new();
    private readonly SolidColorBrush _selectionBackground = new();
    private readonly SolidColorBrush _selectionForeground = new();
    internal ChatTextSelectionDocument Document { get; } = new();

    public NativeChatMessageText()
    {
        _pointerPressedHandler = OnPointerPressed;
        _keyDownHandler = OnKeyDown;
        IsTabStop = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        UseSystemFocusVisuals = true;
        Template = (ControlTemplate)XamlReader.Load("""
            <ControlTemplate TargetType="ContentControl"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Background="Transparent">
                <ContentPresenter Content="{TemplateBinding Content}"
                    HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}" />
                <Canvas x:Name="CaretLayer" IsHitTestVisible="False"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"/>
              </Grid>
            </ControlTemplate>
            """);
        Loaded += OnLoaded;
        Attach();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _caretLayer = (Canvas)GetTemplateChild("CaretLayer");
        _caret = new Border
        {
            Width = 1,
            Background = Foreground,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAccessibilityView(_caret, AccessibilityView.Raw);
        _caretLayer.Children.Add(_caret);
    }

    internal void Update(Func<string, bool>? tryCopy)
    {
        if (_disposed)
            return;
        _tryCopy = tryCopy;
        if (_refreshPending)
            return;
        _refreshPending = true;
        var generation = _generation;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _generation)
                return;
            _refreshPending = false;
            if (!_disposed)
                RefreshDocument();
        });
    }

    private void Attach()
    {
        _suspended = false;
        _refreshPending = false;
        _generation++;
        UpdateSystemColors();
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        // Native text children handle presses before the message-level selection owner.
        AddHandler(PointerPressedEvent, _pointerPressedHandler, handledEventsToo: true);
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        DoubleTapped += OnDoubleTapped;
        AddHandler(KeyDownEvent, _keyDownHandler, handledEventsToo: true);
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        RightTapped += OnRightTapped;
    }

    private void UpdateSystemColors()
    {
        _selectionBackground.Color = _uiSettings.UIElementColor(UIElementType.Highlight);
        _selectionForeground.Color = _uiSettings.UIElementColor(UIElementType.HighlightText);
    }

    private void OnSystemColorsChanged(UISettings sender, object args)
    {
        var generation = _generation;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && generation == _generation)
                UpdateSystemColors();
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed)
            return;
        if (_suspended)
            Attach();
        RefreshDocument();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) => Suspend();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => RefreshDocument();
    private void OnGotFocus(object sender, RoutedEventArgs args) => UpdateCaret();

    private void OnLostFocus(object sender, RoutedEventArgs args)
    {
        var generation = _generation;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !IsLoaded || generation != _generation || XamlRoot is null)
                return;
            // Inactive selection stays visible until focus transfers to another message.
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused is not null)
            {
                if (focused is NativeChatMessageText message)
                {
                    if (message != this)
                        SelectRange(Document.Focus, Document.Focus);
                    break;
                }
                focused = VisualTreeHelper.GetParent(focused);
            }
            UpdateCaret();
        });
    }

    internal void RefreshDocument()
    {
        if (_disposed || _suspended || !IsLoaded || Content is not FrameworkElement content)
            return;
        var previous = new Dictionary<FrameworkElement, ChatTextSurface>(_surfaces);
        _surfaces.Clear();
        _ordered.Clear();
        _elementRanges.Clear();
        var fragments = new List<ChatTextFragment>();
        var length = 0;
        Walk(content, "\n\n");
        foreach (var surface in previous.Values.Where(item => !_surfaces.ContainsKey(item.Element)))
            surface.Restore();
        var changed = Document.Update(fragments);
        AutomationProperties.SetName(this, Document.Text);
        if (changed)
            _peer?.NotifyTextChanged();
        foreach (var surface in _ordered)
            surface.Apply(Document.Start, Document.End, _selectionBackground, _selectionForeground);
        UpdateCaret();

        void Walk(FrameworkElement element, string separator, bool marker = false)
        {
            if (element.Visibility != Visibility.Visible)
                return;
            var begin = length;
            if (element is RichTextBlock or TextBlock)
            {
                var enabled = element is RichTextBlock rich ? rich.IsTextSelectionEnabled :
                    ((TextBlock)element).IsTextSelectionEnabled;
                if (enabled || marker || previous.ContainsKey(element))
                {
                    previous.TryGetValue(element, out var old);
                    var surface = new ChatTextSurface(element, old) { Separator = separator };
                    if (surface.Text.Length > 0)
                    {
                        if (fragments.Count > 0)
                            length += separator.Length;
                        surface.Start = length;
                        length += surface.Text.Length;
                        _ordered.Add(surface);
                        _surfaces.Add(element, surface);
                        fragments.Add(new(surface.Text, separator));
                    }
                }
            }
            else if (element is Expander expander)
            {
                if (expander.Header is TextBlock summary)
                    Walk(summary, separator);
                if (expander.IsExpanded && expander.Content is FrameworkElement body)
                    Walk(body, separator);
            }
            else if (element is ButtonBase or ScrollBar or Thumb)
            {
                // These are native interactions, not characters in the visible body.
            }
            else if (element is Grid grid && (bool)grid.GetValue(ListRowProperty))
            {
                if (grid.Children.Count != 2 || grid.Children[0] is not TextBlock listMarker ||
                    grid.Children[1] is not FrameworkElement body)
                    throw new InvalidOperationException("The Markdown list selection row changed shape.");
                Walk(listMarker, separator, marker: true);
                Walk(body, string.Empty);
            }
            else if (element is Grid table && table.RowDefinitions.Count > 1 &&
                table.Children.Count > 0 && table.Children.All(child => child is RichTextBlock))
            {
                var row = -1;
                foreach (var cell in table.Children.OfType<FrameworkElement>()
                    .OrderBy(Grid.GetRow).ThenBy(Grid.GetColumn))
                {
                    var nextRow = Grid.GetRow(cell);
                    Walk(cell, row < 0 ? separator : row == nextRow ? "\t" : "\n");
                    row = nextRow;
                }
            }
            else
            {
                IEnumerable<FrameworkElement> children = element switch
                {
                    Panel panel => panel.Children.OfType<FrameworkElement>(),
                    Border { Child: FrameworkElement child } => [child],
                    ScrollViewer { Content: FrameworkElement child } => [child],
                    ContentControl { Content: FrameworkElement child } => [child],
                    Viewbox { Child: FrameworkElement child } => [child],
                    _ => [],
                };
                var first = true;
                var list = element is Panel listPanel &&
                    listPanel.Children.OfType<FrameworkElement>().Any(child => (bool)child.GetValue(ListRowProperty));
                foreach (var child in children)
                {
                    Walk(child, first ? separator : list ? "\n" : "\n\n");
                    if (length != begin)
                        first = false;
                }
            }
            _elementRanges[element] = (begin, length);
        }
    }

    internal void SelectRange(int start, int end)
    {
        Document.Select(start, end);
        var changed = _announcedAnchor != Document.Anchor || _announcedFocus != Document.Focus;
        _announcedAnchor = Document.Anchor;
        _announcedFocus = Document.Focus;
        foreach (var surface in _ordered)
            surface.Apply(Document.Start, Document.End, _selectionBackground, _selectionForeground);
        AutomationProperties.SetItemStatus(this, string.Empty);
        UpdateCaret();
        if (changed)
            _peer?.NotifySelectionChanged();
    }

    internal bool BeginSelection(Point point, bool extend = false)
    {
        RefreshDocument();
        var offset = OffsetFromPoint(point);
        if (offset is null)
            return false;
        Focus(FocusState.Pointer);
        SelectRange(extend ? Document.Anchor : offset.Value, offset.Value);
        RememberPointer(point);
        _dragging = true;
        CompositionTarget.Rendering -= OnSelectionFrame;
        CompositionTarget.Rendering += OnSelectionFrame;
        return true;
    }

    internal void ExtendSelection(Point point)
    {
        RememberPointer(point);
        if (_dragging && OffsetFromPoint(point) is { } offset)
            SelectRange(Document.Anchor, offset);
    }

    internal void EndSelection()
    {
        _pressedDisclosure = null;
        _dragging = false;
        CompositionTarget.Rendering -= OnSelectionFrame;
    }

    private void RememberPointer(Point point) =>
        _pointerPosition = XamlRoot?.Content is UIElement root
            ? TransformToVisual(root).TransformPoint(point) : point;

    private void OnSelectionFrame(object? sender, object args)
    {
        if (!_dragging || _disposed || XamlRoot?.Content is not FrameworkElement viewport)
            return;
        var bounds = viewport.TransformToVisual(this)
            .TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
        var point = viewport.TransformToVisual(this).TransformPoint(_pointerPosition);
        var scrollParents = new List<ScrollViewer>();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this);
            parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll)
            {
                scrollParents.Add(scroll);
                bounds = Intersection(bounds, scroll.TransformToVisual(this)
                    .TransformBounds(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight)));
            }
        if (bounds.IsEmpty)
            return;
        var direction = point.Y < bounds.Top ? -1 : point.Y > bounds.Bottom ? 1 : 0;
        if (direction == 0)
            return;
        foreach (var scroll in scrollParents)
        {
            var next = Math.Clamp(scroll.VerticalOffset + direction * 24, 0, scroll.ScrollableHeight);
            if (next != scroll.VerticalOffset)
            {
                scroll.ChangeView(null, next, null, disableAnimation: true);
                break;
            }
        }
        var edge = new Point(Math.Clamp(point.X, bounds.Left, bounds.Right),
            Math.Clamp(point.Y, bounds.Top + 0.1, bounds.Bottom - 0.1));
        if (OffsetFromPoint(edge) is { } offset)
            SelectRange(Document.Anchor, offset);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_dragging && args.Pointer.PointerId == _pointerId)
            return;
        var point = args.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || IsInteractive(args.OriginalSource as DependencyObject))
            return;
        if (!BeginSelection(point.Position, Down(VirtualKey.Shift)))
            return;
        _pointerId = args.Pointer.PointerId;
        CapturePointer(args.Pointer);
        args.Handled = true;
    }

    internal static void ConfigureDisclosure(Expander disclosure, string title)
    {
        if (disclosure.Header is TextBlock existing)
        {
            existing.Text = title;
            return;
        }
        var summary = new TextBlock
        {
            Text = title,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        // Handle the title before the template's ToggleButton captures the pointer.
        // The chevron, keyboard and ExpandCollapse provider keep native behavior.
        summary.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            NativeChatMessageText? owner = null;
            for (DependencyObject? parent = disclosure; parent is not null;
                parent = VisualTreeHelper.GetParent(parent))
                if (parent is NativeChatMessageText candidate)
                {
                    owner = candidate;
                    break;
                }
            if (owner is null || !args.GetCurrentPoint(owner).Properties.IsLeftButtonPressed)
                return;
            var point = args.GetCurrentPoint(owner).Position;
            var shift = Down(VirtualKey.Shift);
            if (!owner.BeginSelection(point, shift))
                return;
            owner._pressedDisclosure = shift ? null : disclosure;
            owner._summaryPressPoint = args.GetCurrentPoint(null).Position;
            owner._summaryDragged = false;
            owner._pointerId = args.Pointer.PointerId;
            owner.CapturePointer(args.Pointer);
            args.Handled = true;
        }), handledEventsToo: true);
        disclosure.Header = summary;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging || args.Pointer.PointerId != _pointerId)
            return;
        UpdateSummaryDrag(args);
        ExtendSelection(args.GetCurrentPoint(this).Position);
        args.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging || args.Pointer.PointerId != _pointerId)
            return;
        UpdateSummaryDrag(args);
        ExtendSelection(args.GetCurrentPoint(this).Position);
        var toggle = !_summaryDragged ? _pressedDisclosure : null;
        EndSelection();
        ReleasePointerCapture(args.Pointer);
        if (toggle is not null)
        {
            toggle.IsExpanded = !toggle.IsExpanded;
            for (var parent = VisualTreeHelper.GetParent((DependencyObject)toggle.Header);
                parent is not null && parent != toggle; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ButtonBase button)
                {
                    button.Focus(FocusState.Pointer);
                    break;
                }
        }
        args.Handled = true;
    }

    private void UpdateSummaryDrag(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(null).Position;
        if (_pressedDisclosure is not null &&
            (Math.Abs(point.X - _summaryPressPoint.X) > 4 || Math.Abs(point.Y - _summaryPressPoint.Y) > 4))
            _summaryDragged = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, this) && args.Pointer.PointerId == _pointerId)
            EndSelection();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (IsInteractive(args.OriginalSource as DependencyObject) ||
            OffsetFromPoint(args.GetPosition(this)) is not { } offset)
            return;
        Document.SelectWord(offset);
        SelectRange(Document.Anchor, Document.Focus);
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!IsInteractive(args.OriginalSource as DependencyObject) &&
            ProcessKey(args.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift)))
            args.Handled = true;
    }

    internal bool ProcessKey(VirtualKey key, bool control, bool shift)
    {
        RefreshDocument();
        if (control && key == VirtualKey.C)
            return CopySelection();
        if (control && key == VirtualKey.A)
        {
            SelectRange(0, Document.Text.Length);
            return true;
        }
        switch (key)
        {
            case VirtualKey.Left:
            case VirtualKey.Right:
                Document.MoveHorizontal(key == VirtualKey.Left ? -1 : 1, control, shift);
                break;
            case VirtualKey.Home:
            case VirtualKey.End:
                Document.MoveTo(control ? key == VirtualKey.Home ? 0 : Document.Text.Length :
                    VisualLineEnd(key == VirtualKey.Home), shift);
                break;
            case VirtualKey.Up:
            case VirtualKey.Down:
            case VirtualKey.PageUp:
            case VirtualKey.PageDown:
                var caret = CaretRectangle(Document.Focus);
                var distance = key is VirtualKey.PageUp or VirtualKey.PageDown
                    ? Math.Max(1, XamlRoot?.Size.Height ?? ActualHeight) : Math.Max(1, caret.Height);
                var direction = key is VirtualKey.Up or VirtualKey.PageUp ? -1 : 1;
                var offset = OffsetFromPoint(new Point(caret.X, caret.Y + caret.Height / 2 + direction * distance));
                if (offset is { } position)
                    Document.MoveTo(position, shift);
                break;
            case VirtualKey.Escape:
                Document.MoveTo(Document.Focus, extend: false);
                break;
            default:
                return false;
        }
        SelectRange(Document.Anchor, Document.Focus);
        ScrollRangeIntoView(Document.Focus, Document.Focus, alignToTop: false);
        return true;
    }

    internal bool CopySelection()
    {
        if (!Document.HasSelection)
            return false;
        var success = _tryCopy?.Invoke(Document.SelectedText) ??
            ClipboardHelper.TryCopyText(Document.SelectedText, flush: true);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetItemStatus(this, success
            ? ChatMarkdownPresentation.Localized("Chat_Copy_Copied", "Copied")
            : ChatMarkdownPresentation.Localized("Chat_Copy_Failed", "Copy failed"));
        _peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return true;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (!Document.HasSelection || IsInteractive(args.OriginalSource as DependencyObject))
            return;
        var copy = new MenuFlyoutItem
        {
            Text = ChatMarkdownPresentation.Localized("Chat_Assistant_Action_Copy", "Copy"),
        };
        copy.Click += (_, _) => CopySelection();
        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        menu.ShowAt(this, args.GetPosition(this));
        args.Handled = true;
    }

    private int VisualLineEnd(bool beginning)
    {
        var surface = _ordered.LastOrDefault(item => Document.Focus >= item.Start);
        if (surface is null)
            return Document.Focus;
        var caret = CaretRectangle(Document.Focus);
        var position = Math.Clamp(Document.Focus, surface.Start, surface.End);
        while (beginning ? position > surface.Start : position < surface.End)
        {
            var next = Document.CharacterBoundary(position, beginning ? -1 : 1);
            var character = beginning ? next : position;
            if (Document.Text[character] is '\r' or '\n' or '\u2028' or '\u2029')
                break;
            var rectangle = CaretRectangle(character);
            if (rectangle.IsEmpty || Math.Abs(rectangle.Y - caret.Y) >= Math.Max(1, caret.Height / 2))
                break;
            position = next;
        }
        return position;
    }

    internal int? OffsetFromPoint(Point point)
    {
        ChatTextSurface? nearest = null;
        var best = double.PositiveInfinity;
        foreach (var surface in _ordered)
        {
            var bounds = VisibleBounds(surface);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                continue;
            var x = Math.Clamp(point.X, bounds.Left, bounds.Right);
            var y = Math.Clamp(point.Y, bounds.Top, bounds.Bottom);
            var score = Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2);
            if (score < best)
            {
                nearest = surface;
                best = score;
            }
        }
        if (nearest is null)
            return null;
        var local = TransformToVisual(nearest.Element).TransformPoint(point);
        return nearest.Start + nearest.OffsetFromPoint(local);
    }

    private Rect CaretRectangle(int offset)
    {
        var surface = _ordered.LastOrDefault(item => offset >= item.Start)
            ?? _ordered.FirstOrDefault();
        if (surface is null)
            return Rect.Empty;
        var rectangle = surface.CharacterRectangle(offset - surface.Start);
        return rectangle.IsEmpty ? Rect.Empty : surface.Element.TransformToVisual(this).TransformBounds(rectangle);
    }

    private void UpdateCaret()
    {
        if (_caret is null || _caretLayer is null)
            return;
        if (FocusState == FocusState.Unfocused || Document.HasSelection)
        {
            _caret.Visibility = Visibility.Collapsed;
            return;
        }
        var rectangle = CaretRectangle(Document.Focus);
        if (rectangle.IsEmpty || rectangle.Height <= 0)
        {
            _caret.Visibility = Visibility.Collapsed;
            return;
        }
        _caret.Visibility = FocusState != FocusState.Unfocused && !Document.HasSelection &&
            !rectangle.IsEmpty && rectangle.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
        _caret.Background = Foreground;
        _caret.Height = Math.Max(1, rectangle.Height);
        Canvas.SetLeft(_caret, rectangle.X);
        Canvas.SetTop(_caret, rectangle.Y);
    }

    internal int OffsetFromScreenPoint(Point point)
    {
        var bounds = (_peer ??= new(this)).GetBoundingRectangle();
        var scale = XamlRoot?.RasterizationScale ?? 1;
        return OffsetFromPoint(new Point((point.X - bounds.X) / scale,
            (point.Y - bounds.Y) / scale)) ?? 0;
    }

    internal double[] ScreenRectangles(int start, int end)
    {
        var rectangles = new List<Rect>();
        foreach (var surface in _ordered)
        {
            var visible = VisibleBounds(surface);
            Rect line = Rect.Empty;
            for (var position = Math.Max(start, surface.Start); position < Math.Min(end, surface.End); position++)
            {
                var local = surface.GlyphRectangle(position - surface.Start);
                if (local.IsEmpty)
                    continue;
                var character = surface.Element.TransformToVisual(this).TransformBounds(local);
                character.Width = Math.Max(1, character.Width);
                character = Intersection(character, visible);
                if (character.IsEmpty)
                    continue;
                if (!line.IsEmpty && Math.Abs(line.Y - character.Y) < 0.5)
                    line.Union(character);
                else
                {
                    if (!line.IsEmpty)
                        rectangles.Add(line);
                    line = character;
                }
            }
            if (!line.IsEmpty)
                rectangles.Add(line);
        }
        var origin = (_peer ??= new(this)).GetBoundingRectangle();
        var scale = XamlRoot?.RasterizationScale ?? 1;
        return rectangles.SelectMany(rectangle => new[]
        {
            origin.X + rectangle.X * scale, origin.Y + rectangle.Y * scale,
            rectangle.Width * scale, rectangle.Height * scale,
        }).ToArray();
    }

    internal IReadOnlyList<(int Start, int End)> VisibleTextRanges()
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var surface in _ordered)
        {
            var bounds = VisibleBounds(surface);
            if (bounds.IsEmpty)
                continue;
            var transform = TransformToVisual(surface.Element);
            var first = surface.OffsetFromPoint(transform.TransformPoint(new Point(bounds.Left, bounds.Top + 0.1)));
            var last = surface.OffsetFromPoint(transform.TransformPoint(new Point(bounds.Right, bounds.Bottom - 0.1)));
            ranges.Add((surface.Start + Math.Min(first, last), surface.Start + Math.Max(first, last)));
        }
        return ranges;
    }

    internal int[] ParagraphBoundaries()
    {
        var boundaries = new SortedSet<int> { 0, Document.Text.Length };
        foreach (var surface in _ordered)
        {
            foreach (var position in surface.ParagraphStarts)
            {
                // A list marker/body pair or adjacent table columns share a paragraph.
                if (position > 0 || surface.Start == 0 || surface.Separator.Contains('\n'))
                    boundaries.Add(surface.Start + position);
            }
        }
        return boundaries.ToArray();
    }

    internal int[]? VisualLineBoundaries()
    {
        var boundaries = new SortedSet<int> { 0, Document.Text.Length };
        double? previousY = null;
        foreach (var surface in _ordered)
        {
            foreach (var position in StringInfo.ParseCombiningCharacters(surface.Text))
            {
                if (surface.Text[position] is '\r' or '\n' or '\u2028' or '\u2029')
                    continue;
                var rectangle = surface.CharacterRectangle(position);
                if (rectangle.IsEmpty || rectangle.Height <= 0)
                    return null;
                rectangle = surface.Element.TransformToVisual(this).TransformBounds(rectangle);
                if (previousY is { } y && Math.Abs(y - rectangle.Y) > 0.5)
                    boundaries.Add(surface.Start + position);
                previousY = rectangle.Y;
            }
        }
        return boundaries.ToArray();
    }

    internal (int Start, int End)? RangeForElement(FrameworkElement element) =>
        _elementRanges.TryGetValue(element, out var range) ? range : null;

    internal void ScrollRangeIntoView(int start, int end, bool alignToTop)
    {
        var offset = alignToTop ? start : end;
        var surface = _ordered.LastOrDefault(item => offset >= item.Start)
            ?? _ordered.FirstOrDefault();
        if (surface is null)
            return;
        var rectangle = surface.CharacterRectangle(offset - surface.Start);
        if (rectangle.IsEmpty)
            return;
        surface.Element.StartBringIntoView(new BringIntoViewOptions
        {
            TargetRect = rectangle,
            VerticalAlignmentRatio = alignToTop ? 0 : 1,
            AnimationDesired = false,
        });
    }

    private Rect VisibleBounds(ChatTextSurface surface)
    {
        var bounds = surface.Element.TransformToVisual(this)
            .TransformBounds(new Rect(0, 0, surface.Element.ActualWidth, surface.Element.ActualHeight));
        for (DependencyObject? parent = VisualTreeHelper.GetParent(surface.Element);
            parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroll)
                bounds = Intersection(bounds, scroll.TransformToVisual(this)
                    .TransformBounds(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight)));
        }
        return Intersection(bounds, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private static Rect Intersection(Rect first, Rect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : Rect.Empty;
    }

    private bool ContainsElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element == this)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private bool IsInteractive(DependencyObject? element)
    {
        while (element is not null && element != this)
        {
            if (element is TextBlock summary && _surfaces.ContainsKey(summary))
                return false;
            if (element is ButtonBase or ScrollBar or Thumb or TextBox or PasswordBox or RichEditBox)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer ??= new(this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Loaded -= OnLoaded;
        Suspend();
    }

    private void Suspend()
    {
        if (_suspended)
            return;
        _suspended = true;
        _generation++;
        _refreshPending = false;
        EndSelection();
        ReleasePointerCaptures();
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        RemoveHandler(PointerPressedEvent, _pointerPressedHandler);
        PointerMoved -= OnPointerMoved;
        PointerReleased -= OnPointerReleased;
        PointerCaptureLost -= OnPointerCaptureLost;
        DoubleTapped -= OnDoubleTapped;
        RemoveHandler(KeyDownEvent, _keyDownHandler);
        GotFocus -= OnGotFocus;
        LostFocus -= OnLostFocus;
        RightTapped -= OnRightTapped;
        _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
        foreach (var surface in _surfaces.Values)
            surface.Restore();
        _surfaces.Clear();
        _ordered.Clear();
        _elementRanges.Clear();
        Document.Select(0, 0);
        Document.Update([]);
    }
}
