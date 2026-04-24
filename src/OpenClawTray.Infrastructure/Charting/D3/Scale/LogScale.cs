// Port of d3-scale/src/log.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A continuous logarithmic scale. Maps a domain value to a range value using a log transform.
/// Direct port of d3.scaleLog().
/// </summary>
public sealed class LogScale
{
    private double[] _domain = [1, 10];
    private double[] _range = [0, 1];
    private double _base = 10;
    private bool _clamp;

    private Func<double, double>? _output;
    private Func<double, double>? _input;

    public LogScale() { }

    public LogScale(double[] domain, double[] range)
    {
        _domain = [.. domain];
        _range = [.. range];
    }

    /// <summary>Maps a domain value to a range value using log transform.</summary>
    public double Map(double x)
    {
        if (double.IsNaN(x) || x <= 0) return double.NaN;
        _output ??= BuildPiecewise(_domain, _range, true);
        double clamped = _clamp ? Clamp(x, _domain[0], _domain[^1]) : x;
        return _output(clamped);
    }

    /// <summary>Maps a range value back to a domain value (inverse).</summary>
    public double Invert(double y)
    {
        if (_input == null)
        {
            double r0 = _range[0], r1 = _range[^1];
            double ld0 = Log(_domain[0]), ld1 = Log(_domain[^1]);
            double rDelta = r1 - r0;
            Func<double, double> normalize = rDelta == 0 ? _ => 0.5 : v => (v - r0) / rDelta;
            _input = v => Pow(ld0 + normalize(v) * (ld1 - ld0));
        }
        double raw = _input(y);
        return _clamp ? Clamp(raw, _domain[0], _domain[^1]) : raw;
    }

    public double[] Domain
    {
        get => [.. _domain];
        set { _domain = [.. value]; Rescale(); }
    }

    public double[] Range
    {
        get => [.. _range];
        set { _range = [.. value]; Rescale(); }
    }

    public double Base
    {
        get => _base;
        set { _base = value; Rescale(); }
    }

    public bool Clamped
    {
        get => _clamp;
        set { _clamp = value; Rescale(); }
    }

    public LogScale SetDomain(params double[] domain) { Domain = domain; return this; }
    public LogScale SetRange(params double[] range) { Range = range; return this; }
    public LogScale SetBase(double b) { Base = b; return this; }
    public LogScale SetClamp(bool clamp) { Clamped = clamp; return this; }

    /// <summary>Returns approximately count representative values from the domain.</summary>
    public double[] Ticks(int count = 10)
    {
        double d0 = _domain[0], d1 = _domain[^1];
        bool reverse = d1 < d0;
        if (reverse) (d0, d1) = (d1, d0);

        int i = (int)Math.Floor(Log(d0));
        int j = (int)Math.Ceiling(Log(d1));
        var ticks = new List<double>();

        if (_base % 1 == 0)
        {
            int b = (int)_base;
            for (int p = i; p <= j; p++)
            {
                for (int k = 1; k < b; k++)
                {
                    double t = p < 0 ? k / Math.Pow(_base, -p) : k * Math.Pow(_base, p);
                    if (t >= d0 && t <= d1) ticks.Add(t);
                }
            }
            double last = j < 0 ? 1.0 / Math.Pow(_base, -j) : Math.Pow(_base, j);
            if (last >= d0 && last <= d1) ticks.Add(last);
        }
        else
        {
            for (int p = i; p <= j; p++)
            {
                double t = Math.Pow(_base, p);
                if (t >= d0 && t <= d1) ticks.Add(t);
            }
        }

        if (reverse) ticks.Reverse();
        return ticks.ToArray();
    }

    /// <summary>Extends the domain to nice powers of the base.</summary>
    public LogScale Nice()
    {
        var d = Domain;
        d[0] = Math.Pow(_base, Math.Floor(Log(d[0])));
        d[^1] = Math.Pow(_base, Math.Ceiling(Log(d[^1])));
        Domain = d;
        return this;
    }

    public LogScale Copy()
    {
        return new LogScale
        {
            _domain = [.. _domain],
            _range = [.. _range],
            _base = _base,
            _clamp = _clamp,
        };
    }

    private double Log(double x) => Math.Log(x) / Math.Log(_base);
    private double Pow(double x) => Math.Pow(_base, x);

    private void Rescale()
    {
        _output = null;
        _input = null;
    }

    private Func<double, double> BuildPiecewise(double[] domain, double[] range, bool isLog)
    {
        double d0 = domain[0], d1 = domain[^1];
        double r0 = range[0], r1 = range[^1];

        Func<double, double> normalize;
        if (isLog)
        {
            double ld0 = Log(d0), ld1 = Log(d1);
            double delta = ld1 - ld0;
            normalize = delta == 0 ? _ => 0.5 : x => (Log(x) - ld0) / delta;
        }
        else
        {
            double delta = d1 - d0;
            normalize = delta == 0 ? _ => 0.5 : x => (x - d0) / delta;
        }

        return x => r0 + normalize(x) * (r1 - r0);
    }

    private static double Clamp(double x, double a, double b)
    {
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        return Math.Max(lo, Math.Min(hi, x));
    }
}
