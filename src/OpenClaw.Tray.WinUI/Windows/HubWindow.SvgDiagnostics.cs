using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClawTray.Services;
using WinMemoryManager = Windows.System.MemoryManager;
using WinAppMemoryUsageLimitChangingEventArgs = Windows.System.AppMemoryUsageLimitChangingEventArgs;

namespace OpenClawTray.Windows;

// Diagnostics scaffolding for the colorful SvgImageSource sidebar icons.
// Motivation: a Scott-reported repro showed the left-nav icons going completely
// blank after long uptime while the rest of the UI (FontIcon-based glyphs)
// rendered fine. WinUI 3 does not use GDI; the suspected failure path is
// silent D2D/D3D rasterization failure of SvgImageSource under memory or
// device-lost pressure.
//
// Detection strategy: hook both SvgImageSource.Opened (success) and OpenFailed
// (parse/URI failure) on every NavView resource key at construction time, BEFORE
// any consumer has caused decode. Track which keys ever reported Opened. The
// sanity check then walks NavigationView items and warns about any ImageIcon
// whose backing SvgImageSource never produced an Opened event. We deliberately
// do not rely on RasterizePixelWidth/Height -- those are input properties (target
// raster size), not decode-status outputs.
public sealed partial class HubWindow
{
    private static readonly TimeSpan SvgDiagnosticsCheckInterval = TimeSpan.FromSeconds(60);

    // Resource-dictionary keys for SvgImageSource entries declared at NavView.Resources
    // in HubWindow.xaml. Kept explicit so we know exactly which icons are expected to load.
    private static readonly string[] s_sidebarIconResourceKeys =
    {
        "Chat_Icon", "Connection_Icon", "Sessions_Icon", "Skills_Icon",
        "Channels_Icon", "Instances_Icon", "Advanced_Icon", "AgentEvents_Icon",
        "Agents_Icon", "Bindings_Icon", "Config_Icon", "Usage_Icon",
        "Cron_Icon", "Voice_Icon", "Settings_Icon", "Permissions_Icon",
        "Sandbox_Icon", "Activity_Icon", "Debug_Icon", "Info_Icon",
    };

    // Cap on blank-icon warnings per process so a stuck failure doesn't fill the log.
    private const int MaxBlankIconLogs = 50;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _svgDiagnosticsTimer;
    private DateTime? _lastThemeChangeUtc;
    private DateTime? _lastXamlRootChangeUtc;
    private int _blankIconLogCount;
    private bool _svgDiagnosticsInitialized;
    private bool _memoryManagerHooked;
    private bool _globalTimingMissLogged;

    // Reverse map from each tracked SvgImageSource back to its resource key, so the
    // sanity check can identify which icon a nav ImageIcon is referencing.
    private readonly Dictionary<SvgImageSource, string> _trackedSvgKeysByInstance = new();

    // Keys for which SvgImageSource.Opened has fired. Used as the success signal
    // (replaces the unreliable RasterizePixelWidth==0 heuristic from earlier).
    private readonly HashSet<string> _openedSvgKeys = new(StringComparer.Ordinal);

    private void InitializeSvgDiagnostics()
    {
        if (_svgDiagnosticsInitialized) return;
        _svgDiagnosticsInitialized = true;

        // High-contrast mode replaces the SVG icons with FontIcons at construction,
        // so there is nothing meaningful to monitor on the SVG decode path.
        if (_isHighContrast)
        {
            Logger.Debug("[SvgDiag] Skipping init (HighContrast active; FontIcons in use).");
            return;
        }

        try
        {
            HookSvgEventHandlers();
            HookMemoryPressureListener();

            NavView.Loaded += OnNavViewLoadedForSvgDiagnostics;

            _svgDiagnosticsTimer = DispatcherQueue.CreateTimer();
            _svgDiagnosticsTimer.Interval = SvgDiagnosticsCheckInterval;
            _svgDiagnosticsTimer.IsRepeating = true;
            _svgDiagnosticsTimer.Tick += OnSvgDiagnosticsTimerTick;
            _svgDiagnosticsTimer.Start();

            Logger.Info($"[SvgDiag] Initialized (interval={SvgDiagnosticsCheckInterval.TotalSeconds}s, icons={s_sidebarIconResourceKeys.Length}).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SvgDiag] InitializeSvgDiagnostics failed: {ex.Message}");
        }
    }

    private void HookSvgEventHandlers()
    {
        // Subscribed at ctor time, before any consumer triggers decode. Tracking the
        // Opened event lets the sanity check tell "this source never decoded" apart
        // from "this source decoded fine" without depending on the (input-only)
        // Rasterize* properties.
        foreach (var key in s_sidebarIconResourceKeys)
        {
            if (NavView.Resources.TryGetValue(key, out var value) && value is SvgImageSource svg)
            {
                _trackedSvgKeysByInstance[svg] = key;

                var capturedKey = key;
                var capturedSvg = svg;
                svg.Opened += (sender, args) =>
                {
                    _openedSvgKeys.Add(capturedKey);
                };
                svg.OpenFailed += (sender, args) =>
                {
                    // OpenFailed signals parse/URI/IO failure at the initial Open step.
                    // Post-load rasterization failures (the suspected long-uptime case)
                    // are caught by the sanity check seeing the key missing from
                    // _openedSvgKeys -- though for sources that opened once and then lose
                    // their backing surface, neither signal fires. That class of failure
                    // is what the XamlRoot.Changed / memory-pressure correlations exist for.
                    Logger.Warn($"[SvgDiag] SvgImageSource.OpenFailed key={capturedKey} uri={capturedSvg.UriSource} status={args.Status}");
                };
            }
            else
            {
                Logger.Debug($"[SvgDiag] Missing or non-SVG resource key: {key}");
            }
        }
    }

    private void HookMemoryPressureListener()
    {
        try
        {
            WinMemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            WinMemoryManager.AppMemoryUsageLimitChanging += OnAppMemoryUsageLimitChanging;
            _memoryManagerHooked = true;
        }
        catch (Exception ex)
        {
            // MemoryManager requires packaged identity. If we are running unpackaged
            // (e.g. dev builds), the API will throw on subscription and we degrade silently.
            Logger.Debug($"[SvgDiag] MemoryManager hooks unavailable: {ex.Message}");
        }
    }

    // Teardown for state that must be released when the window closes. Called from the
    // HubWindow.xaml.cs Closed handler. The static MemoryManager events would otherwise
    // root this HubWindow instance forever, leaking the window (and its entire visual
    // tree) across every open/close cycle.
    internal void TeardownSvgDiagnostics()
    {
        _svgDiagnosticsTimer?.Stop();
        if (_memoryManagerHooked)
        {
            try
            {
                WinMemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
                WinMemoryManager.AppMemoryUsageLimitChanging -= OnAppMemoryUsageLimitChanging;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[SvgDiag] MemoryManager unhook failed: {ex.Message}");
            }
            _memoryManagerHooked = false;
        }
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        try
        {
            var level = WinMemoryManager.AppMemoryUsageLevel;
            var usage = WinMemoryManager.AppMemoryUsage;
            var limit = WinMemoryManager.AppMemoryUsageLimit;
            Logger.Info($"[SvgDiag] AppMemoryUsageIncreased level={level} usage={usage} limit={limit}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[SvgDiag] AppMemoryUsageIncreased read failed: {ex.Message}");
        }
    }

    private void OnAppMemoryUsageLimitChanging(object? sender, WinAppMemoryUsageLimitChangingEventArgs args)
    {
        // Fires on a thread-pool thread (WinRT static event). An unhandled exception
        // here would terminate the process via the runtime's unhandled-exception path,
        // so we mirror the try/catch hardening from OnAppMemoryUsageIncreased.
        try
        {
            Logger.Info($"[SvgDiag] AppMemoryUsageLimitChanging old={args.OldLimit} new={args.NewLimit}");
        }
        catch (Exception ex)
        {
            try { Logger.Debug($"[SvgDiag] AppMemoryUsageLimitChanging log failed: {ex.Message}"); } catch { }
        }
    }

    private void OnSvgDiagnosticsTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        RunSvgIconSanityCheck("timer");
    }

    private void OnNavViewLoadedForSvgDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            if (NavView.XamlRoot is { } root)
            {
                root.Changed -= OnXamlRootChangedForSvgDiagnostics;
                root.Changed += OnXamlRootChangedForSvgDiagnostics;
            }

            NavView.ActualThemeChanged -= OnNavViewThemeChangedForSvgDiagnostics;
            NavView.ActualThemeChanged += OnNavViewThemeChangedForSvgDiagnostics;

            // Baseline check after layout so RasterizePixelWidth has stabilized.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => RunSvgIconSanityCheck("loaded"));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SvgDiag] OnNavViewLoadedForSvgDiagnostics failed: {ex.Message}");
        }
    }

    private void OnXamlRootChangedForSvgDiagnostics(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        _lastXamlRootChangeUtc = DateTime.UtcNow;
        try
        {
            Logger.Info($"[SvgDiag] XamlRoot.Changed scale={sender.RasterizationScale:F2} size={sender.Size.Width:F0}x{sender.Size.Height:F0} visible={sender.IsHostVisible}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[SvgDiag] XamlRoot.Changed read failed: {ex.Message}");
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => RunSvgIconSanityCheck("xamlroot-changed"));
    }

    private void OnNavViewThemeChangedForSvgDiagnostics(FrameworkElement sender, object args)
    {
        _lastThemeChangeUtc = DateTime.UtcNow;
        Logger.Info($"[SvgDiag] NavView.ActualThemeChanged theme={sender.ActualTheme}");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => RunSvgIconSanityCheck("theme-changed"));
    }

    private void RunSvgIconSanityCheck(string trigger)
    {
        if (IsClosed) return;
        if (_blankIconLogCount >= MaxBlankIconLogs) return;

        try
        {
            var unopened = new List<string>();
            CollectUnopenedIconNavItems(NavView.MenuItems, unopened);
            CollectUnopenedIconNavItems(NavView.FooterMenuItems, unopened);

            if (unopened.Count == 0)
                return;

            // Defensive guard: if we tracked SvgImageSources but never saw a single Opened event,
            // the most likely explanation is that handler attachment raced the initial decode (not
            // that every icon actually failed). Log once and short-circuit to avoid log-cap burn.
            if (_openedSvgKeys.Count == 0 && _trackedSvgKeysByInstance.Count > 0)
            {
                if (!_globalTimingMissLogged)
                {
                    _globalTimingMissLogged = true;
                    Logger.Warn($"[SvgDiag] No Opened events observed for any tracked SvgImageSource (trigger={trigger}, tracked={_trackedSvgKeysByInstance.Count}); handlers may have been attached after initial decode. Suppressing per-icon warnings for this session.");
                }
                return;
            }

            _blankIconLogCount++;
            var snapshot = CaptureDiagnosticSnapshot();
            Logger.Warn($"[SvgDiag] Unopened SvgImageSource icons detected (trigger={trigger}) count={unopened.Count} icons=[{string.Join(", ", unopened)}] openedCount={_openedSvgKeys.Count}/{_trackedSvgKeysByInstance.Count} {snapshot}");

            if (_blankIconLogCount == MaxBlankIconLogs)
                Logger.Warn($"[SvgDiag] Blank-icon log cap reached ({MaxBlankIconLogs}); suppressing further warnings this session.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SvgDiag] RunSvgIconSanityCheck({trigger}) failed: {ex.Message}");
        }
    }

    private void CollectUnopenedIconNavItems(IList<object> items, List<string> unopened)
    {
        foreach (var obj in items)
        {
            if (obj is not NavigationViewItem item) continue;
            if (item.Icon is ImageIcon imageIcon
                && imageIcon.Source is SvgImageSource svg
                && _trackedSvgKeysByInstance.TryGetValue(svg, out var resourceKey)
                && !_openedSvgKeys.Contains(resourceKey))
            {
                var navName = item.Tag as string ?? item.Content as string ?? "(unnamed)";
                unopened.Add($"{navName}={resourceKey}");
            }
            if (item.MenuItems.Count > 0)
                CollectUnopenedIconNavItems(item.MenuItems, unopened);
        }
    }

    private string CaptureDiagnosticSnapshot()
    {
        var sb = new StringBuilder();
        try
        {
            using var proc = Process.GetCurrentProcess();
            var uptimeSec = (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds;
            sb.Append($"uptimeSec={uptimeSec:F0} ");
            sb.Append($"handles={proc.HandleCount} ");
            sb.Append($"workingSetMb={proc.WorkingSet64 / (1024 * 1024)} ");
        }
        catch (Exception ex)
        {
            sb.Append($"procReadErr={ex.GetType().Name} ");
        }
        try
        {
            sb.Append($"memUsage={WinMemoryManager.AppMemoryUsage} ");
            sb.Append($"memLimit={WinMemoryManager.AppMemoryUsageLimit} ");
            sb.Append($"memLevel={WinMemoryManager.AppMemoryUsageLevel} ");
        }
        catch
        {
            // MemoryManager not available (unpackaged); skip silently.
        }
        if (_lastThemeChangeUtc.HasValue)
            sb.Append($"sinceThemeChangeSec={(DateTime.UtcNow - _lastThemeChangeUtc.Value).TotalSeconds:F0} ");
        if (_lastXamlRootChangeUtc.HasValue)
            sb.Append($"sinceXamlRootChangeSec={(DateTime.UtcNow - _lastXamlRootChangeUtc.Value).TotalSeconds:F0} ");
        return sb.ToString().TrimEnd();
    }
}
