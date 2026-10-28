using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Systems;

/// <summary>
/// Maps a metric series onto a chart box: oldest sample at the left edge, newest at the right.
/// Fixed-range metrics scale from zero to their ceiling. Autoranged metrics (disk space, which
/// barely moves) pad around the observed range so a steady trend stays visible without implying
/// a drop to zero.
/// </summary>
internal static class SparklineGeometry
{
    public const double TopPadding = 6;
    public const double BottomPadding = 2;

    public readonly record struct Range(double Min, double Span);

    public static Range Scale(IReadOnlyList<double> values, double ceiling, bool autorange)
    {
        var low = values.Count == 0 ? 0 : values.Min();
        var high = values.Count == 0 ? 0 : values.Max();
        if (!autorange)
        {
            var max = Math.Max(ceiling, high);
            return new Range(0, max > 0 ? max : 1);
        }
        var spread = Math.Max(Math.Max(high - low, Math.Abs(high) * 0.02), 1e-9);
        var floor = Math.Max(low - spread * 0.5, 0);
        return new Range(floor, high + spread * 0.25 - floor);
    }

    /// <summary>Pixel positions for each value. A single value sits at the right edge.</summary>
    public static IReadOnlyList<(double X, double Y)> Points(IReadOnlyList<double> values, Range range, double width, double height)
    {
        if (values.Count == 0 || width <= 0 || height <= 0)
            return Array.Empty<(double, double)>();
        var usable = Math.Max(height - TopPadding - BottomPadding, 1);
        var step = values.Count > 1 ? width / (values.Count - 1) : 0;
        return values.Select((value, index) =>
        {
            var ratio = Math.Clamp((value - range.Min) / range.Span, 0, 1);
            var x = values.Count > 1 ? index * step : width;
            return (x, height - BottomPadding - ratio * usable);
        }).ToArray();
    }

    /// <summary>Index of the sample nearest a pointer position.</summary>
    public static int NearestIndex(double x, double width, int count)
    {
        if (count <= 1 || width <= 0)
            return Math.Max(count - 1, 0);
        return (int)Math.Clamp(Math.Round(x / width * (count - 1)), 0, count - 1);
    }
}
