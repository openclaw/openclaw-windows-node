using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Snapshot of a registered window as surfaced by <c>reactor.windows</c>.
/// </summary>
internal sealed record WindowInfo(
    string Id,
    string Title,
    long Hwnd,
    WindowBounds Bounds,
    bool IsMain,
    string BuildTag);

internal readonly record struct WindowBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Tracks live WinUI windows and their stable MCP ids. Id assignment happens at
/// <see cref="Window.Activated"/> time; ids are reserved forever so a reopened
/// window with the same title takes a suffix, not the original id.
/// </summary>
internal sealed class WindowRegistry
{
    private readonly object _lock = new();
    private readonly WindowIdAllocator _allocator = new();
    private readonly List<Entry> _entries = new();
    private readonly string _buildTag;

    public WindowRegistry(string buildTag) => _buildTag = buildTag;

    /// <summary>
    /// Attaches to a window so it will appear in the registry on activation.
    /// Safe to call before or after Activated has fired — we also register the
    /// current state eagerly so tests don't need to pump the dispatcher.
    /// <paramref name="stableId"/> forces a specific id (e.g. <c>"main"</c> for
    /// the primary devtools window) so the handle stays the same even as the
    /// window's title changes on <c>switchComponent</c>.
    /// </summary>
    public void Attach(Window window, bool isMain = false, string? stableId = null)
    {
        RegisterCore(window, isMain, stableId);
        window.Activated += (_, _) => RegisterCore(window, isMain, stableId);
        window.Closed += (_, _) => Forget(window);
    }

    private void RegisterCore(Window window, bool isMain, string? stableId)
    {
        lock (_lock)
        {
            if (_entries.Any(e => ReferenceEquals(e.Window.Target, window))) return;

            // The devtools main window pins to "main" so the id survives
            // switchComponent (which updates the window title). Secondary
            // windows fall through to the title-based allocator.
            var id = stableId is not null
                ? _allocator.Reserve(stableId)
                : _allocator.Allocate(window.Title);
            _entries.Add(new Entry(id, new WeakReference(window), isMain));
        }
    }

    private void Forget(Window window)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => ReferenceEquals(e.Window.Target, window));
        }
    }

    /// <summary>Returns a snapshot of active windows for the <c>windows</c> tool.</summary>
    public IReadOnlyList<WindowInfo> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<WindowInfo>(_entries.Count);
            foreach (var entry in _entries)
            {
                if (entry.Window.Target is not Window w) continue;
                var bounds = ReadBounds(w);
                result.Add(new WindowInfo(
                    Id: entry.Id,
                    Title: w.Title ?? "",
                    Hwnd: TryGetHwnd(w),
                    Bounds: bounds,
                    IsMain: entry.IsMain,
                    BuildTag: _buildTag));
            }
            return result;
        }
    }

    /// <summary>Resolves an id to its Window. Returns null if not registered or disposed.</summary>
    public Window? Resolve(string id)
    {
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (entry.Id == id && entry.Window.Target is Window w)
                    return w;
            }
            return null;
        }
    }

    /// <summary>
    /// When exactly one window is registered, returns it; when multiple are
    /// registered, returns null so callers can error with the available ids.
    /// </summary>
    public Window? TryDefault(out IReadOnlyList<string> activeIds)
    {
        lock (_lock)
        {
            activeIds = _entries
                .Where(e => e.Window.Target is Window)
                .Select(e => e.Id)
                .ToArray();

            if (activeIds.Count == 1)
                return _entries.First(e => e.Window.Target is Window).Window.Target as Window;
            return null;
        }
    }

    private static long TryGetHwnd(Window w)
    {
        try
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(w).ToInt64();
        }
        catch { return 0; }
    }

    private static WindowBounds ReadBounds(Window w)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
            if (GetWindowRect(hwnd, out var r))
                return new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        catch { }
        return new WindowBounds(0, 0, 0, 0);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    // Unit-test hooks ------------------------------------------------------------

    internal WindowIdAllocator AllocatorForTests => _allocator;

    private sealed record Entry(string Id, WeakReference Window, bool IsMain);
}
