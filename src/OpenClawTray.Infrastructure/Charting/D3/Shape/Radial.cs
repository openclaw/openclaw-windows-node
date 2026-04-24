// Port of d3-shape radial variants — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Generates SVG path data for a radial line (polar coordinates).
/// Direct port of d3.lineRadial(). Maps angle → x, radius → y.
/// </summary>
public sealed class RadialLineGenerator<T>
{
    private Func<T, int, double> _angle;
    private Func<T, int, double> _radius;
    private Func<T, int, bool> _defined = (_, _) => true;
    private CurveFactory? _curve;
    private int? _digits = 3;

    public RadialLineGenerator(Func<T, int, double> angle, Func<T, int, double> radius)
    {
        _angle = angle;
        _radius = radius;
    }

    /// <summary>Generates the SVG path string for the given data in polar coordinates.</summary>
    public string? Generate(IReadOnlyList<T> data)
    {
        // Convert polar to Cartesian and delegate to a line generator
        var cartesian = new List<(double x, double y)>();
        var definedMask = new List<bool>();

        for (int i = 0; i < data.Count; i++)
        {
            bool isDefined = _defined(data[i], i);
            definedMask.Add(isDefined);
            if (isDefined)
            {
                double a = _angle(data[i], i) - Math.PI / 2;
                double r = _radius(data[i], i);
                cartesian.Add((Math.Cos(a) * r, Math.Sin(a) * r));
            }
            else
            {
                cartesian.Add((0, 0));
            }
        }

        var line = new LineGenerator<(double x, double y)>(
            (d, _) => d.x, (d, _) => d.y)
            .SetDefined((_, i) => definedMask[i])
            .SetDigits(_digits);

        if (_curve != null)
            line.SetCurve(_curve);

        return line.Generate(cartesian);
    }

    public RadialLineGenerator<T> SetAngle(Func<T, int, double> angle) { _angle = angle; return this; }
    public RadialLineGenerator<T> SetRadius(Func<T, int, double> radius) { _radius = radius; return this; }
    public RadialLineGenerator<T> SetDefined(Func<T, int, bool> defined) { _defined = defined; return this; }
    public RadialLineGenerator<T> SetCurve(CurveFactory? curve) { _curve = curve; return this; }
    public RadialLineGenerator<T> SetDigits(int? digits) { _digits = digits; return this; }
}

/// <summary>
/// Generates SVG path data for a radial area (polar coordinates).
/// Direct port of d3.areaRadial(). Maps angle → x, innerRadius/outerRadius → y.
/// </summary>
public sealed class RadialAreaGenerator<T>
{
    private Func<T, int, double> _angle0;
    private Func<T, int, double>? _angle1;
    private Func<T, int, double> _innerRadius;
    private Func<T, int, double> _outerRadius;
    private Func<T, int, bool> _defined = (_, _) => true;
    private int? _digits = 3;

    public RadialAreaGenerator(
        Func<T, int, double> angle,
        Func<T, int, double> innerRadius,
        Func<T, int, double> outerRadius)
    {
        _angle0 = angle;
        _innerRadius = innerRadius;
        _outerRadius = outerRadius;
    }

    /// <summary>Generates the SVG path string for the given data in polar coordinates.</summary>
    public string? Generate(IReadOnlyList<T> data)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();
        int n = data.Count;

        // Build top line (outer radius, forward)
        var topPoints = new List<(double x, double y)>();
        var bottomPoints = new List<(double x, double y)>();
        var definedRanges = new List<(int start, int end)>();

        int? rangeStart = null;
        for (int i = 0; i <= n; i++)
        {
            bool isDefined = i < n && _defined(data[i], i);
            if (isDefined && rangeStart == null)
                rangeStart = i;
            else if (!isDefined && rangeStart != null)
            {
                definedRanges.Add((rangeStart.Value, i));
                rangeStart = null;
            }
        }

        foreach (var (start, end) in definedRanges)
        {
            // Forward pass: outer radius
            for (int i = start; i < end; i++)
            {
                double a = (_angle1 != null ? _angle1(data[i], i) : _angle0(data[i], i)) - Math.PI / 2;
                double r = _outerRadius(data[i], i);
                double x = Math.Cos(a) * r, y = Math.Sin(a) * r;
                if (i == start) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }
            // Backward pass: inner radius
            for (int i = end - 1; i >= start; i--)
            {
                double a = _angle0(data[i], i) - Math.PI / 2;
                double r = _innerRadius(data[i], i);
                path.LineTo(Math.Cos(a) * r, Math.Sin(a) * r);
            }
            path.ClosePath();
        }

        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    public RadialAreaGenerator<T> SetAngle(Func<T, int, double> angle) { _angle0 = angle; _angle1 = null; return this; }
    public RadialAreaGenerator<T> SetStartAngle(Func<T, int, double> angle) { _angle0 = angle; return this; }
    public RadialAreaGenerator<T> SetEndAngle(Func<T, int, double>? angle) { _angle1 = angle; return this; }
    public RadialAreaGenerator<T> SetInnerRadius(Func<T, int, double> r) { _innerRadius = r; return this; }
    public RadialAreaGenerator<T> SetOuterRadius(Func<T, int, double> r) { _outerRadius = r; return this; }
    public RadialAreaGenerator<T> SetDefined(Func<T, int, bool> defined) { _defined = defined; return this; }
    public RadialAreaGenerator<T> SetDigits(int? digits) { _digits = digits; return this; }
}

/// <summary>
/// Generates SVG path data for a radial link (polar coordinates).
/// Direct port of d3.linkRadial(). Useful for radial tree/dendrogram edges.
/// </summary>
public sealed class RadialLinkGenerator<T>
{
    private Func<T, (double angle, double radius)> _source;
    private Func<T, (double angle, double radius)> _target;
    private int? _digits = 3;

    public RadialLinkGenerator(
        Func<T, (double angle, double radius)> source,
        Func<T, (double angle, double radius)> target)
    {
        _source = source;
        _target = target;
    }

    /// <summary>Generates the SVG path string for the given link data.</summary>
    public string? Generate(T datum)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();
        var s = _source(datum);
        var t = _target(datum);

        // Convert polar to Cartesian
        double sa = s.angle - Math.PI / 2, sr = s.radius;
        double ta = t.angle - Math.PI / 2, tr = t.radius;

        double sx = Math.Cos(sa) * sr, sy = Math.Sin(sa) * sr;
        double tx = Math.Cos(ta) * tr, ty = Math.Sin(ta) * tr;

        // Radial link uses a cubic bezier through the origin's radius
        double mr = (sr + tr) / 2;
        double ma = (sa + ta) / 2;
        double cx = Math.Cos(ma) * mr, cy = Math.Sin(ma) * mr;

        path.MoveTo(sx, sy);
        path.QuadraticCurveTo(cx, cy, tx, ty);

        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    public RadialLinkGenerator<T> SetSource(Func<T, (double angle, double radius)> source) { _source = source; return this; }
    public RadialLinkGenerator<T> SetTarget(Func<T, (double angle, double radius)> target) { _target = target; return this; }
    public RadialLinkGenerator<T> SetDigits(int? digits) { _digits = digits; return this; }
}

public static class RadialLineGenerator
{
    /// <summary>Creates a radial line generator for (angle, radius) tuple data.</summary>
    public static RadialLineGenerator<(double angle, double radius)> Create()
        => new((d, _) => d.angle, (d, _) => d.radius);

    /// <summary>Creates a radial line generator with custom accessors.</summary>
    public static RadialLineGenerator<T> Create<T>(Func<T, double> angle, Func<T, double> radius)
        => new((d, _) => angle(d), (d, _) => radius(d));
}

public static class RadialAreaGenerator
{
    /// <summary>Creates a radial area generator with custom accessors.</summary>
    public static RadialAreaGenerator<T> Create<T>(
        Func<T, double> angle, Func<T, double> innerRadius, Func<T, double> outerRadius)
        => new((d, _) => angle(d), (d, _) => innerRadius(d), (d, _) => outerRadius(d));
}

public static class RadialLinkGenerator
{
    /// <summary>Creates a radial link generator with custom accessors.</summary>
    public static RadialLinkGenerator<(TNode source, TNode target)> Create<TNode>(
        Func<TNode, double> angle, Func<TNode, double> radius)
        => new(
            d => (angle(d.source), radius(d.source)),
            d => (angle(d.target), radius(d.target)));
}
