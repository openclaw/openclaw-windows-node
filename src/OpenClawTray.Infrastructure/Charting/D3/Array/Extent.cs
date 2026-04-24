// Port of d3-array/src/extent.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes the minimum and maximum values of an array.
/// </summary>
public static class D3Extent
{
    /// <summary>
    /// Returns the [min, max] of the given values.
    /// </summary>
    public static (double min, double max) Extent(IEnumerable<double> values)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        bool found = false;

        foreach (var value in values)
        {
            if (double.IsNaN(value)) continue;
            if (!found)
            {
                min = max = value;
                found = true;
            }
            else
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        return found ? (min, max) : (double.NaN, double.NaN);
    }

    /// <summary>
    /// Returns the [min, max] of the given values, applying a value accessor.
    /// </summary>
    public static (double min, double max) Extent<T>(IEnumerable<T> values, Func<T, double> valueOf)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        bool found = false;

        foreach (var item in values)
        {
            double value = valueOf(item);
            if (double.IsNaN(value)) continue;
            if (!found)
            {
                min = max = value;
                found = true;
            }
            else
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        return found ? (min, max) : (double.NaN, double.NaN);
    }
}
