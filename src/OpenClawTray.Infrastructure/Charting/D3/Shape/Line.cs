// Port of d3-shape/src/line.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Generates SVG path data for a line from an array of data points.
/// Direct port of d3.line().
/// </summary>
public sealed class LineGenerator<T>
{
    private Func<T, int, double> _x;
    private Func<T, int, double> _y;
    private Func<T, int, bool> _defined = (_, _) => true;
    private CurveFactory? _curve;
    private int? _digits = 3;

    public LineGenerator(Func<T, int, double> x, Func<T, int, double> y)
    {
        _x = x;
        _y = y;
    }

    /// <summary>Generates the SVG path string for the given data.</summary>
    public string? Generate(IReadOnlyList<T> data)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();
        int n = data.Count;

        if (_curve != null)
        {
            var curve = _curve(path);
            bool defined0 = false;
            for (int i = 0; i <= n; ++i)
            {
                bool isDefined = i < n && _defined(data[i], i);
                if (isDefined != defined0)
                {
                    defined0 = isDefined;
                    if (defined0) curve.LineStart();
                    else curve.LineEnd();
                }
                if (defined0)
                    curve.Point(_x(data[i], i), _y(data[i], i));
            }
        }
        else
        {
            bool defined0 = false;
            for (int i = 0; i <= n; ++i)
            {
                bool isDefined = i < n && _defined(data[i], i);
                if (isDefined != defined0)
                {
                    defined0 = isDefined;
                    if (defined0)
                        path.MoveTo(_x(data[i], i), _y(data[i], i));
                }
                else if (defined0)
                {
                    path.LineTo(_x(data[i], i), _y(data[i], i));
                }
            }
        }

        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    public LineGenerator<T> SetX(Func<T, int, double> x) { _x = x; return this; }
    public LineGenerator<T> SetY(Func<T, int, double> y) { _y = y; return this; }
    public LineGenerator<T> SetDefined(Func<T, int, bool> defined) { _defined = defined; return this; }
    public LineGenerator<T> SetCurve(CurveFactory? curve) { _curve = curve; return this; }
    public LineGenerator<T> SetDigits(int? digits) { _digits = digits; return this; }
    public int? Digits => _digits;
}

/// <summary>
/// Factory for creating line generators with common data types.
/// </summary>
public static class LineGenerator
{
    /// <summary>Creates a line generator for (x, y) tuple data.</summary>
    public static LineGenerator<(double x, double y)> Create()
        => new((d, _) => d.x, (d, _) => d.y);

    /// <summary>Creates a line generator for double[] data where [0]=x, [1]=y.</summary>
    public static LineGenerator<double[]> FromArrays()
        => new((d, _) => d[0], (d, _) => d[1]);

    /// <summary>Creates a line generator with custom accessors.</summary>
    public static LineGenerator<T> Create<T>(Func<T, double> x, Func<T, double> y)
        => new((d, _) => x(d), (d, _) => y(d));
}
