// Port of d3-scale/src/linear.js + continuous.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A continuous linear scale that maps a numeric domain to a numeric range.
/// Direct port of d3.scaleLinear().
/// </summary>
public sealed class LinearScale
{
    private double[] _domain = [0, 1];
    private double[] _range = [0, 1];
    private bool _clamp;
    private double? _unknown;

    // Cached interpolation functions — invalidated on rescale
    private Func<double, double>? _output;
    private Func<double, double>? _input;

    public LinearScale() { }

    public LinearScale(double[] domain, double[] range)
    {
        _domain = [.. domain];
        _range = [.. range];
    }

    /// <summary>Maps a domain value to a range value.</summary>
    public double Map(double x)
    {
        if (double.IsNaN(x)) return _unknown ?? double.NaN;
        _output ??= BuildPiecewise(_domain, _range);
        double clamped = _clamp ? Clamp(x, _domain[0], _domain[^1]) : x;
        return _output(clamped);
    }

    /// <summary>Maps a range value back to a domain value (inverse).</summary>
    public double Invert(double y)
    {
        _input ??= BuildPiecewise(_range, _domain);
        double raw = _input(y);
        return _clamp ? Clamp(raw, _domain[0], _domain[^1]) : raw;
    }

    /// <summary>Gets or sets the scale domain.</summary>
    public double[] Domain
    {
        get => [.. _domain];
        set { _domain = [.. value]; Rescale(); }
    }

    /// <summary>Gets or sets the scale range.</summary>
    public double[] Range
    {
        get => [.. _range];
        set { _range = [.. value]; Rescale(); }
    }

    /// <summary>Gets or sets whether the scale clamps values to the domain.</summary>
    public bool Clamped
    {
        get => _clamp;
        set { _clamp = value; Rescale(); }
    }

    /// <summary>Gets or sets the value returned for undefined/NaN inputs.</summary>
    public double? Unknown
    {
        get => _unknown;
        set => _unknown = value;
    }

    // Fluent API mirrors D3's chained methods

    public LinearScale SetDomain(params double[] domain) { Domain = domain; return this; }
    public LinearScale SetRange(params double[] range) { Range = range; return this; }
    public LinearScale SetClamp(bool clamp) { Clamped = clamp; return this; }
    public LinearScale SetUnknown(double? unknown) { Unknown = unknown; return this; }

    /// <summary>
    /// Extends the domain to nice round values.
    /// Port of linearish.nice().
    /// </summary>
    public LinearScale Nice(int count = 10)
    {
        var d = Domain;
        int i0 = 0;
        int i1 = d.Length - 1;
        double start = d[i0];
        double stop = d[i1];

        if (stop < start)
        {
            (start, stop) = (stop, start);
            (i0, i1) = (i1, i0);
        }

        int maxIter = 10;
        long prestep = 0;
        while (maxIter-- > 0)
        {
            long step = D3Ticks.TickIncrement(start, stop, count);
            if (step == prestep)
            {
                d[i0] = start;
                d[i1] = stop;
                Domain = d;
                return this;
            }
            else if (step > 0)
            {
                start = Math.Floor(start / step) * step;
                stop = Math.Ceiling(stop / step) * step;
            }
            else if (step < 0)
            {
                start = Math.Ceiling(start * step) / step;
                stop = Math.Floor(stop * step) / step;
            }
            else
            {
                break;
            }
            prestep = step;
        }

        return this;
    }

    /// <summary>
    /// Returns approximately <paramref name="count"/> representative values from the domain.
    /// </summary>
    public double[] Ticks(int count = 10)
    {
        var d = _domain;
        return D3Ticks.Ticks(d[0], d[^1], count);
    }

    /// <summary>Creates a copy of this scale.</summary>
    public LinearScale Copy()
    {
        return new LinearScale
        {
            _domain = [.. _domain],
            _range = [.. _range],
            _clamp = _clamp,
            _unknown = _unknown,
        };
    }

    private void Rescale()
    {
        _output = null;
        _input = null;
    }

    private static Func<double, double> BuildPiecewise(double[] domain, double[] range)
    {
        int n = Math.Min(domain.Length, range.Length);
        if (n == 2) return Bimap(domain, range);
        return Polymap(domain, range);
    }

    private static Func<double, double> Bimap(double[] domain, double[] range)
    {
        double d0 = domain[0], d1 = domain[1];
        double r0 = range[0], r1 = range[1];

        Func<double, double> normalize;
        Func<double, double> interpolate;

        if (d1 < d0)
        {
            normalize = Normalize(d1, d0);
            interpolate = D3Interpolate.Number(r1, r0);
        }
        else
        {
            normalize = Normalize(d0, d1);
            interpolate = D3Interpolate.Number(r0, r1);
        }

        return x => interpolate(normalize(x));
    }

    private static Func<double, double> Polymap(double[] domain, double[] range)
    {
        int j = Math.Min(domain.Length, range.Length) - 1;
        var d = new Func<double, double>[j];
        var r = new Func<double, double>[j];

        double[] dom = [.. domain];
        double[] ran = [.. range];

        if (dom[j] < dom[0])
        {
            Array.Reverse(dom);
            Array.Reverse(ran);
        }

        for (int i = 0; i < j; i++)
        {
            d[i] = Normalize(dom[i], dom[i + 1]);
            r[i] = D3Interpolate.Number(ran[i], ran[i + 1]);
        }

        return x =>
        {
            int i = D3Bisect.BisectRight(dom, x, 1, j) - 1;
            return r[i](d[i](x));
        };
    }

    private static Func<double, double> Normalize(double a, double b)
    {
        double delta = b - a;
        if (delta == 0) return _ => double.IsNaN(delta) ? double.NaN : 0.5;
        return x => (x - a) / delta;
    }

    private static double Clamp(double x, double a, double b)
    {
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        return Math.Max(lo, Math.Min(hi, x));
    }
}
