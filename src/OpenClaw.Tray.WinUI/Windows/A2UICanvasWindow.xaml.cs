using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Hosting;
using OpenClawTray.A2UI.Rendering;
using WinUIEx;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenClawTray.Windows;

/// <summary>
/// Native A2UI canvas. Hosts an <see cref="A2UIRouter"/> that drives one or
/// more <see cref="SurfaceHost"/> instances directly into XAML. No WebView2,
/// no HTTP host, no JS bridge.
/// </summary>
public sealed partial class A2UICanvasWindow : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int SW_SHOWNORMAL = 1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly DispatcherQueue _dispatcher;
    private readonly A2UIRouter _router;
    private readonly DataModelStore _dataModel;

    public bool IsClosed { get; private set; }

    /// <summary>
    /// Construct the native A2UI canvas window. <paramref name="actions"/> is
    /// the dispatcher used for any user interactions raised by surface widgets.
    /// </summary>
    public A2UICanvasWindow(IActionSink actions, MediaResolver media, IOpenClawLogger logger)
    {
        InitializeComponent();
        this.SetIcon("Assets\\openclaw.ico");
        // Title is set programmatically (rather than via XAML literal) so we
        // never flash "Canvas" in en-US before the locale's resw value loads.
        Title = OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_CanvasTitle");
        WaitingForContentText.Text = OpenClawTray.Helpers.LocalizationHelper.GetString("Canvas_WaitingForContent");

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _dataModel = new DataModelStore(_dispatcher);
        var registry = ComponentRendererRegistry.BuildDefault(media);
        _router = new A2UIRouter(_dispatcher, _dataModel, registry, actions, logger);

        _router.SurfaceCreated += OnSurfaceCreated;
        _router.SurfaceDeleted += OnSurfaceDeleted;

        // Explicit teardown: unsubscribe router events and reset surfaces so
        // the router's component subscriptions don't outlive the window. The
        // router holds back-references via event delegates; without this, a
        // closed window stays GC-rooted by router state until the next
        // ResetAll, which may never come if the window owned the router.
        Closed += (_, _) =>
        {
            IsClosed = true;
            try { _router.SurfaceCreated -= OnSurfaceCreated; } catch { }
            try { _router.SurfaceDeleted -= OnSurfaceDeleted; } catch { }
            try { _router.ResetAll(); } catch { }
            _surfaceScrollers.Clear();
            _surfaceTabs.Clear();
        };
    }

    /// <summary>
    /// Push a JSONL blob through the router. Safe from any thread — the
    /// router posts UI work to the dispatcher.
    /// </summary>
    public void Push(string jsonl) => _router.Push(jsonl);

    /// <summary>Reset everything: surfaces, data models, visuals.</summary>
    public void Reset() => _router.ResetAll();

    private void OnSurfaceCreated(object? sender, SurfaceHost host)
    {
        UpdateLayout();
    }

    private void OnSurfaceDeleted(object? sender, string surfaceId)
    {
        UpdateLayout();
    }

    // SurfaceId → existing TabViewItem, so add/remove diffs can preserve the
    // user's selected tab and avoid re-templating unchanged surfaces (M15).
    private readonly Dictionary<string, TabViewItem> _surfaceTabs = new(StringComparer.Ordinal);
    // SurfaceId → ScrollViewer wrapping that surface's RootElement. A2UI is
    // designed against web semantics: surfaces don't carry their own outer
    // scroller because the document body scrolls. WinUI containers don't, so
    // a tall surface clips at the viewport. Cached so reconciling tab content
    // doesn't churn ScrollViewer instances (and lose scroll position) on
    // every UpdateLayout pass.
    private readonly Dictionary<string, ScrollViewer> _surfaceScrollers = new(StringComparer.Ordinal);

    private ScrollViewer GetOrCreateScroller(SurfaceHost s)
    {
        if (_surfaceScrollers.TryGetValue(s.SurfaceId, out var existing))
        {
            if (!ReferenceEquals(existing.Content, s.RootElement))
                existing.Content = s.RootElement;
            return existing;
        }
        // HorizontalScrollBarVisibility=Disabled forces children to lay out
        // within the viewport width — same as the HTML body's default of
        // wrapping rather than overflowing horizontally. Vertical=Auto keeps
        // the bar out of sight when the content fits.
        var sv = new ScrollViewer
        {
            Content = s.RootElement,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            ZoomMode = ZoomMode.Disabled,
        };
        _surfaceScrollers[s.SurfaceId] = sv;
        return sv;
    }

    private void UpdateLayout()
    {
        var surfaces = new List<SurfaceHost>(_router.Surfaces.Values);
        if (surfaces.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            SingleSurfaceHost.Visibility = Visibility.Collapsed;
            MultiSurfaceTabs.Visibility = Visibility.Collapsed;
            SingleSurfaceHost.Content = null;
            MultiSurfaceTabs.TabItems.Clear();
            _surfaceTabs.Clear();
            _surfaceScrollers.Clear();
            Title = OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_CanvasTitle");
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;

        // Drop cached scrollers for surfaces that no longer exist so we don't
        // pin their content trees in memory.
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in surfaces) live.Add(s.SurfaceId);
        var staleScrollers = new List<string>();
        foreach (var kv in _surfaceScrollers)
            if (!live.Contains(kv.Key)) staleScrollers.Add(kv.Key);
        foreach (var id in staleScrollers) _surfaceScrollers.Remove(id);

        if (surfaces.Count == 1)
        {
            var s = surfaces[0];
            SingleSurfaceHost.Visibility = Visibility.Visible;
            MultiSurfaceTabs.Visibility = Visibility.Collapsed;
            SingleSurfaceHost.Content = GetOrCreateScroller(s);
            MultiSurfaceTabs.TabItems.Clear();
            _surfaceTabs.Clear();
            Title = string.IsNullOrWhiteSpace(s.Title)
                ? OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_CanvasTitle")
                : s.Title!;
            return;
        }

        SingleSurfaceHost.Visibility = Visibility.Collapsed;
        MultiSurfaceTabs.Visibility = Visibility.Visible;
        SingleSurfaceHost.Content = null;

        // Diff incrementally so a third surface added doesn't reset the user's
        // selected tab. Algorithm:
        //   1. Track the currently selected surface id (if any) so we can restore it.
        //   2. Remove tabs whose surfaceId no longer exists.
        //   3. For each surface: reuse the cached TabViewItem (refresh header) or create.
        //   4. Reorder TabItems to match the surface list.
        var selectedId = (MultiSurfaceTabs.SelectedItem as TabViewItem)?.Tag as string;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in surfaces) seen.Add(s.SurfaceId);

        // Pass 1: drop stale.
        var stale = new List<string>();
        foreach (var kv in _surfaceTabs)
            if (!seen.Contains(kv.Key)) stale.Add(kv.Key);
        foreach (var id in stale)
        {
            if (_surfaceTabs.Remove(id, out var oldTab))
                MultiSurfaceTabs.TabItems.Remove(oldTab);
        }

        // Pass 2: ensure each surface has a tab and the visual ordering matches.
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            var header = string.IsNullOrWhiteSpace(s.Title) ? s.SurfaceId : s.Title!;
            var scroller = GetOrCreateScroller(s);
            if (!_surfaceTabs.TryGetValue(s.SurfaceId, out var tab))
            {
                tab = new TabViewItem
                {
                    Header = header,
                    Content = scroller,
                    IsClosable = false,
                    Tag = s.SurfaceId,
                };
                _surfaceTabs[s.SurfaceId] = tab;
                MultiSurfaceTabs.TabItems.Insert(i, tab);
            }
            else
            {
                if (!Equals(tab.Header, header)) tab.Header = header;
                if (!ReferenceEquals(tab.Content, scroller)) tab.Content = scroller;
                int currentIndex = MultiSurfaceTabs.TabItems.IndexOf(tab);
                if (currentIndex != i)
                {
                    MultiSurfaceTabs.TabItems.RemoveAt(currentIndex);
                    MultiSurfaceTabs.TabItems.Insert(i, tab);
                }
            }
        }

        // Restore selection by surfaceId. WinUI auto-selects index 0 when items
        // are inserted into an empty TabView; re-select the previously-selected
        // surface if it still exists.
        if (selectedId != null && _surfaceTabs.TryGetValue(selectedId, out var stillSelected))
            MultiSurfaceTabs.SelectedItem = stillSelected;

        Title = OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_CanvasTitle");
    }

    /// <summary>
    /// Render the current visible content (Empty / Single / Multi mode) into a
    /// PNG or JPEG and return base64. Uses XAML's RenderTargetBitmap so the
    /// snapshot reflects the actual visual tree, not a Win32 window blit.
    /// </summary>
    public async Task<string> CaptureSnapshotAsync(string format = "png")
    {
        // Pick the visible host. Falls back to the root grid if neither is shown
        // (e.g. surfaces have been pushed but layout hasn't settled).
        FrameworkElement? target = null;
        if (SingleSurfaceHost.Visibility == Visibility.Visible)
            target = SingleSurfaceHost;
        else if (MultiSurfaceTabs.Visibility == Visibility.Visible)
            target = MultiSurfaceTabs;
        else if (EmptyPanel.Visibility == Visibility.Visible)
            target = EmptyPanel;

        if (target == null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            target = RootGrid;

        var rtb = new RenderTargetBitmap();
        try
        {
            await rtb.RenderAsync(target);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Common when the window is minimised or has not yet completed first
            // layout. RenderTargetBitmap surfaces the underlying composition error
            // as a COMException; surface a structured code rather than a stack
            // trace through the snapshot pipeline.
            throw new InvalidOperationException("CANVAS_SNAPSHOT_NOT_VISIBLE: window is not in a renderable state", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("CANVAS_SNAPSHOT_NOT_VISIBLE: target is not in a renderable state", ex);
        }
        var pixelBuffer = await rtb.GetPixelsAsync();
        var pixels = pixelBuffer.ToArray();

        var encoderId = format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) || format.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            ? BitmapEncoder.JpegEncoderId
            : BitmapEncoder.PngEncoderId;

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        var dpi = 96.0; // RenderTargetBitmap is logical-pixel sized; encode at 1:1.
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            dpi,
            dpi,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// JSON state dump: surfaces (with components + data model) keyed by id.
    /// Returned as a string so callers can hand it back through MCP without
    /// further serialization. Reflects the renderer's authoritative state at
    /// call time — components that haven't been rendered yet are still listed.
    /// </summary>
    public string GetStateSnapshot()
    {
        var surfaces = new JsonObject();
        foreach (var (id, host) in _router.Surfaces)
        {
            surfaces[id] = host.GetSnapshot();
        }
        var snapshot = new JsonObject
        {
            ["renderer"] = "native",
            ["a2uiVersion"] = "0.8",
            ["surfaceCount"] = _router.Surfaces.Count,
            ["surfaces"] = surfaces,
        };
        return snapshot.ToJsonString();
    }

    public void BringToFront(bool keepTopMost = false)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_SHOWNORMAL);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
            if (!keepTopMost)
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }
        catch { /* best-effort */ }
    }
}
