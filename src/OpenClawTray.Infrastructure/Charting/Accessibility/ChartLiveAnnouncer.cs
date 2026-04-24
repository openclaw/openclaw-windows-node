using System.Diagnostics;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Manages live-region announcements for a chart. Provides debouncing (400ms trailing)
/// to collapse rapid-fire events into a single announcement, and message templates
/// for common chart events.
/// </summary>
internal sealed class ChartLiveAnnouncer
{
    private readonly Stopwatch _lastAnnounceTimer = new();
    private readonly object _lock = new();
    private string? _pendingMessage;
    private ChartAnnouncePriority _pendingPriority;
    private bool _animationInFlight;

    /// <summary>Minimum interval between announcements (trailing debounce).</summary>
    public const int DebounceMs = 400;

    /// <summary>The most recent message that was (or will be) announced.</summary>
    public string? CurrentMessage { get; private set; }

    /// <summary>The priority of the current announcement.</summary>
    public ChartAnnouncePriority CurrentPriority { get; private set; }

    /// <summary>
    /// Queues a message for announcement via the live region. If a previous
    /// announcement is still within the debounce window, the new message replaces it
    /// (burst collapse). Only <see cref="ChartAnnouncePriority.Assertive"/> messages
    /// bypass debounce.
    /// </summary>
    public void Announce(string message, ChartAnnouncePriority priority = ChartAnnouncePriority.Polite)
    {
        lock (_lock)
        {
            // Assertive messages (errors) bypass debounce
            if (priority == ChartAnnouncePriority.Assertive)
            {
                CurrentMessage = message;
                CurrentPriority = priority;
                _lastAnnounceTimer.Restart();
                _pendingMessage = null;
                return;
            }

            // Suppress during animation unless reduced motion
            if (_animationInFlight)
            {
                _pendingMessage = message;
                _pendingPriority = priority;
                return;
            }

            // Debounce: if within the window, replace pending message
            if (_lastAnnounceTimer.IsRunning && _lastAnnounceTimer.ElapsedMilliseconds < DebounceMs)
            {
                _pendingMessage = message;
                _pendingPriority = priority;
                return;
            }

            CurrentMessage = message;
            CurrentPriority = priority;
            _lastAnnounceTimer.Restart();
            _pendingMessage = null;
        }
    }

    /// <summary>
    /// Called on each frame/tick to flush pending debounced messages.
    /// Returns the message to announce (if any) or null if nothing is pending.
    /// </summary>
    public (string? Message, ChartAnnouncePriority Priority) Flush()
    {
        lock (_lock)
        {
            if (_pendingMessage is null)
                return (null, ChartAnnouncePriority.Polite);

            if (!_lastAnnounceTimer.IsRunning || _lastAnnounceTimer.ElapsedMilliseconds >= DebounceMs)
            {
                var msg = _pendingMessage;
                var pri = _pendingPriority;
                CurrentMessage = msg;
                CurrentPriority = pri;
                _pendingMessage = null;
                _lastAnnounceTimer.Restart();
                return (msg, pri);
            }

            return (null, ChartAnnouncePriority.Polite);
        }
    }

    /// <summary>
    /// Re-speaks the full current view summary regardless of debounce state (S key).
    /// Does not interrupt in-progress announcement — queues instead if within debounce window.
    /// </summary>
    public void RequestSummary(string summaryText)
    {
        lock (_lock)
        {
            // Queue after current announcement completes
            if (_lastAnnounceTimer.IsRunning && _lastAnnounceTimer.ElapsedMilliseconds < DebounceMs)
            {
                _pendingMessage = summaryText;
                _pendingPriority = ChartAnnouncePriority.Polite;
            }
            else
            {
                CurrentMessage = summaryText;
                CurrentPriority = ChartAnnouncePriority.Polite;
                _lastAnnounceTimer.Restart();
            }
        }
    }

    /// <summary>Mark animation as in-flight. Suppresses intermediate announcements.</summary>
    public void BeginAnimation()
    {
        lock (_lock)
        {
            _animationInFlight = true;
        }
    }

    /// <summary>Mark animation as settled. Flushes any pending announcement.</summary>
    public void EndAnimation()
    {
        lock (_lock)
        {
            _animationInFlight = false;
            // Flush pending message that was suppressed during animation
            if (_pendingMessage is not null)
            {
                CurrentMessage = _pendingMessage;
                CurrentPriority = _pendingPriority;
                _pendingMessage = null;
                _lastAnnounceTimer.Restart();
            }
        }
    }

    /// <summary>Whether an animation is currently in flight.</summary>
    public bool IsAnimating { get { lock (_lock) { return _animationInFlight; } } }

    // ── Message templates ──────────────────────────────────────────

    /// <summary>Generates a zoom announcement message.</summary>
    public static string ZoomMessage(double zoomLevel) =>
        $"Zoomed to {zoomLevel:P0}";

    /// <summary>Generates a pan announcement message.</summary>
    public static string PanMessage(double xMin, double xMax) =>
        $"Viewing range {xMin:G4} to {xMax:G4}";

    /// <summary>Generates a brush selection announcement message.</summary>
    public static string BrushMessage(int startIndex, int endIndex, int totalPoints) =>
        $"Selected points {startIndex + 1} through {endIndex + 1} of {totalPoints}";

    /// <summary>Generates a filter change announcement message.</summary>
    public static string FilterMessage(int visibleSeries, int totalSeries) =>
        $"Showing {visibleSeries} of {totalSeries} series";

    /// <summary>Generates a data update announcement message.</summary>
    public static string DataUpdateMessage(int seriesCount, int pointCount) =>
        $"Data updated: {seriesCount} series, {pointCount} points";

    /// <summary>Generates a series toggle announcement message.</summary>
    public static string SeriesToggleMessage(string seriesName, bool visible) =>
        visible ? $"Series \"{seriesName}\" shown" : $"Series \"{seriesName}\" hidden";

    /// <summary>Assertive error: no data in selected range.</summary>
    public static string NoDataInRangeMessage() =>
        "No data in selected range.";
}

/// <summary>
/// Priority level for chart live-region announcements.
/// </summary>
internal enum ChartAnnouncePriority
{
    /// <summary>Polite: announced after current speech completes.</summary>
    Polite,

    /// <summary>Assertive: interrupts current speech. Reserved for errors.</summary>
    Assertive,
}
