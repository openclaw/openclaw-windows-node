namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Auto-generates accessible summaries of chart data for screen readers.
/// Wired to <c>AutomationProperties.FullDescription</c> via the chart automation peer.
/// </summary>
internal static class ChartSummarizer
{
    /// <summary>
    /// Generates a structured summary of the chart's data.
    /// </summary>
    internal static ChartSummary Summarize(IChartAccessibilityData data, string? chartType = null)
    {
        var overview = GenerateOverview(data, chartType);
        var axisRanges = GenerateAxisRanges(data);
        var seriesStats = data.Series.Select(ComputeSeriesStats).ToArray();
        var outliers = data.Series.SelectMany((s, si) => DetectOutliers(s, si)).ToArray();

        return new ChartSummary(
            Overview: overview,
            AxisRanges: axisRanges,
            SeriesStats: seriesStats,
            Outliers: outliers);
    }

    /// <summary>
    /// Formats the full summary as a single string suitable for FullDescription.
    /// </summary>
    internal static string FormatSummary(ChartSummary summary)
    {
        var parts = new List<string> { summary.Overview };

        if (!string.IsNullOrEmpty(summary.AxisRanges))
            parts.Add(summary.AxisRanges);

        foreach (var stats in summary.SeriesStats)
        {
            var trend = stats.TrendVerdict != null ? $", {stats.TrendVerdict}" : "";
            parts.Add($"{stats.SeriesName}: min {FormatValue(stats.Min)}, max {FormatValue(stats.Max)}{trend}.");
        }

        if (summary.Outliers.Length > 0)
        {
            var outlierDesc = string.Join("; ", summary.Outliers.Select(o =>
                $"{o.SeriesName} point {o.PointIndex + 1} ({FormatValue(o.Value)})"));
            parts.Add($"Outliers: {outlierDesc}.");
        }

        return string.Join(" ", parts);
    }

    // ── Overview ─────────────────────────────────────────────────────

    private static string GenerateOverview(IChartAccessibilityData data, string? chartType)
    {
        var type = chartType ?? "Chart";
        var seriesCount = data.Series.Count;
        if (seriesCount == 0)
            return $"Empty {type.ToLowerInvariant()} chart.";

        var totalPoints = data.Series.Sum(s => s.Points.Count);
        string pointsDesc;
        if (data.Series.Count == 1)
        {
            pointsDesc = $"{totalPoints} points";
        }
        else
        {
            // For multiple series, report per-series counts if uniform, else total
            var counts = data.Series.Select(s => s.Points.Count).Distinct().ToArray();
            pointsDesc = counts.Length == 1
                ? $"{counts[0]} points each"
                : $"{totalPoints} points total";
        }

        return $"{type} chart, {seriesCount} series, {pointsDesc}.";
    }

    // ── Axis ranges ──────────────────────────────────────────────────

    private static string GenerateAxisRanges(IChartAccessibilityData data)
    {
        if (data.Axes.Count == 0) return string.Empty;

        var parts = data.Axes.Select(axis =>
        {
            var label = axis.Label ?? (axis.AxisType == ChartAxisType.X ? "X" : "Y");
            var units = axis.Units != null ? $" {axis.Units}" : "";
            return $"{label}: {FormatValue(axis.Min)}{units} to {FormatValue(axis.Max)}{units}";
        });

        return string.Join(". ", parts) + ".";
    }

    // ── Series statistics ────────────────────────────────────────────

    private static SeriesStatsResult ComputeSeriesStats(ChartSeriesDescriptor series)
    {
        if (series.Points.Count == 0)
            return new SeriesStatsResult(series.Name, 0, 0, null);

        var values = series.Points.Select(p => p.YValue).ToArray();
        var min = values.Min();
        var max = values.Max();
        var trend = DetectTrend(values);

        return new SeriesStatsResult(series.Name, min, max, trend);
    }

    // ── Mann-Kendall trend detection ─────────────────────────────────

    /// <summary>
    /// Simple Mann-Kendall test for monotonic trend.
    /// Returns "increasing", "decreasing", or null (no clear trend).
    /// </summary>
    internal static string? DetectTrend(double[] values)
    {
        if (values.Length < 3) return null;

        int s = 0;
        int n = values.Length;

        for (int i = 0; i < n - 1; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (values[j] > values[i]) s++;
                else if (values[j] < values[i]) s--;
            }
        }

        // Compute variance for significance test
        double variance = n * (n - 1.0) * (2.0 * n + 5.0) / 18.0;
        double z = variance > 0 ? (s - Math.Sign(s)) / Math.Sqrt(variance) : 0;

        // Significance at α = 0.05 (z > 1.96)
        if (z > 1.96) return "increasing";
        if (z < -1.96) return "decreasing";
        return null;
    }

    // ── Outlier detection ────────────────────────────────────────────

    /// <summary>
    /// Flags points more than 2σ from the series mean.
    /// </summary>
    internal static OutlierResult[] DetectOutliers(ChartSeriesDescriptor series, int seriesIndex)
    {
        if (series.Points.Count < 3) return [];

        var values = series.Points.Select(p => p.YValue).ToArray();
        var mean = values.Average();
        var stdDev = Math.Sqrt(values.Select(v => (v - mean) * (v - mean)).Average());

        if (stdDev < 1e-10) return [];

        return series.Points
            .Select((p, i) => (Point: p, Index: i))
            .Where(t => Math.Abs(t.Point.YValue - mean) > 2 * stdDev)
            .Select(t => new OutlierResult(series.Name, t.Index, t.Point.YValue, seriesIndex))
            .ToArray();
    }

    private static string FormatValue(double value)
    {
        return value == Math.Truncate(value)
            ? value.ToString("N0")
            : value.ToString("N2");
    }
}

/// <summary>
/// Structured summary of a chart's data for accessibility.
/// </summary>
internal record ChartSummary(
    string Overview,
    string AxisRanges,
    SeriesStatsResult[] SeriesStats,
    OutlierResult[] Outliers);

/// <summary>Per-series min/max and trend verdict.</summary>
internal record SeriesStatsResult(
    string SeriesName,
    double Min,
    double Max,
    string? TrendVerdict);

/// <summary>Describes a detected outlier point.</summary>
internal record OutlierResult(
    string SeriesName,
    int PointIndex,
    double Value,
    int SeriesIndex);
