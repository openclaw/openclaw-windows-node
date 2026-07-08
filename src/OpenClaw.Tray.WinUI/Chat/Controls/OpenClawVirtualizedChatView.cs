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
public sealed class OpenClawVirtualizedChatView : ContentControl, IDisposable
{
    private const double FollowThreshold = 60;
    private const int MaxSessionOffsets = 50;
    private const int RequiredStableRestorePasses = 2;
    private static readonly Dictionary<string, double> s_sessionOffsets = new(StringComparer.Ordinal);

    private readonly ScrollViewer _scrollViewer;
    private readonly ItemsRepeater _itemsRepeater;
    private readonly Button _scrollToLatestButton;
    private readonly ChatRowElementFactory _rowFactory;
    private readonly ObservableCollection<OpenClawChatTimelineRow> _rows = new();

    private OpenClawChatTimelineView _view = OpenClawChatTimelineView.Empty;
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
    private int _previousScrollToBottomToken;
    private int _previousSuppressAutoFollowToken;
    private int _loadMoreRequestedForCount = -1;
    private bool _disposed;

    public OpenClawVirtualizedChatView()
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

    public void Update(OpenClawChatTimelineView view)
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
            && view.EntryIds.Contains(previousFirstEntryId);
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
            StoreSessionOffset(previousSessionId, _lastVerticalOffset);
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
        _suppressAutoFollow = false;
        QueueScrollToBottom(_view.SessionId, disableAnimation: false);
    }

    private void SyncRows(IReadOnlyList<OpenClawChatTimelineRow> desiredRows)
    {
        StableRowCollection.Sync(_rows, desiredRows, row => row.Key);
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

        if (!_suppressAutoFollow && _isFollowing)
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
        var movedUp = _scrollViewer.VerticalOffset < _lastVerticalOffset - 0.5;

        if (IsAtBottom)
        {
            _isFollowing = true;
            _scrollToLatestButton.Visibility = Visibility.Collapsed;
        }
        else if (movedUp || !_isFollowing)
        {
            _isFollowing = false;
            _scrollToLatestButton.Visibility = Visibility.Visible;
        }

        _lastVerticalOffset = _scrollViewer.VerticalOffset;
        _lastScrollableHeight = _scrollViewer.ScrollableHeight;
        StoreSessionOffset(_view.SessionId, _scrollViewer.VerticalOffset);

        if (_scrollViewer.ScrollableHeight > 0
            && _scrollViewer.VerticalOffset <= FollowThreshold
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
        else
        {
            EnqueueOnView(() => { _ = ApplyPendingRestoreIfReady(); });
        }

        return true;
    }

    private void QueueScrollToBottom(string? sessionId, bool disableAnimation)
    {
        _isFollowing = true;
        _scrollToLatestButton.Visibility = Visibility.Collapsed;
        EnqueueOnView(() =>
        {
            var bottom = _scrollViewer.ScrollableHeight;
            _scrollViewer.ChangeView(null, bottom, null, disableAnimation);
            _lastVerticalOffset = bottom;
            _lastScrollableHeight = _scrollViewer.ScrollableHeight;
            _isFollowing = true;
            StoreSessionOffset(sessionId, bottom);
        });
    }

    private void QueuePreservePrependOffset(string? sessionId, double oldOffset, double oldScrollableHeight)
    {
        _suppressAutoFollow = true;
        EnqueueOnView(() =>
        {
            var delta = _scrollViewer.ScrollableHeight - oldScrollableHeight;
            var target = ClampOffset(oldOffset + delta, _scrollViewer.ScrollableHeight);
            _scrollViewer.ChangeView(null, target, null, disableAnimation: true);
            _lastVerticalOffset = target;
            _lastScrollableHeight = _scrollViewer.ScrollableHeight;
            _isFollowing = _scrollViewer.ScrollableHeight - target <= FollowThreshold;
            _scrollToLatestButton.Visibility = _isFollowing ? Visibility.Collapsed : Visibility.Visible;
            StoreSessionOffset(sessionId, target);
            EnqueueOnView(() => _suppressAutoFollow = false);
        });
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

    private static double ClampOffset(double offset, double max) =>
        Math.Max(0, Math.Min(offset, max));

    private static double? TryGetSessionOffset(string? sessionId)
    {
        if (sessionId is not { Length: > 0 })
            return null;

        return s_sessionOffsets.TryGetValue(sessionId, out var offset) ? offset : null;
    }

    private static void StoreSessionOffset(string? sessionId, double offset)
    {
        if (sessionId is not { Length: > 0 })
            return;

        s_sessionOffsets[sessionId] = offset;
        if (s_sessionOffsets.Count <= MaxSessionOffsets)
            return;

        var first = s_sessionOffsets.Keys.First();
        s_sessionOffsets.Remove(first);
    }

    private sealed class ChatRowElementFactory : IElementFactory, IDisposable
    {
        private readonly Dictionary<string, OpenClawChatTimelineRow> _rows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FunctionalHostControl> _hosts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _rowHeights = new(StringComparer.Ordinal);

        public void Update(IReadOnlyList<OpenClawChatTimelineRow> rows)
        {
            _rows.Clear();
            foreach (var row in rows)
                _rows[row.Key] = row;

            foreach (var staleKey in _rowHeights.Keys.Where(key => !_rows.ContainsKey(key)).ToArray())
                _rowHeights.Remove(staleKey);

            RefreshRealizedRows();
        }

        private void RefreshRealizedRows()
        {
            foreach (var (key, host) in _hosts)
            {
                if (host.Parent is not null && _rows.TryGetValue(key, out var row))
                    Mount(host, row);
            }
        }

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is not OpenClawChatTimelineRow row)
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
                if (_rowHeights.TryGetValue(row.Key, out var cachedHeight))
                    host.MinHeight = cachedHeight;
                else if (row.EstimatedHeight > 0)
                    host.MinHeight = row.EstimatedHeight;

                host.SizeChanged += (_, e) =>
                {
                    if (e.NewSize.Height <= 0)
                        return;

                    _rowHeights[row.Key] = e.NewSize.Height;
                    if (host.MinHeight > 0)
                        host.MinHeight = 0;
                };
                _hosts[row.Key] = host;
            }

            Mount(host, row);
            return host;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is not FunctionalHostControl host)
                return;

            var key = _hosts.FirstOrDefault(kvp => ReferenceEquals(kvp.Value, host)).Key;
            if (key is null)
                return;

            host.Dispose();
            _hosts.Remove(key);
        }

        public void Dispose()
        {
            foreach (var host in _hosts.Values)
                host.Dispose();

            _hosts.Clear();
            _rows.Clear();
        }

        private static void Mount(FunctionalHostControl host, OpenClawChatTimelineRow row)
        {
            host.Mount(_ => row.Render());
        }
    }
}
