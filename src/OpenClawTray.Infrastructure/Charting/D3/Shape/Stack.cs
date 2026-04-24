// Port of d3-shape/src/stack.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes a stacked layout from tabular data.
/// Direct port of d3.stack().
/// </summary>
public sealed class StackGenerator<T>
{
    private string[] _keys = [];
    private Func<T, string, double> _value = (_, _) => 0;

    /// <summary>Computes the stacked series from the given data.</summary>
    public StackSeries[] Generate(IReadOnlyList<T> data)
    {
        int n = _keys.Length;
        var series = new StackSeries[n];
        for (int i = 0; i < n; i++)
        {
            series[i] = new StackSeries { Key = _keys[i], Points = new StackPoint[data.Count] };
        }

        for (int j = 0; j < data.Count; j++)
        {
            for (int i = 0; i < n; i++)
            {
                series[i].Points[j] = new StackPoint(0, _value(data[j], _keys[i]));
            }
        }

        // Apply none offset: stack on top of each other
        for (int i = 1; i < n; i++)
        {
            for (int j = 0; j < data.Count; j++)
            {
                double prev = series[i - 1].Points[j].Y1;
                if (double.IsNaN(prev)) prev = series[i - 1].Points[j].Y0;
                series[i].Points[j] = new StackPoint(prev, prev + series[i].Points[j].Y1);
            }
        }

        return series;
    }

    public StackGenerator<T> SetKeys(params string[] keys) { _keys = keys; return this; }
    public StackGenerator<T> SetValue(Func<T, string, double> value) { _value = value; return this; }
}

public record struct StackPoint(double Y0, double Y1);

public sealed class StackSeries
{
    public required string Key { get; init; }
    public required StackPoint[] Points { get; set; }
}

public static class StackGenerator
{
    public static StackGenerator<T> Create<T>() => new();
}
