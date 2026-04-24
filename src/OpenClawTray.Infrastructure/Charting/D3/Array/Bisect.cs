// Port of d3-array/src/bisect.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Binary search utilities for sorted arrays.
/// Direct port of d3-array's bisector.
/// </summary>
public static class D3Bisect
{
    /// <summary>
    /// Returns the insertion point for <paramref name="x"/> in <paramref name="array"/>
    /// to maintain sorted order. If <paramref name="x"/> is already present, inserts after
    /// (to the right of) any existing entries.
    /// </summary>
    public static int BisectRight(double[] array, double x, int lo = 0, int hi = -1)
    {
        if (hi < 0) hi = array.Length;
        if (double.IsNaN(x)) return hi;
        while (lo < hi)
        {
            int mid = (int)((uint)(lo + hi) >> 1);
            if (array[mid] <= x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Returns the insertion point for <paramref name="x"/> in <paramref name="array"/>
    /// to maintain sorted order. If <paramref name="x"/> is already present, inserts before
    /// (to the left of) any existing entries.
    /// </summary>
    public static int BisectLeft(double[] array, double x, int lo = 0, int hi = -1)
    {
        if (hi < 0) hi = array.Length;
        if (double.IsNaN(x)) return hi;
        while (lo < hi)
        {
            int mid = (int)((uint)(lo + hi) >> 1);
            if (array[mid] < x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Returns the index of the value closest to <paramref name="x"/> in a sorted <paramref name="array"/>.
    /// </summary>
    public static int BisectCenter(double[] array, double x, int lo = 0, int hi = -1)
    {
        if (array.Length == 0) return 0;
        if (hi < 0) hi = array.Length;
        int i = BisectLeft(array, x, lo, hi - 1);
        return i > lo && i < array.Length && x - array[i - 1] < array[i] - x ? i - 1 : i;
    }
}
