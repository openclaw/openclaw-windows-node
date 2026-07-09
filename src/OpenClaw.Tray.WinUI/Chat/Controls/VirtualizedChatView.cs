using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI.Hosting;
using OpenClawTray.Helpers;
using System.Collections.ObjectModel;

namespace OpenClawTray.Chat.Controls;

/// <summary>
/// Native WinUI timeline host for OpenClaw chat rows. It owns realization,
/// follow-scroll, load-earlier, per-session offset restore, and the floating
/// scroll-to-latest affordance while row UI remains supplied by FunctionalUI.
/// </summary>
public sealed class VirtualizedChatView : ContentControl, IDisposable
{
    private const double FollowThreshold = 60;
    private const int MaxSessionOffsets = 50;
    private const int RequiredStableRestorePasses = 2;
    private static readonly Dictionary<string, double> s_sessionOffsets = new(StringComparer.Ordinal);
    private static readonly object s_sessionOffsetsLock = new();

    private readonly ScrollViewer _scrollViewer;
    private readonly ItemsRepeater _itemsRepeater;
    private readonly Button _scrollToLatestButton;
    private readonly ChatRowElementFactory _rowFactory;
    private readonly ObservableCollection<ChatTimelineRow> _rows = new();
    private readonly HashSet<string> _rowKeyScratch = new(StringComparer.Ordinal);

    private ChatTimelineView _view = ChatTimelineView.Empty;
    private string? _previousSessionId;
    private int _previousEntryCount;
    private string? _previousFirstEntryId;
    private string? _previousLastEntryId;
    private double _lastVerticalOffset;
    private double _lastScrollableHeight;
    private bool _isFollowing = true;
    private bool _suppressAutoFollow;
    private double? _pendingRestoreOffset;
    private double? _lastRestoreScrollableHeight;
    private int _stableRestorePasses;
    private string? _pendingPrependSessionId;
    private double? _pendingPrependOldOffset;
    private double? _pendingPrependOldScrollableHeight;
    private double? _lastPrependScrollableHeight;
    private int _stablePrependPasses;
    private int _previousScrollToBottomToken;
    private int _previousSuppressAutoFollowToken;
    private int _loadMoreRequestedForCount = -1;
    private ScrollToEndState _scrollToEndState;
    private bool _scrollToEndPending;
    private string? _scrollToEndSessionId;
    private bool _scrollToEndDisableAnimation;
    private bool _disposed;

    private enum ScrollToEndState
    {
        Idle,
        Queued,
        BringingIntoView,
    }

    public VirtualizedChatView()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _rowFactory = new ChatRowElementFactory();
        _itemsRepeater = new ItemsRepeater
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Layout = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
            },
            ItemsSource = _rows,
            ItemTemplate = _rowFactory,
        };
        _itemsRepeater.SizeChanged += OnContentSizeChanged;

        _scrollViewer = new ScrollViewer
        {
            Content = _itemsRepeater,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _scrollViewer.ViewChanged += OnViewChanged;
        _scrollViewer.SizeChanged += OnContentSizeChanged;

        _scrollToLatestButton = new Button
        {
            Width = 40,
            Height = 40,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = Visibility.Collapsed,
            Content = new FontIcon
            {
                Glyph = "\uE70D",
                FontSize = 16,
                FontFamily = FluentIconCatalog.SymbolThemeFontFamily,
            },
        };
        AutomationProperties.SetName(
            _scrollToLatestButton,
            LocalizationHelper.GetString("Chat_Timeline_ScrollToLatest"));
        _scrollToLatestButton.Click += (_, _) => ScrollToEnd();

        var root = new Grid();
        root.Children.Add(_scrollViewer);
        root.Children.Add(_scrollToLatestButton);
        Content = root;
    }

    public void Update(ChatTimelineView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var previousSessionId = _previousSessionId;
        var previousEntryCount = _previousEntryCount;
        var previousFirstEntryId = _previousFirstEntryId;
        var previousLastEntryId = _previousLastEntryId;
        var sessionChanged = !string.Equals(view.SessionId, previousSessionId, StringComparison.Ordinal);
        var isFirstMount = sessionChanged && previousSessionId is null;
        var initialLoad = isFirstMount
            ? view.EntryCount > 0
            : (!sessionChanged && previousEntryCount == 0 && view.EntryCount > 0);
        var prependedHistory = !sessionChanged
            && previousEntryCount > 0
            && view.EntryCount > previousEntryCount
            && previousFirstEntryId is not null
            && !string.Equals(view.FirstEntryId, previousFirstEntryId, StringComparison.Ordinal)
            && string.Equals(view.LastEntryId, previousLastEntryId, StringComparison.Ordinal)
            && view.ContainsEntryId(previousFirstEntryId);
        var appendedEntries = !sessionChanged
            && view.EntryCount > previousEntryCount
            && !prependedHistory;

        _view = view;
        _rowFactory.Update(view.Rows);
        SyncRows(view.Rows);

        if (view.EntryCount != previousEntryCount)
            _loadMoreRequestedForCount = -1;

        if (sessionChanged && !isFirstMount)
        {
            StoreCurrentOffsetForSession(previousSessionId);
            if (view.EntryCount > 0)
                QueueScrollToBottom(view.SessionId, disableAnimation: true);
        }
        else if (prependedHistory)
        {
            QueuePreservePrependOffset(view.SessionId, _lastVerticalOffset, _lastScrollableHeight);
        }
        else if (initialLoad)
        {
            var savedOffset = TryGetSessionOffset(view.SessionId);
            if (savedOffset is not null)
            {
                _pendingRestoreOffset = savedOffset.Value;
                _lastRestoreScrollableHeight = null;
                _stableRestorePasses = 0;
                _suppressAutoFollow = true;
                _isFollowing = false;
                ApplyPendingRestoreIfReady();
            }
            else
            {
                QueueScrollToBottom(view.SessionId, disableAnimation: true);
            }
        }
        else if (appendedEntries && _isFollowing)
        {
            QueueScrollToBottom(view.SessionId, disableAnimation: false);
        }

        if (view.ScrollToBottomToken != _previousScrollToBottomToken)
        {
            _previousScrollToBottomToken = view.ScrollToBottomToken;
            QueueScrollToBottom(view.SessionId, disableAnimation: false);
        }

        if (view.SuppressAutoFollowToken != _previousSuppressAutoFollowToken)
        {
            _previousSuppressAutoFollowToken = view.SuppressAutoFollowToken;
            _suppressAutoFollow = true;
        }

        _previousSessionId = view.SessionId;
        _previousFirstEntryId = view.FirstEntryId;
        _previousLastEntryId = view.LastEntryId;
        _previousEntryCount = view.EntryCount;
    }

    public void ScrollToEnd()
    {
        _pendingRestoreOffset = null;
        ClearPendingPrependCorrection();
        ResetScrollToEndState();
        _suppressAutoFollow = false;
        ClearSessionOffset(_view.SessionId);
        QueueScrollToBottom(_view.SessionId, disableAnimation: false);
    }

    private void SyncRows(IReadOnlyList<ChatTimelineRow> desiredRows)
    {
        StableRowCollection.Sync(_rows, desiredRows, row => row.Key, _rowKeyScratch);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _scrollViewer.ViewChanged -= OnViewChanged;
        _scrollViewer.SizeChanged -= OnContentSizeChanged;
        _itemsRepeater.SizeChanged -= OnContentSizeChanged;
        _itemsRepeater.ItemsSource = null;
        _itemsRepeater.ItemTemplate = null;
        _rowFactory.Dispose();
        Content = null;
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ApplyPendingRestoreIfReady())
            return;

        if (ApplyPendingPrependCorrectionIfReady())
            return;

        if (!_suppressAutoFollow && _isFollowing && _scrollToEndState == ScrollToEndState.Idle)
        {
            QueueScrollToBottom(_view.SessionId, disableAnimation: true);
        }
        else if (_suppressAutoFollow)
        {
            _suppressAutoFollow = false;
        }
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var verticalOffset = _scrollViewer.VerticalOffset;

        if (!e.IsIntermediate && _scrollToEndState == ScrollToEndState.BringingIntoView)
        {
            CompleteScrollToEnd();
        }

        var movedUp = verticalOffset < _lastVerticalOffset - 0.5;

        if (e.IsIntermediate && _scrollToEndState != ScrollToEndState.BringingIntoView)
        {
            CancelPendingCorrectionsForManualScroll();
            ResetScrollToEndState();
            _isFollowing = false;
            _scrollToLatestButton.Visibility = Visibility.Visible;
        }
        else if (_scrollToEndState == ScrollToEndState.Idle && IsAtBottom)
        {
            _isFollowing = true;
            _scrollToLatestButton.Visibility = Visibility.Collapsed;
        }
        else if (_scrollToEndState == ScrollToEndState.Idle && (movedUp || !_isFollowing))
        {
            _isFollowing = false;
            _scrollToLatestButton.Visibility = Visibility.Visible;
        }

        _lastVerticalOffset = verticalOffset;
        _lastScrollableHeight = _scrollViewer.ScrollableHeight;
        StoreCurrentOffsetForSession(_view.SessionId);

        if (_scrollViewer.ScrollableHeight > 0
            && _scrollViewer.VerticalOffset <= FollowThreshold
            && (movedUp || _scrollViewer.VerticalOffset <= 1)
            && _scrollToEndState == ScrollToEndState.Idle
            && _view.HasMoreHistory
            && _loadMoreRequestedForCount != _view.EntryCount)
        {
            _loadMoreRequestedForCount = _view.EntryCount;
            _view.OnLoadMoreHistory?.Invoke();
        }
    }

    private bool IsAtBottom =>
        _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset <= FollowThreshold;

    private bool ApplyPendingRestoreIfReady()
    {
        if (_pendingRestoreOffset is not { } pendingOffset || _scrollViewer.ScrollableHeight <= 0)
            return false;

        var scrollableHeight = _scrollViewer.ScrollableHeight;
        var target = ClampOffset(pendingOffset, scrollableHeight);
        _isFollowing = scrollableHeight - target <= FollowThreshold;
        _scrollToLatestButton.Visibility = _isFollowing ? Visibility.Collapsed : Visibility.Visible;
        _scrollViewer.ChangeView(null, target, null, disableAnimation: true);
        _lastVerticalOffset = target;
        _lastScrollableHeight = scrollableHeight;

        if (_lastRestoreScrollableHeight is { } previousHeight &&
            Math.Abs(previousHeight - scrollableHeight) < 0.5)
        {
            _stableRestorePasses++;
        }
        else
        {
            _stableRestorePasses = 0;
            _lastRestoreScrollableHeight = scrollableHeight;
        }

        if (_stableRestorePasses >= RequiredStableRestorePasses)
        {
            _pendingRestoreOffset = null;
            _lastRestoreScrollableHeight = null;
            _stableRestorePasses = 0;
            _suppressAutoFollow = false;
        }

        return true;
    }

    private void QueueScrollToBottom(string? sessionId, bool disableAnimation)
    {
        _pendingRestoreOffset = null;
        ClearPendingPrependCorrection();
        _isFollowing = true;
        _scrollToLatestButton.Visibility = Visibility.Collapsed;
        if (_scrollToEndState != ScrollToEndState.Idle)
        {
            _scrollToEndPending = true;
            _scrollToEndSessionId = sessionId;
            _scrollToEndDisableAnimation = true;
            QueueStuckBringIntoViewFallback();
            return;
        }

        _scrollToEndPending = false;
        _scrollToEndSessionId = sessionId;
        _scrollToEndDisableAnimation = disableAnimation;
        _scrollToEndState = ScrollToEndState.Queued;
        EnqueueOnView(StartScrollToEndIfFollowing);
    }

    private void StartScrollToEndIfFollowing()
    {
        if (_scrollToEndState != ScrollToEndState.Queued)
            return;

        _scrollToEndState = ScrollToEndState.Idle;
        if (!_isFollowing || _rows.Count == 0)
        {
            _scrollToEndPending = false;
            return;
        }

        var latest = _itemsRepeater.GetOrCreateElement(_rows.Count - 1);
        _scrollToEndState = ScrollToEndState.BringingIntoView;
        latest.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = !_scrollToEndDisableAnimation,
            VerticalAlignmentRatio = 1.0,
        });
        QueueBringIntoViewCompletionFallback();
    }

    private void CompleteScrollToEnd()
    {
        if (_scrollToEndState != ScrollToEndState.BringingIntoView)
            return;

        _scrollToEndState = ScrollToEndState.Idle;
        var bottom = _scrollViewer.ScrollableHeight;
        _scrollViewer.ChangeView(null, bottom, null, _scrollToEndDisableAnimation);
        _lastVerticalOffset = bottom;
        _lastScrollableHeight = _scrollViewer.ScrollableHeight;
        _isFollowing = true;
        ClearSessionOffset(_scrollToEndSessionId);

        if (_scrollToEndPending)
        {
            _scrollToEndPending = false;
            QueueScrollToBottom(_scrollToEndSessionId, disableAnimation: true);
        }
    }

    private void QueueStuckBringIntoViewFallback()
    {
        QueueBringIntoViewCompletionFallback();
    }

    private void QueueBringIntoViewCompletionFallback()
    {
        EnqueueOnView(() =>
        {
            if (_scrollToEndState == ScrollToEndState.BringingIntoView)
                EnqueueOnView(CompleteScrollToEnd);
        });
    }

    private void ResetScrollToEndState()
    {
        _scrollToEndPending = false;
        _scrollToEndState = ScrollToEndState.Idle;
    }

    private void QueuePreservePrependOffset(string? sessionId, double oldOffset, double oldScrollableHeight)
    {
        _pendingPrependSessionId = sessionId;
        _pendingPrependOldOffset = oldOffset;
        _pendingPrependOldScrollableHeight = oldScrollableHeight;
        _lastPrependScrollableHeight = null;
        _stablePrependPasses = 0;
        _suppressAutoFollow = true;
        EnqueueOnView(() => { _ = ApplyPendingPrependCorrectionIfReady(); });
    }

    private bool ApplyPendingPrependCorrectionIfReady()
    {
        if (_pendingPrependOldOffset is not { } oldOffset ||
            _pendingPrependOldScrollableHeight is not { } oldScrollableHeight ||
            _scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        var scrollableHeight = _scrollViewer.ScrollableHeight;
        var delta = scrollableHeight - oldScrollableHeight;
        var target = ClampOffset(oldOffset + delta, scrollableHeight);
        _scrollViewer.ChangeView(null, target, null, disableAnimation: true);
        _lastVerticalOffset = target;
        _lastScrollableHeight = scrollableHeight;
        _isFollowing = scrollableHeight - target <= FollowThreshold;
        _scrollToLatestButton.Visibility = _isFollowing ? Visibility.Collapsed : Visibility.Visible;
        if (_isFollowing)
            ClearSessionOffset(_pendingPrependSessionId);
        else
            StoreSessionOffset(_pendingPrependSessionId, target);

        if (_lastPrependScrollableHeight is { } previousHeight &&
            Math.Abs(previousHeight - scrollableHeight) < 0.5)
        {
            _stablePrependPasses++;
        }
        else
        {
            _stablePrependPasses = 0;
            _lastPrependScrollableHeight = scrollableHeight;
        }

        if (_stablePrependPasses >= RequiredStableRestorePasses)
        {
            ClearPendingPrependCorrection();
            _suppressAutoFollow = false;
        }

        return true;
    }

    private void ClearPendingPrependCorrection()
    {
        _pendingPrependSessionId = null;
        _pendingPrependOldOffset = null;
        _pendingPrependOldScrollableHeight = null;
        _lastPrependScrollableHeight = null;
        _stablePrependPasses = 0;
    }

    private void EnqueueOnView(Action action)
    {
        if (_disposed)
            return;

        var dispatcher = _scrollViewer.DispatcherQueue ?? DispatcherQueue;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
                action();
        });
    }

    private void CancelPendingCorrectionsForManualScroll()
    {
        _pendingRestoreOffset = null;
        _lastRestoreScrollableHeight = null;
        _stableRestorePasses = 0;
        ClearPendingPrependCorrection();
        _suppressAutoFollow = false;
    }

    private static double ClampOffset(double offset, double max) =>
        Math.Max(0, Math.Min(offset, max));

    private static double? TryGetSessionOffset(string? sessionId)
    {
        if (sessionId is not { Length: > 0 })
            return null;

        lock (s_sessionOffsetsLock)
        {
            return s_sessionOffsets.TryGetValue(sessionId, out var offset) ? offset : null;
        }
    }

    private static void StoreSessionOffset(string? sessionId, double offset)
    {
        if (sessionId is not { Length: > 0 })
            return;

        lock (s_sessionOffsetsLock)
        {
            s_sessionOffsets[sessionId] = offset;
            if (s_sessionOffsets.Count <= MaxSessionOffsets)
                return;

            var first = s_sessionOffsets.Keys.First();
            s_sessionOffsets.Remove(first);
        }
    }

    private void StoreCurrentOffsetForSession(string? sessionId)
    {
        if (sessionId is not { Length: > 0 })
            return;

        if (_isFollowing || IsAtBottom)
        {
            ClearSessionOffset(sessionId);
            return;
        }

        StoreSessionOffset(sessionId, _lastVerticalOffset);
    }

    private static void ClearSessionOffset(string? sessionId)
    {
        if (sessionId is not { Length: > 0 })
            return;

        lock (s_sessionOffsetsLock)
        {
            s_sessionOffsets.Remove(sessionId);
        }
    }

    private sealed class ChatRowElementFactory : IElementFactory, IDisposable
    {
        private readonly Dictionary<string, ChatTimelineRow> _rows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FunctionalHostControl> _hosts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _realizedKeys = new(StringComparer.Ordinal);

        public void Update(IReadOnlyList<ChatTimelineRow> rows)
        {
            _rows.Clear();
            foreach (var row in rows)
                _rows[row.Key] = row;

            RefreshRealizedRows();
        }

        private void RefreshRealizedRows()
        {
            foreach (var key in _realizedKeys.ToArray())
            {
                if (_rows.TryGetValue(key, out var row) && _hosts.TryGetValue(key, out var host))
                {
                    Mount(host, row, renderImmediately: false);
                }
                else
                {
                    _realizedKeys.Remove(key);
                }
            }
        }

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is not ChatTimelineRow row)
                return new ContentPresenter();

            if (_rows.TryGetValue(row.Key, out var latestRow))
                row = latestRow;

            if (!_hosts.TryGetValue(row.Key, out var host))
            {
                host = new FunctionalHostControl
                {
                    SuppressAutoDispose = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                };
                _hosts[row.Key] = host;
            }
            else if (host.Parent is not null)
            {
                DetachFromParent(host);
            }

            Mount(host, row, renderImmediately: true);
            _realizedKeys.Add(row.Key);
            return host;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is not FunctionalHostControl host)
                return;

            var key = _hosts.FirstOrDefault(kvp => ReferenceEquals(kvp.Value, host)).Key;
            if (key is null)
                return;

            _realizedKeys.Remove(key);
            host.Dispose();
            _hosts.Remove(key);
        }

        public void Dispose()
        {
            foreach (var host in _hosts.Values)
                host.Dispose();

            _hosts.Clear();
            _rows.Clear();
            _realizedKeys.Clear();
        }

        private static void Mount(FunctionalHostControl host, ChatTimelineRow row, bool renderImmediately)
        {
            host.Mount(_ => row.Render(), preserveRootContext: true, renderImmediately: renderImmediately);
        }

        private static void DetachFromParent(FunctionalHostControl host)
        {
            switch (host.Parent)
            {
                case Panel panel:
                    panel.Children.Remove(host);
                    break;
                case Border border when ReferenceEquals(border.Child, host):
                    border.Child = null;
                    break;
                case ScrollViewer scrollViewer when ReferenceEquals(scrollViewer.Content, host):
                    scrollViewer.Content = null;
                    break;
                case ContentControl contentControl when ReferenceEquals(contentControl.Content, host):
                    contentControl.Content = null;
                    break;
            }
        }
    }
}
