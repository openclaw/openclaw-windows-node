// Port of d3-shape/src/area.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Generates SVG path data for an area shape from an array of data points.
/// Direct port of d3.area().
/// </summary>
public sealed class AreaGenerator<T>
{
    private Func<T, int, double> _x0;
    private Func<T, int, double>? _x1;
    private Func<T, int, double> _y0;
    private Func<T, int, double> _y1;
    private Func<T, int, bool> _defined = (_, _) => true;
    private int? _digits = 3;

    public AreaGenerator(
        Func<T, int, double> x0,
        Func<T, int, double> y0,
        Func<T, int, double> y1)
    {
        _x0 = x0;
        _y0 = y0;
        _y1 = y1;
    }

    /// <summary>Generates the SVG path string for the given data.</summary>
    public string? Generate(IReadOnlyList<T> data)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();
        int n = data.Count;
        bool defined0 = false;
        int j = 0;

        var x0z = new double[n];
        var y0z = new double[n];

        for (int i = 0; i <= n; ++i)
        {
            bool isDefined = i < n && _defined(data[i], i);
            if (isDefined != defined0)
            {
                defined0 = isDefined;
                if (defined0)
                {
                    j = i;
                    // Top line: start
                    double xi = _x1 != null ? _x1(data[i], i) : _x0(data[i], i);
                    path.MoveTo(xi, _y1(data[i], i));
                    x0z[i] = _x0(data[i], i);
                    y0z[i] = _y0(data[i], i);
                }
                else
                {
                    // Bottom line: reverse
                    for (int k = i - 1; k >= j; --k)
                    {
                        path.LineTo(x0z[k], y0z[k]);
                    }
                    path.ClosePath();
                }
            }
            else if (defined0)
            {
                x0z[i] = _x0(data[i], i);
                y0z[i] = _y0(data[i], i);
                double xi = _x1 != null ? _x1(data[i], i) : x0z[i];
                path.LineTo(xi, _y1(data[i], i));
            }
        }

        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    public AreaGenerator<T> SetX(Func<T, int, double> x) { _x0 = x; _x1 = null; return this; }
    public AreaGenerator<T> SetX0(Func<T, int, double> x0) { _x0 = x0; return this; }
    public AreaGenerator<T> SetX1(Func<T, int, double>? x1) { _x1 = x1; return this; }
    public AreaGenerator<T> SetY0(Func<T, int, double> y0) { _y0 = y0; return this; }
    public AreaGenerator<T> SetY1(Func<T, int, double> y1) { _y1 = y1; return this; }
    public AreaGenerator<T> SetDefined(Func<T, int, bool> defined) { _defined = defined; return this; }
    public AreaGenerator<T> SetDigits(int? digits) { _digits = digits; return this; }
}

public static class AreaGenerator
{
    /// <summary>Creates an area generator for double[] data where [0]=x, [1]=y.</summary>
    public static AreaGenerator<double[]> FromArrays()
        => new((d, _) => d[0], (_, _) => 0, (d, _) => d[1]);

    /// <summary>Creates an area generator with custom accessors.</summary>
    public static AreaGenerator<T> Create<T>(Func<T, double> x, Func<T, double> y0, Func<T, double> y1)
        => new((d, _) => x(d), (d, _) => y0(d), (d, _) => y1(d));

    /// <summary>Creates an area generator with baseline at 0.</summary>
    public static AreaGenerator<T> Create<T>(Func<T, double> x, Func<T, double> y)
        => new((d, _) => x(d), (_, _) => 0, (d, _) => y(d));
}
