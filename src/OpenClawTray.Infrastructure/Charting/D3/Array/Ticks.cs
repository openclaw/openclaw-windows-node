// Port of d3-array/src/ticks.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Generates human-readable tick values for numeric scales.
/// Direct port of d3-array's tick generation algorithm.
/// </summary>
public static class D3Ticks
{
    private static readonly double E10 = Math.Sqrt(50);
    private static readonly double E5 = Math.Sqrt(10);
    private static readonly double E2 = Math.Sqrt(2);

    internal static (long i1, long i2, long inc) TickSpec(double start, double stop, int count)
    {
        double step = (stop - start) / Math.Max(0, count);
        double power = Math.Floor(Math.Log10(step));
        double error = step / Math.Pow(10, power);
        double factor = error >= E10 ? 10 : error >= E5 ? 5 : error >= E2 ? 2 : 1;

        long i1, i2, inc;
        if (power < 0)
        {
            inc = (long)(Math.Pow(10, -power) / factor);
            i1 = (long)Math.Round(start * inc);
            i2 = (long)Math.Round(stop * inc);
            if ((double)i1 / inc < start) ++i1;
            if ((double)i2 / inc > stop) --i2;
            inc = -inc;
        }
        else
        {
            inc = (long)(Math.Pow(10, power) * factor);
            i1 = (long)Math.Round(start / inc);
            i2 = (long)Math.Round(stop / inc);
            if (i1 * inc < start) ++i1;
            if (i2 * inc > stop) --i2;
        }

        if (i2 < i1 && 0.5 <= count && count < 2)
            return TickSpec(start, stop, count * 2);

        return (i1, i2, inc);
    }

    /// <summary>
    /// Returns an array of approximately <paramref name="count"/> representative values
    /// from the given numeric interval [<paramref name="start"/>, <paramref name="stop"/>].
    /// The returned tick values are uniformly-spaced, have human-readable values
    /// (such as multiples of powers of 10), and are guaranteed to be within the extent
    /// of the given interval.
    /// </summary>
    public static double[] Ticks(double start, double stop, int count)
    {
        if (count <= 0) return [];
        if (start == stop) return [start];

        bool reverse = stop < start;
        var (i1, i2, inc) = reverse ? TickSpec(stop, start, count) : TickSpec(start, stop, count);

        if (i2 < i1) return [];

        int n = (int)(i2 - i1 + 1);
        var ticks = new double[n];

        if (reverse)
        {
            if (inc < 0)
                for (int i = 0; i < n; ++i) ticks[i] = (double)(i2 - i) / -inc;
            else
                for (int i = 0; i < n; ++i) ticks[i] = (double)(i2 - i) * inc;
        }
        else
        {
            if (inc < 0)
                for (int i = 0; i < n; ++i) ticks[i] = (double)(i1 + i) / -inc;
            else
                for (int i = 0; i < n; ++i) ticks[i] = (double)(i1 + i) * inc;
        }

        return ticks;
    }

    /// <summary>
    /// Returns the tick increment for the given interval and count.
    /// A negative value indicates that ticks should be computed as division rather than multiplication.
    /// </summary>
    public static long TickIncrement(double start, double stop, int count)
    {
        return TickSpec(start, stop, count).inc;
    }

    /// <summary>
    /// Returns the tick step size for the given interval and count.
    /// </summary>
    public static double TickStep(double start, double stop, int count)
    {
        bool reverse = stop < start;
        long inc = reverse ? TickIncrement(stop, start, count) : TickIncrement(start, stop, count);
        return (reverse ? -1 : 1) * (inc < 0 ? 1.0 / -inc : inc);
    }
}
