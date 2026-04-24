// Port of d3-scale/src/pow.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A continuous power scale. Maps domain values using a power transform (x^exponent).
/// Direct port of d3.scalePow() and d3.scaleSqrt().
/// </summary>
public sealed class PowScale
{
    private double[] _domain = [0, 1];
    private double[] _range = [0, 1];
    private double _exponent = 1;
    private bool _clamp;

    private Func<double, double>? _output;
    private Func<double, double>? _input;

    public PowScale() { }

    public PowScale(double exponent)
    {
        _exponent = exponent;
    }

    /// <summary>Maps a domain value to a range value using the power transform.</summary>
    public double Map(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        _output ??= BuildPiecewise(_domain, _range, true);
        double clamped = _clamp ? Clamp(x, _domain[0], _domain[^1]) : x;
        return _output(clamped);
    }

    /// <summary>Maps a range value back to a domain value.</summary>
    public double Invert(double y)
    {
        _input ??= BuildPiecewise(_range, _domain, false);
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

    public double Exponent
    {
        get => _exponent;
        set { _exponent = value; Rescale(); }
    }

    public bool Clamped
    {
        get => _clamp;
        set { _clamp = value; Rescale(); }
    }

    public PowScale SetDomain(params double[] domain) { Domain = domain; return this; }
    public PowScale SetRange(params double[] range) { Range = range; return this; }
    public PowScale SetExponent(double exp) { Exponent = exp; return this; }
    public PowScale SetClamp(bool clamp) { Clamped = clamp; return this; }

    /// <summary>Returns approximately count representative values from the domain.</summary>
    public double[] Ticks(int count = 10) => D3Ticks.Ticks(_domain[0], _domain[^1], count);

    /// <summary>Extends the domain to nice round values.</summary>
    public PowScale Nice(int count = 10)
    {
        // Delegate to linear nice on the transformed domain
        var linear = new LinearScale { Domain = _domain }.Nice(count);
        Domain = linear.Domain;
        return this;
    }

    public PowScale Copy()
    {
        return new PowScale
        {
            _domain = [.. _domain],
            _range = [.. _range],
            _exponent = _exponent,
            _clamp = _clamp,
        };
    }

    /// <summary>Creates a square root scale (exponent = 0.5).</summary>
    public static PowScale Sqrt() => new(0.5);

    private void Rescale()
    {
        _output = null;
        _input = null;
    }

    private Func<double, double> BuildPiecewise(double[] domain, double[] range, bool applyPow)
    {
        double d0 = domain[0], d1 = domain[^1];
        double r0 = range[0], r1 = range[^1];

        Func<double, double> normalize;
        if (applyPow)
        {
            double pd0 = PowTransform(d0), pd1 = PowTransform(d1);
            double delta = pd1 - pd0;
            normalize = delta == 0 ? _ => 0.5 : x => (PowTransform(x) - pd0) / delta;
        }
        else
        {
            double delta = d1 - d0;
            normalize = delta == 0 ? _ => 0.5 : x => (x - d0) / delta;
        }

        if (applyPow)
        {
            return x => r0 + normalize(x) * (r1 - r0);
        }
        else
        {
            // Invert: apply inverse power transform to result
            return x =>
            {
                double t = normalize(x);
                double linear = r0 + t * (r1 - r0);
                return InversePowTransform(linear);
            };
        }
    }

    private double PowTransform(double x)
    {
        return (_exponent == 1) ? x
            : (_exponent == 0.5) ? (x < 0 ? -Math.Sqrt(-x) : Math.Sqrt(x))
            : (x < 0 ? -Math.Pow(-x, _exponent) : Math.Pow(x, _exponent));
    }

    private double InversePowTransform(double x)
    {
        return (_exponent == 1) ? x
            : (_exponent == 0.5) ? (x < 0 ? -(x * x) : x * x)
            : (x < 0 ? -Math.Pow(-x, 1.0 / _exponent) : Math.Pow(x, 1.0 / _exponent));
    }

    private static double Clamp(double x, double a, double b)
    {
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        return Math.Max(lo, Math.Min(hi, x));
    }
}
