// Port of d3-array/src/bin.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Bins continuous data into discrete intervals (histogram).
/// Direct port of d3.bin().
/// </summary>
public sealed class BinGenerator<T>
{
    private Func<T, double> _value;
    private Func<double[], int, double[]>? _thresholds;
    private int _thresholdCount = 10;

    public BinGenerator(Func<T, double> value)
    {
        _value = value;
    }

    /// <summary>Generates bins for the given data.</summary>
    public Bin<T>[] Generate(IReadOnlyList<T> data)
    {
        var values = new double[data.Count];
        for (int i = 0; i < data.Count; i++)
            values[i] = _value(data[i]);

        // Compute domain
        var (min, max) = D3Extent.Extent(values);
        if (double.IsNaN(min)) return [];

        // Compute thresholds
        double[] tz = _thresholds != null
            ? _thresholds(values, _thresholdCount)
            : ThresholdSturges(min, max, _thresholdCount);

        // Ensure thresholds are within domain
        int m = tz.Length;
        while (m > 0 && tz[m - 1] >= max) --m;
        int start = 0;
        while (start < m && tz[start] <= min) ++start;

        // Create bins
        int binCount = m - start + 1;
        var bins = new Bin<T>[binCount];
        for (int i = 0; i < binCount; i++)
        {
            bins[i] = new Bin<T>
            {
                X0 = i > 0 ? tz[start + i - 1] : min,
                X1 = i < binCount - 1 ? tz[start + i] : max,
                Items = []
            };
        }

        // Assign items to bins
        for (int i = 0; i < data.Count; i++)
        {
            double v = values[i];
            if (double.IsNaN(v)) continue;
            int idx = D3Bisect.BisectRight(tz, v, start, m) - start;
            if (idx >= binCount) idx = binCount - 1;
            if (idx < 0) idx = 0;
            bins[idx].Items.Add(data[i]);
        }

        return bins;
    }

    public BinGenerator<T> SetValue(Func<T, double> value) { _value = value; return this; }
    public BinGenerator<T> SetThresholdCount(int count) { _thresholdCount = count; return this; }
    public BinGenerator<T> SetThresholds(Func<double[], int, double[]> thresholds) { _thresholds = thresholds; return this; }

    /// <summary>Sturges' formula for threshold count, then uniform thresholds.</summary>
    private static double[] ThresholdSturges(double min, double max, int count)
    {
        double step = D3Ticks.TickStep(min, max, count);
        if (step == 0) return [min];

        double start, stop;
        if (step > 0)
        {
            start = Math.Ceiling(min / step) * step;
            stop = Math.Floor(max / step) * step;
        }
        else
        {
            step = -step;
            start = Math.Ceiling(min * step) / step;
            stop = Math.Floor(max * step) / step;
        }

        int n = Math.Max(0, (int)(Math.Round((stop - start) / step)) + 1);
        var thresholds = new double[n];
        for (int i = 0; i < n; i++)
            thresholds[i] = start + i * step;
        return thresholds;
    }
}

/// <summary>A single bin containing items within the [x0, x1) interval.</summary>
public sealed class Bin<T>
{
    public required double X0 { get; init; }
    public required double X1 { get; init; }
    public required List<T> Items { get; init; }
    public int Count => Items.Count;
}

public static class BinGenerator
{
    /// <summary>Creates a bin generator for numeric data.</summary>
    public static BinGenerator<double> Create() => new(v => v);

    /// <summary>Creates a bin generator with a custom value accessor.</summary>
    public static BinGenerator<T> Create<T>(Func<T, double> value) => new(value);
}
