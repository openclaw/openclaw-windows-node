// Port of d3-array/src/range.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Generates a range of evenly-spaced numeric values.
/// Direct port of d3.range().
/// </summary>
public static class D3Range
{
    /// <summary>
    /// Returns an array of evenly-spaced values from <paramref name="start"/> (inclusive)
    /// to <paramref name="stop"/> (exclusive) with the given <paramref name="step"/>.
    /// </summary>
    public static double[] Range(double start, double stop, double step = 1)
    {
        if (step == 0) return [];
        int n = Math.Max(0, (int)Math.Ceiling((stop - start) / step));
        var range = new double[n];
        for (int i = 0; i < n; i++)
        {
            range[i] = start + i * step;
        }
        return range;
    }

    /// <summary>
    /// Returns an array of integers from 0 (inclusive) to <paramref name="stop"/> (exclusive).
    /// </summary>
    public static double[] Range(double stop)
    {
        return Range(0, stop, 1);
    }
}
