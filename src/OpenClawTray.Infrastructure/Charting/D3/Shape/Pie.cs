// Port of d3-shape/src/pie.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes arc angles from data values, suitable for feeding into an ArcGenerator.
/// Direct port of d3.pie().
/// </summary>
public sealed class PieGenerator<T>
{
    private Func<T, int, double> _value;
    private Comparison<double>? _sortValues = (a, b) => b.CompareTo(a); // descending by default
    private Comparison<T>? _sort;
    private double _startAngle = 0;
    private double _endAngle = 2 * Math.PI;
    private double _padAngle = 0;

    public PieGenerator(Func<T, int, double> value)
    {
        _value = value;
    }

    /// <summary>Computes pie arc descriptors for the given data.</summary>
    public PieArc<T>[] Generate(IReadOnlyList<T> data)
    {
        int n = data.Count;
        double sum = 0;
        var index = new int[n];
        var values = new double[n];
        var arcs = new PieArc<T>[n];

        double a0 = _startAngle;
        double da = Math.Min(2 * Math.PI, Math.Max(-2 * Math.PI, _endAngle - a0));
        double p = Math.Min(Math.Abs(da) / n, _padAngle);
        double pa = p * (da < 0 ? -1 : 1);

        for (int i = 0; i < n; ++i)
        {
            index[i] = i;
            double v = _value(data[i], i);
            values[i] = v;
            if (v > 0) sum += v;
        }

        // Sort
        if (_sortValues != null)
        {
            Array.Sort(index, (a, b) => _sortValues(values[a], values[b]));
        }
        else if (_sort != null)
        {
            Array.Sort(index, (a, b) => _sort(data[a], data[b]));
        }

        double k = sum > 0 ? (da - n * pa) / sum : 0;

        for (int i = 0; i < n; ++i)
        {
            int j = index[i];
            double v = values[j];
            double a1 = a0 + (v > 0 ? v * k : 0) + pa;
            arcs[j] = new PieArc<T>(data[j], v, i, a0, a1, p);
            a0 = a1;
        }

        return arcs;
    }

    public PieGenerator<T> SetValue(Func<T, int, double> value) { _value = value; return this; }
    public PieGenerator<T> SetSortValues(Comparison<double>? sortValues) { _sortValues = sortValues; _sort = null; return this; }
    public PieGenerator<T> SetSort(Comparison<T>? sort) { _sort = sort; _sortValues = null; return this; }
    public PieGenerator<T> SetStartAngle(double angle) { _startAngle = angle; return this; }
    public PieGenerator<T> SetEndAngle(double angle) { _endAngle = angle; return this; }
    public PieGenerator<T> SetPadAngle(double angle) { _padAngle = angle; return this; }
}

/// <summary>Describes a single arc slice computed by PieGenerator.</summary>
public readonly record struct PieArc<T>(T Data, double Value, int Index, double StartAngle, double EndAngle, double PadAngle);

public static class PieGenerator
{
    /// <summary>Creates a pie generator for numeric data.</summary>
    public static PieGenerator<double> Create()
        => new((d, _) => d);

    /// <summary>Creates a pie generator with a custom value accessor.</summary>
    public static PieGenerator<T> Create<T>(Func<T, double> value)
        => new((d, _) => value(d));

    /// <summary>One-shot convenience: computes pie arcs directly without constructing and configuring a generator instance.</summary>
    public static PieArc<T>[] Generate<T>(IReadOnlyList<T> data, Func<T, double> value,
        bool sort = true, double padAngle = 0)
    {
        var gen = Create(value);
        if (!sort) gen.SetSortValues(null);
        if (padAngle != 0) gen.SetPadAngle(padAngle);
        return gen.Generate(data);
    }
}
