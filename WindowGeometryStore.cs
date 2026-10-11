using System;
using System.IO;
using System.Text.Json;
using OpenClawTray.Services;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// Remembers the size and position of a top-level window between runs.
/// </summary>
/// <remarks>
/// Window geometry is intentionally stored per machine rather than in roaming settings:
/// a size that suits a large desktop is wrong on a laptop, and <c>settings.json</c> roams.
/// <see cref="LoadOrNull"/> rejects saved values that no longer make sense (a monitor was
/// removed, the window would land off-screen, or the size is too small to render the
/// layout), so a stale file can never strand the window somewhere the user cannot reach
/// it; callers fall back to a screen-derived default instead.
/// </remarks>
internal sealed record WindowGeometry(int X, int Y, int Width, int Height)
{
    internal const int MinimumWidth = 640;
    internal const int MinimumHeight = 480;

    /// <summary>True when the size is large enough to render the layout.</summary>
    internal bool HasUsableSize => Width >= MinimumWidth && Height >= MinimumHeight;

    /// <summary>
    /// True when the rectangle overlaps the supplied working area by at least
    /// <paramref name="minimumVisiblePixels"/> in both axes. This is what keeps a saved
    /// position from leaving no grabbable part of the title bar on screen.
    /// </summary>
    internal bool IsVisibleWithin(
        int workLeft,
        int workTop,
        int workWidth,
        int workHeight,
        int minimumVisiblePixels = 48)
    {
        var overlapX = Math.Min(X + Width, workLeft + workWidth) - Math.Max(X, workLeft);
        var overlapY = Math.Min(Y + Height, workTop + workHeight) - Math.Max(Y, workTop);
        return overlapX >= minimumVisiblePixels && overlapY >= minimumVisiblePixels;
    }
}

/// <summary>Attaches restore-and-persist behaviour for a window's geometry.</summary>
internal static class WindowGeometryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // Target size, matching the harness/WorkBuddy companion windows which occupy about
    // 1295x823 on this display.
    private const int DefaultPixelWidth = 1295;
    private const int DefaultPixelHeight = 823;
    private const int WorkAreaWidthFallback = 1536;
    private const int WorkAreaHeightFallback = 816;

    /// <summary>
    /// Empirically measured difference between the size handed to
    /// <c>AppWindow.MoveAndResize</c> and the size the window actually ends up with.
    /// </summary>
    /// <remarks>
    /// On this machine a 1036-unit request renders 829 units wide, i.e. the platform applies
    /// the display scale (125%) a second time before the window is shown, so the visible
    /// window is 0.8x of what was asked for. Dividing by that factor compensates, giving the
    /// requested pixel size as the visible size. If a future Windows App SDK release fixes
    /// the unit handling in MoveAndResize, set this to 1.0.
    /// </remarks>
    private const double MoveAndResizeAppliedScale = 0.8;

    private static int ToMoveAndResizeUnits(int desiredPixels) =>
        (int)Math.Round(desiredPixels / MoveAndResizeAppliedScale);

    /// <summary>
    /// The same pixel count expressed in the units the platform applies scaling to.
    /// Used both to bound a request and to compare against a persisted size.
    /// </summary>
    private static int MoveAndResizeUnitsFor(int pixels) =>
        (int)Math.Round(pixels / MoveAndResizeAppliedScale);

    internal static string PathFor(string windowName) => Path.Combine(
        AppIdentity.ResolveLocalDataDirectory(),
        $"{windowName}-geometry.json");

    /// <summary>
    /// Applies the size and position a window should open with, then keeps the saved
    /// geometry up to date as the user moves or resizes it.
    /// </summary>
    /// <remarks>
    /// Call this from the window constructor. Restoring the last geometry is what makes a
    /// resize stick; sizing to the work area is what keeps the window fully visible on
    /// displays smaller than <see cref="DefaultMaxWidth"/> x <see cref="DefaultMaxHeight"/>.
    /// </remarks>
    internal static void Attach(WindowEx window, string windowName)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);

        AttachCore(window, windowName);
    }

    private static void AttachCore(WindowEx window, string windowName)
    {
        // Register persistence immediately: the window may be shown later (pre-warmed
        // instances are constructed once and only activated on demand), and we must not
        // miss a resize that happens before the first activation.
        try
        {
            window.AppWindow.Changed += (sender, args) => Persist(sender, args, windowName);
            window.Closed += (_, _) => SaveCurrent(window.AppWindow, windowName);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WindowGeometry] {windowName} could not subscribe to geometry changes: {ex.Message}");
        }

        // Apply geometry on first activation. Doing it from the constructor races the
        // platform's own initial sizing, which is what previously discarded the restored
        // rectangle; by activation the window has a real HWND and display area.
        var applied = false;
        void ApplyOnce(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
        {
            if (applied)
                return;
            applied = true;
            window.Activated -= ApplyOnce;
            Apply(window, windowName);
        }

        window.Activated += ApplyOnce;
    }

    /// <summary>
    /// Moves the window to the restored geometry, or to the companion-window default when
    /// there is nothing usable to restore.
    /// </summary>
    /// <remarks>
    /// Everything here is in the device-independent units shared by
    /// <c>DisplayArea.WorkArea</c>, <c>AppWindow.Size</c> and <c>MoveAndResize</c>. Scaling
    /// these to physical pixels before the call applies the display scale twice (measured on
    /// a 125% display: a 1295-wide request produced a 1036-wide window).
    /// </remarks>
    private static void Apply(WindowEx window, string windowName)
    {
        try
        {
            var appWindow = window.AppWindow;

            var work = Microsoft.UI.Windowing.DisplayArea
                .GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
                .WorkArea;

            var workX = work.X;
            var workY = work.Y;
            var workWidth = Math.Max(WindowGeometry.MinimumWidth, work.Width);
            var workHeight = Math.Max(WindowGeometry.MinimumHeight, work.Height);

            var saved = LoadOrNull(windowName);
            if (saved is not null
                && saved.IsVisibleWithin(workX, workY, workWidth, workHeight)
                && saved.Width <= workWidth
                && saved.Height <= workHeight)
            {
                appWindow.MoveAndResize(Clamp(saved, workX, workY, workWidth, workHeight));
                return;
            }

            // First run, or the saved rectangle no longer fits (monitor removed, resolution
            // reduced, work area shrunk). Use the companion-window size, shrunk if this
            // display is too small for it.
            var width = Math.Min(
                ToMoveAndResizeUnits(DefaultPixelWidth),
                Math.Max(WindowGeometry.MinimumWidth, workWidth));
            var height = Math.Min(
                ToMoveAndResizeUnits(DefaultPixelHeight),
                Math.Max(WindowGeometry.MinimumHeight, workHeight));

            // Centre horizontally and align to the top of the work area, which keeps the whole
            // window above the taskbar and leaves the bottom edge clear.
            var x = workX + Math.Max(0, (workWidth - width) / 2);

            appWindow.MoveAndResize(Clamp(
                new WindowGeometry(x, workY, width, height),
                workX, workY, workWidth, workHeight));
        }
        catch (Exception ex)
        {
            // Geometry problems must never stop a window from working.
            Logger.Warn($"[WindowGeometry] {windowName} geometry apply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the rectangle inside the work area. This is what guarantees the title bar and
    /// the bottom edge stay reachable instead of sitting off-screen or under the taskbar.
    /// </summary>
    private static global::Windows.Graphics.RectInt32 Clamp(
        WindowGeometry geometry,
        int workX,
        int workY,
        int workWidth,
        int workHeight)
    {
        var width = Math.Min(geometry.Width, workWidth);
        var height = Math.Min(geometry.Height, workHeight);
        var x = System.Math.Clamp(geometry.X, workX, Math.Max(workX, workX + workWidth - width));
        var y = System.Math.Clamp(geometry.Y, workY, Math.Max(workY, workY + workHeight - height));

        return new global::Windows.Graphics.RectInt32(x, y, width, height);
    }

    /// <summary>
    /// Keeps the rectangle inside the work area and converts it to the physical pixels that
    /// <c>MoveAndResize</c> expects. This is what guarantees the title bar and the bottom
    /// edge stay reachable instead of sitting off-screen or under the taskbar.
    /// </summary>
    private static void Persist(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args,
        string windowName)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
            return;

        Save(windowName, ReadCurrent(sender));
    }

    private static void SaveCurrent(Microsoft.UI.Windowing.AppWindow appWindow, string windowName)
    {
        try
        {
            Save(windowName, ReadCurrent(appWindow));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WindowGeometry] {windowName} geometry capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the window rectangle in the same units <see cref="Clamp"/> requests, so a
    /// restored value round-trips instead of drifting on every launch.
    /// </summary>
    private static WindowGeometry ReadCurrent(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        var pos = appWindow.Position;
        var size = appWindow.Size;
        return new WindowGeometry(pos.X, pos.Y, size.Width, size.Height);
    }

    /// <summary>Returns the saved geometry, or <see langword="null"/> when unusable.</summary>
    internal static WindowGeometry? LoadOrNull(string windowName)
    {
        try
        {
            var path = PathFor(windowName);
            if (!File.Exists(path))
                return null;

            var geometry = JsonSerializer.Deserialize<WindowGeometry>(
                File.ReadAllText(path), Options);

            return geometry is { HasUsableSize: true } ? geometry : null;
        }
        catch (Exception ex)
        {
            // A corrupt or unreadable file must never stop the window from opening.
            Logger.Warn($"[WindowGeometry] Could not read geometry for {windowName}: {ex.Message}");
            return null;
        }
    }

    internal static void Save(string windowName, WindowGeometry geometry)
    {
        if (!geometry.HasUsableSize)
            return;

        try
        {
            var path = PathFor(windowName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(geometry, Options));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WindowGeometry] Could not save geometry for {windowName}: {ex.Message}");
        }
    }
}
