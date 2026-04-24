using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Manages focus save/restore for chart navigation. Saves the current focus position
/// (series index + point index) when focus leaves the chart, and restores it on return.
/// Also handles data/filter changes that may invalidate the saved position.
/// </summary>
internal sealed class ChartFocusContext
{
    private int _savedSeriesIndex;
    private int _savedPointIndex;
    private bool _hasSavedPosition;

    /// <summary>Whether a focus position has been saved.</summary>
    public bool HasSavedPosition => _hasSavedPosition;

    /// <summary>The saved series index.</summary>
    public int SavedSeriesIndex => _savedSeriesIndex;

    /// <summary>The saved point index.</summary>
    public int SavedPointIndex => _savedPointIndex;

    /// <summary>
    /// Saves the current focus position before the chart loses focus.
    /// Called when Tab leaves the plot area or when toggling to alternate view.
    /// </summary>
    public void SavePosition(int seriesIndex, int pointIndex)
    {
        _savedSeriesIndex = seriesIndex;
        _savedPointIndex = pointIndex;
        _hasSavedPosition = true;
    }

    /// <summary>
    /// Restores the saved focus position. Returns the (seriesIndex, pointIndex)
    /// to focus. If no position was saved, returns (0, 0).
    /// </summary>
    public (int SeriesIndex, int PointIndex) RestorePosition()
    {
        if (!_hasSavedPosition)
            return (0, 0);

        return (_savedSeriesIndex, _savedPointIndex);
    }

    /// <summary>
    /// Clears the saved focus position.
    /// </summary>
    public void ClearSavedPosition()
    {
        _hasSavedPosition = false;
        _savedSeriesIndex = 0;
        _savedPointIndex = 0;
    }

    /// <summary>
    /// Called when chart data or filters change. If the saved position is no longer
    /// valid (e.g., the series was filtered out), moves focus to the nearest
    /// surviving point in the same series. Returns the adjusted position and
    /// an optional announcement message if the position was adjusted.
    /// </summary>
    public (int SeriesIndex, int PointIndex, string? Announcement) AdjustForDataChange(
        IReadOnlyList<ChartSeriesDescriptor> series)
    {
        if (!_hasSavedPosition || series.Count == 0)
            return (0, 0, null);

        // Clamp series index
        int si = _savedSeriesIndex;
        if (si >= series.Count)
        {
            si = series.Count - 1;
            _savedSeriesIndex = si;
        }

        // Clamp point index
        int pi = _savedPointIndex;
        if (pi >= series[si].Points.Count)
        {
            int oldPi = pi;
            pi = Math.Max(0, series[si].Points.Count - 1);
            _savedPointIndex = pi;

            string announcement = series[si].Points.Count == 0
                ? $"Series \"{series[si].Name}\" has no visible data points"
                : $"Moved to point {pi + 1} of {series[si].Points.Count} in \"{series[si].Name}\"";
            return (si, pi, announcement);
        }

        return (si, pi, null);
    }
}
