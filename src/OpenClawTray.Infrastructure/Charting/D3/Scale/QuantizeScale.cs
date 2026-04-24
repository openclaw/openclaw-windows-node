// Port of d3-scale/src/quantize.js + quantile.js + threshold.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A quantize scale maps a continuous domain to a discrete range by dividing
/// the domain into uniform segments. Direct port of d3.scaleQuantize().
/// </summary>
public sealed class QuantizeScale
{
    private double _x0 = 0, _x1 = 1;
    private double[] _range = [0, 1];

    public QuantizeScale() { }

    /// <summary>Maps a continuous domain value to a discrete range value.</summary>
    public double Map(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        double t = Math.Max(0, Math.Min(1, (_x1 - _x0) != 0 ? (x - _x0) / (_x1 - _x0) : 0));
        int i = (int)Math.Floor(t * _range.Length);
        return _range[Math.Clamp(i, 0, _range.Length - 1)];
    }

    /// <summary>Returns the extent of domain values that map to the given range value.</summary>
    public (double x0, double x1) InvertExtent(double y)
    {
        int i = Array.IndexOf(_range, y);
        if (i < 0) return (double.NaN, double.NaN);
        int n = _range.Length;
        double step = (_x1 - _x0) / n;
        return (_x0 + i * step, _x0 + (i + 1) * step);
    }

    public double[] Domain
    {
        get => [_x0, _x1];
        set { _x0 = value[0]; _x1 = value[1]; }
    }

    public double[] Range
    {
        get => [.. _range];
        set => _range = [.. value];
    }

    public QuantizeScale SetDomain(double x0, double x1) { _x0 = x0; _x1 = x1; return this; }
    public QuantizeScale SetRange(params double[] range) { Range = range; return this; }

    /// <summary>Returns the computed thresholds between range segments.</summary>
    public double[] Thresholds()
    {
        int n = _range.Length;
        var t = new double[n - 1];
        double step = (_x1 - _x0) / n;
        for (int i = 0; i < n - 1; i++)
            t[i] = _x0 + (i + 1) * step;
        return t;
    }

    public QuantizeScale Copy()
    {
        return new QuantizeScale { _x0 = _x0, _x1 = _x1, _range = [.. _range] };
    }
}

/// <summary>
/// A quantile scale maps a sampled domain to a discrete range using quantiles.
/// Direct port of d3.scaleQuantile().
/// </summary>
public sealed class QuantileScale
{
    private double[] _domain = [];
    private double[] _range = [];
    private double[] _thresholds = [];

    public QuantileScale() { }

    /// <summary>Maps a domain value to a range value based on quantile thresholds.</summary>
    public double Map(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        int i = D3Bisect.BisectRight(_thresholds, x);
        return _range.Length > 0 ? _range[Math.Min(i, _range.Length - 1)] : double.NaN;
    }

    /// <summary>Returns the extent of domain values that map to the given range value.</summary>
    public (double x0, double x1) InvertExtent(double y)
    {
        if (_domain.Length == 0) return (double.NaN, double.NaN);
        int i = Array.IndexOf(_range, y);
        if (i < 0) return (double.NaN, double.NaN);
        double x0 = i > 0 ? _thresholds[i - 1] : _domain[0];
        double x1 = i < _thresholds.Length ? _thresholds[i] : _domain[^1];
        return (x0, x1);
    }

    public double[] Domain
    {
        get => [.. _domain];
        set { _domain = value.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray(); Rescale(); }
    }

    public double[] Range
    {
        get => [.. _range];
        set { _range = [.. value]; Rescale(); }
    }

    /// <summary>Returns the computed quantile thresholds.</summary>
    public double[] Quantiles() => [.. _thresholds];

    public QuantileScale SetDomain(params double[] domain) { Domain = domain; return this; }
    public QuantileScale SetRange(params double[] range) { Range = range; return this; }

    public QuantileScale Copy()
    {
        return new QuantileScale
        {
            _domain = [.. _domain],
            _range = [.. _range],
            _thresholds = [.. _thresholds],
        };
    }

    private void Rescale()
    {
        int n = Math.Max(1, _range.Length);
        _thresholds = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            _thresholds[i] = D3Statistics.QuantileSorted(_domain, (double)(i + 1) / n);
        }
    }
}

/// <summary>
/// A threshold scale maps arbitrary domain thresholds to a discrete range.
/// Direct port of d3.scaleThreshold().
/// </summary>
public sealed class ThresholdScale
{
    private double[] _domain = [0.5];
    private double[] _range = [0, 1];

    public ThresholdScale() { }

    /// <summary>Maps a value to a range value based on which threshold interval it falls in.</summary>
    public double Map(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        int i = D3Bisect.BisectRight(_domain, x);
        return i < _range.Length ? _range[i] : _range[^1];
    }

    /// <summary>Returns the extent of domain values that map to the given range value.</summary>
    public (double x0, double x1) InvertExtent(double y)
    {
        int i = Array.IndexOf(_range, y);
        if (i < 0) return (double.NaN, double.NaN);
        double x0 = i > 0 ? _domain[i - 1] : double.NegativeInfinity;
        double x1 = i < _domain.Length ? _domain[i] : double.PositiveInfinity;
        return (x0, x1);
    }

    public double[] Domain
    {
        get => [.. _domain];
        set => _domain = [.. value];
    }

    public double[] Range
    {
        get => [.. _range];
        set => _range = [.. value];
    }

    public ThresholdScale SetDomain(params double[] domain) { Domain = domain; return this; }
    public ThresholdScale SetRange(params double[] range) { Range = range; return this; }

    public ThresholdScale Copy()
    {
        return new ThresholdScale { _domain = [.. _domain], _range = [.. _range] };
    }
}
