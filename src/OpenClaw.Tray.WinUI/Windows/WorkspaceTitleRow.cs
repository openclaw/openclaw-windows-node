// Joins the chat side panel's header to the Workspace window's caption row, as in the web
// Control UI: places the title-bar actions flush against the caption buttons, offers the row
// to the panel, and keeps the panel's tabs clickable under the title bar. WorkspaceWindow owns
// one instance and calls Update from a coalesced low-priority pass:
//   _titleRow = new WorkspaceTitleRow(AppWindow, WorkspaceTitleBar, TitleBarRightActions,
//       TitleBarPassthroughCanvas, SidePanelTabsPassthrough, QueueTitleRowLayout);
//   _titleRow.Update(panel, offer: chatShown && !bannerAboveChat, startInset);
//   _titleRow.Dispose(); // on window close
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Chat;
using Windows.Foundation;

namespace OpenClawTray.Windows;

/// <summary>Caption-row geometry and pointer pass-through for the chat side panel's header.</summary>
internal sealed class WorkspaceTitleRow : IDisposable
{
    // Space kept between the side panel's tabs and the title-bar actions.
    private const double ActionGap = 8;
    // The TabView template part that hosts the tab items.
    private const string TabListPartName = "TabListView";
    private readonly AppWindow _window;
    private readonly TitleBar _titleBar;
    private readonly FrameworkElement _actions;
    private readonly Canvas _passthroughCanvas;
    private readonly FrameworkElement _tabsPassthrough;
    private readonly Action _invalidate;
    private FrameworkElement? _tabStrip;
    private FrameworkElement? _tabList;
    private Rect? _tabBounds;
    private XamlRoot? _root;

    internal WorkspaceTitleRow(AppWindow window, TitleBar titleBar, FrameworkElement actions,
        Canvas passthroughCanvas, FrameworkElement tabsPassthrough, Action invalidate)
    {
        _window = window;
        _titleBar = titleBar;
        _actions = actions;
        _passthroughCanvas = passthroughCanvas;
        _tabsPassthrough = tabsPassthrough;
        _invalidate = invalidate;
    }

    /// <summary>
    /// Applies the placement for the current layout. Withdraw the row (<paramref name="offer"/> false)
    /// while chat is hidden or content such as a banner sits above it, since a joined panel would cover it.
    /// TitleBar refreshes its pass-through regions only on its own size or template changes, so this
    /// recomputes them after every placement change.
    /// </summary>
    internal void Update(ChatConversationPanelState? panel, bool offer, double startInset)
    {
        if (_titleBar.XamlRoot is not { RasterizationScale: > 0 } root) return;
        WatchScale(root);
        var scale = root.RasterizationScale;
        var captionWidth = _window.TitleBar.RightInset / scale;
        var captionHeight = _window.TitleBar.Height / scale;
        if (captionHeight > 0) _actions.Height = captionHeight;
        // The template reserves a minimum drag column plus caption padding sized from the physical
        // RightInset as if it were DIPs, which exceeds the real buttons above 100% scale. Extend the
        // bar past the window edge by the excess so the actions are laid out beside minimize; TitleBar
        // derives pointer pass-through from layout bounds, so a render transform would not be clickable.
        if (VisualTreeHelper.GetChildrenCount(_titleBar) > 0 &&
            VisualTreeHelper.GetChild(_titleBar, 0) is Grid { ColumnDefinitions.Count: >= 2 } layout)
        {
            var columns = layout.ColumnDefinitions;
            var excess = Math.Max(0, columns[^1].ActualWidth + columns[^2].ActualWidth - captionWidth);
            if (Math.Abs(_titleBar.Margin.Right + excess) >= 0.5)
                _titleBar.Margin = new Thickness(0, 0, -excess, 0);
        }
        // The joined header covers the whole bar height so no strip of panel body falls in the drag region.
        panel?.SetTitleRow(offer ? _titleBar.ActualHeight : 0,
            _actions.ActualWidth + captionWidth + ActionGap, startInset, captionHeight);
        Track(offer && panel is { JoinsTitleRow: true } ? panel.TabStrip as FrameworkElement : null);
        PlaceTabsPassthrough();
        _titleBar.RecomputeDragRegions();
    }

    // Pass-through rectangles are physical pixels; a monitor scale change keeps DIP bounds but moves them.
    private void WatchScale(XamlRoot root)
    {
        if (ReferenceEquals(root, _root)) return;
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = root;
        root.Changed += OnRootChanged;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => _invalidate();

    private void Track(FrameworkElement? tabStrip)
    {
        if (ReferenceEquals(tabStrip, _tabStrip)) return;
        if (_tabStrip is not null) _tabStrip.LayoutUpdated -= OnTabStripLayoutUpdated;
        _tabStrip = tabStrip;
        _tabList = null;
        if (tabStrip is not null) tabStrip.LayoutUpdated += OnTabStripLayoutUpdated;
    }

    private void PlaceTabsPassthrough()
    {
        _tabBounds = TabBounds();
        if (_tabBounds is not { } bounds)
        {
            _tabsPassthrough.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(_tabsPassthrough, bounds.X);
        Canvas.SetTop(_tabsPassthrough, bounds.Y);
        _tabsPassthrough.Width = bounds.Width;
        _tabsPassthrough.Height = bounds.Height;
        _tabsPassthrough.Visibility = Visibility.Visible;
    }

    /// <summary>Bounds of the tab items in pass-through canvas coordinates, once the template part is ready.</summary>
    private Rect? TabBounds()
    {
        if (_tabStrip is not { IsLoaded: true } strip) return null;
        if (_tabList is not { IsLoaded: true }) _tabList = FindPart(strip);
        if (_tabList is not { ActualWidth: > 0, ActualHeight: > 0 } tabs) return null;
        try
        {
            var origin = tabs.TransformToVisual(_passthroughCanvas).TransformPoint(default);
            return new Rect(origin.X, origin.Y, tabs.ActualWidth, tabs.ActualHeight);
        }
        catch (ArgumentException) { return null; }
    }

    // Tabs move without resizing when the panel or sidebar slides, and the template part can load a
    // pass after the strip; re-place whenever the bounds appear, change or disappear.
    private void OnTabStripLayoutUpdated(object? sender, object e)
    {
        var bounds = TabBounds();
        if (bounds is null && _tabBounds is null) return;
        if (bounds is { } next && _tabBounds is { } previous &&
            Math.Abs(next.X - previous.X) < 0.5 && Math.Abs(next.Y - previous.Y) < 0.5 &&
            Math.Abs(next.Width - previous.Width) < 0.5 && Math.Abs(next.Height - previous.Height) < 0.5)
            return;
        _invalidate();
    }

    private static FrameworkElement? FindPart(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement { Name: TabListPartName } part) return part;
            if (FindPart(child) is { } found) return found;
        }
        return null;
    }

    public void Dispose()
    {
        Track(null);
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = null;
    }
}
