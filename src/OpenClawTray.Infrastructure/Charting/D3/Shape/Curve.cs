// Port of d3-shape/src/curve/ — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Curves transform a sequence of points into a path.
/// This is the interface all curve implementations share.
/// </summary>
public interface ICurve
{
    void LineStart();
    void LineEnd();
    void Point(double x, double y);
}

/// <summary>
/// Curve factory that creates curve instances bound to a PathBuilder.
/// </summary>
public delegate ICurve CurveFactory(PathBuilder path);

/// <summary>
/// Built-in curve implementations matching d3-shape curves.
/// </summary>
public static class D3Curve
{
    /// <summary>Straight line segments between points. Port of d3.curveLinear.</summary>
    public static readonly CurveFactory Linear = path => new CurveLinear(path);

    /// <summary>Step function (midpoint). Port of d3.curveStep.</summary>
    public static readonly CurveFactory Step = path => new CurveStep(path, 0.5);

    /// <summary>Step function (change at start). Port of d3.curveStepBefore.</summary>
    public static readonly CurveFactory StepBefore = path => new CurveStep(path, 0.0);

    /// <summary>Step function (change at end). Port of d3.curveStepAfter.</summary>
    public static readonly CurveFactory StepAfter = path => new CurveStep(path, 1.0);

    /// <summary>Uniform B-spline. Port of d3.curveBasis.</summary>
    public static readonly CurveFactory Basis = path => new CurveBasis(path);

    /// <summary>Closed uniform B-spline. Port of d3.curveBasisClosed.</summary>
    public static readonly CurveFactory BasisClosed = path => new CurveBasisClosed(path);

    /// <summary>Natural cubic spline. Port of d3.curveNatural.</summary>
    public static readonly CurveFactory Natural = path => new CurveNatural(path);

    /// <summary>Cardinal spline with default tension. Port of d3.curveCardinal.</summary>
    public static readonly CurveFactory Cardinal = CardinalWithTension(0);

    /// <summary>Creates a cardinal spline with the given tension [0, 1].</summary>
    public static CurveFactory CardinalWithTension(double tension)
    {
        return path => new CurveCardinal(path, tension);
    }

    /// <summary>Catmull-Rom spline with default alpha. Port of d3.curveCatmullRom.</summary>
    public static readonly CurveFactory CatmullRom = CatmullRomWithAlpha(0.5);

    /// <summary>Creates a Catmull-Rom spline with the given alpha [0, 1].</summary>
    public static CurveFactory CatmullRomWithAlpha(double alpha)
    {
        return path => new CurveCatmullRom(path, alpha);
    }

    /// <summary>Monotone cubic interpolation in x. Port of d3.curveMonotoneX.</summary>
    public static readonly CurveFactory MonotoneX = path => new CurveMonotoneX(path);
}

// ─── curveLinear ────────────────────────────────────────────────────────

internal sealed class CurveLinear : ICurve
{
    private readonly PathBuilder _path;
    private int _point;

    public CurveLinear(PathBuilder path) { _path = path; }

    public void LineStart() { _point = 0; }
    public void LineEnd() { }

    public void Point(double x, double y)
    {
        if (_point++ == 0) _path.MoveTo(x, y);
        else _path.LineTo(x, y);
    }
}

// ─── curveStep ──────────────────────────────────────────────────────────

internal sealed class CurveStep : ICurve
{
    private readonly PathBuilder _path;
    private readonly double _t;
    private int _point;
    private double _x, _y;

    public CurveStep(PathBuilder path, double t) { _path = path; _t = t; }

    public void LineStart() { _point = 0; }
    public void LineEnd()
    {
        if (_point == 1) _path.LineTo(_x, _y);
    }

    public void Point(double x, double y)
    {
        switch (_point)
        {
            case 0:
                _point = 1;
                _path.MoveTo(x, y);
                break;
            default:
                _point = 2;
                double xm = _x + (_t == 0 ? 0 : _t == 1 ? (x - _x) : (x - _x) * _t);
                _path.LineTo(xm, _y);
                _path.LineTo(xm, y);
                break;
        }
        _x = x;
        _y = y;
    }
}

// ─── curveBasis ─────────────────────────────────────────────────────────

internal sealed class CurveBasis : ICurve
{
    private readonly PathBuilder _path;
    private int _point;
    private double _x0, _y0, _x1, _y1;

    public CurveBasis(PathBuilder path) { _path = path; }

    public void LineStart() { _point = 0; }

    public void LineEnd()
    {
        switch (_point)
        {
            case 2: _path.LineTo((_x0 + 2 * _x1) / 3, (_y0 + 2 * _y1) / 3); break;
            case 3: BasisPoint(_x1, _y1); _path.LineTo((_x0 + 2 * _x1) / 3, (_y0 + 2 * _y1) / 3); break;
        }
    }

    public void Point(double x, double y)
    {
        switch (_point)
        {
            case 0: _point = 1; break;
            case 1: _point = 2; _path.MoveTo((5 * _x0 + x) / 6, (5 * _y0 + y) / 6); break;
            case 2: _point = 3; goto default;
            default: BasisPoint(x, y); break;
        }
        _x0 = _x1; _y0 = _y1;
        _x1 = x; _y1 = y;
    }

    private void BasisPoint(double x, double y)
    {
        _path.BezierCurveTo(
            (2 * _x0 + _x1) / 3, (2 * _y0 + _y1) / 3,
            (_x0 + 2 * _x1) / 3, (_y0 + 2 * _y1) / 3,
            (_x0 + 4 * _x1 + x) / 6, (_y0 + 4 * _y1 + y) / 6);
    }
}

// ─── curveBasisClosed ───────────────────────────────────────────────────

internal sealed class CurveBasisClosed : ICurve
{
    private readonly PathBuilder _path;
    private int _point;
    private double _x0, _y0, _x1, _y1, _x2, _y2, _x3, _y3, _x4, _y4;

    public CurveBasisClosed(PathBuilder path) { _path = path; }

    public void LineStart() { _point = 0; }

    public void LineEnd()
    {
        switch (_point)
        {
            case 1:
                _path.MoveTo(_x2, _y2);
                _path.ClosePath();
                break;
            case 2:
                _path.LineTo(_x2, _y2);
                _path.ClosePath();
                break;
            case 3:
                Point(_x2, _y2);
                Point(_x3, _y3);
                Point(_x4, _y4);
                break;
        }
    }

    public void Point(double x, double y)
    {
        switch (_point)
        {
            case 0: _point = 1; _x2 = x; _y2 = y; break;
            case 1: _point = 2; _x3 = x; _y3 = y; break;
            case 2: _point = 3; _x4 = x; _y4 = y;
                _path.MoveTo((_x0 + 4 * _x1 + x) / 6, (_y0 + 4 * _y1 + y) / 6);
                break;
            default:
                _path.BezierCurveTo(
                    (2 * _x0 + _x1) / 3, (2 * _y0 + _y1) / 3,
                    (_x0 + 2 * _x1) / 3, (_y0 + 2 * _y1) / 3,
                    (_x0 + 4 * _x1 + x) / 6, (_y0 + 4 * _y1 + y) / 6);
                break;
        }
        _x0 = _x1; _y0 = _y1;
        _x1 = x; _y1 = y;
    }
}

// ─── curveNatural ───────────────────────────────────────────────────────

internal sealed class CurveNatural : ICurve
{
    private readonly PathBuilder _path;
    private readonly List<double> _xs = [];
    private readonly List<double> _ys = [];

    public CurveNatural(PathBuilder path) { _path = path; }

    public void LineStart()
    {
        _xs.Clear();
        _ys.Clear();
    }

    public void LineEnd()
    {
        int n = _xs.Count;
        if (n < 1) return;
        if (n == 1) { _path.MoveTo(_xs[0], _ys[0]); return; }
        if (n == 2) { _path.MoveTo(_xs[0], _ys[0]); _path.LineTo(_xs[1], _ys[1]); return; }

        var px = ControlPoints(_xs);
        var py = ControlPoints(_ys);

        _path.MoveTo(_xs[0], _ys[0]);
        for (int i = 0; i < n - 1; i++)
        {
            _path.BezierCurveTo(px[0][i], py[0][i], px[1][i], py[1][i], _xs[i + 1], _ys[i + 1]);
        }
    }

    public void Point(double x, double y)
    {
        _xs.Add(x);
        _ys.Add(y);
    }

    /// <summary>
    /// Computes control points for a natural cubic spline.
    /// Returns [cp1[], cp2[]] arrays of length n-1.
    /// </summary>
    private static double[][] ControlPoints(List<double> x)
    {
        int n = x.Count - 1;
        var a = new double[n];
        var b = new double[n];
        var r = new double[n];

        a[0] = 0;
        b[0] = 2;
        r[0] = x[0] + 2 * x[1];

        for (int i = 1; i < n - 1; i++)
        {
            a[i] = 1;
            b[i] = 4;
            r[i] = 4 * x[i] + 2 * x[i + 1];
        }

        a[n - 1] = 2;
        b[n - 1] = 7;
        r[n - 1] = 8 * x[n - 1] + x[n];

        // Thomas algorithm (tridiagonal solve)
        for (int i = 1; i < n; i++)
        {
            double m = a[i] / b[i - 1];
            b[i] -= m;
            r[i] -= m * r[i - 1];
        }

        var p1 = new double[n];
        p1[n - 1] = r[n - 1] / b[n - 1];
        for (int i = n - 2; i >= 0; i--)
            p1[i] = (r[i] - p1[i + 1]) / b[i];

        var p2 = new double[n];
        for (int i = 0; i < n - 1; i++)
            p2[i] = 2 * x[i + 1] - p1[i + 1];
        p2[n - 1] = (x[n] + p1[n - 1]) / 2;

        return [p1, p2];
    }
}

// ─── curveCardinal ──────────────────────────────────────────────────────

internal sealed class CurveCardinal : ICurve
{
    private readonly PathBuilder _path;
    private readonly double _k; // (1 - tension) / 6
    private int _point;
    private double _x0, _y0, _x1, _y1, _x2, _y2;

    public CurveCardinal(PathBuilder path, double tension)
    {
        _path = path;
        _k = (1 - tension) / 6;
    }

    public void LineStart() { _point = 0; }

    public void LineEnd()
    {
        if (_point == 2) _path.LineTo(_x2, _y2);
    }

    public void Point(double x, double y)
    {
        switch (_point)
        {
            case 0: _point = 1; break;
            case 1: _point = 2; _path.MoveTo(_x1, _y1); break;
            case 2: _point = 3; goto default;
            default:
                _path.BezierCurveTo(
                    _x1 + _k * (_x2 - _x0), _y1 + _k * (_y2 - _y0),
                    _x2 - _k * (x - _x1), _y2 - _k * (y - _y1),
                    _x2, _y2);
                break;
        }
        _x0 = _x1; _y0 = _y1;
        _x1 = _x2; _y1 = _y2;
        _x2 = x; _y2 = y;
    }
}

// ─── curveCatmullRom ────────────────────────────────────────────────────

internal sealed class CurveCatmullRom : ICurve
{
    private readonly PathBuilder _path;
    private readonly double _alpha;
    private int _point;
    private double _x0, _y0, _x1, _y1, _x2, _y2;
    private double _l01_a, _l12_a, _l01_2a, _l12_2a;

    public CurveCatmullRom(PathBuilder path, double alpha)
    {
        _path = path;
        _alpha = alpha;
    }

    public void LineStart() { _point = 0; _l01_a = _l12_a = _l01_2a = _l12_2a = 0; }

    public void LineEnd()
    {
        if (_point == 2) _path.LineTo(_x2, _y2);
    }

    public void Point(double x, double y)
    {
        if (_point > 0)
        {
            double dx = x - _x2, dy = y - _y2;
            _l01_a = _l12_a;
            _l01_2a = _l12_2a;
            double l12 = Math.Sqrt(dx * dx + dy * dy);
            _l12_a = Math.Pow(l12, _alpha);
            _l12_2a = Math.Pow(l12, 2 * _alpha);
        }

        switch (_point)
        {
            case 0: _point = 1; break;
            case 1: _point = 2; _path.MoveTo(_x1, _y1); break;
            case 2: _point = 3; goto default;
            default:
                CatmullRomPoint(x, y);
                break;
        }
        _x0 = _x1; _y0 = _y1;
        _x1 = _x2; _y1 = _y2;
        _x2 = x; _y2 = y;
    }

    private void CatmullRomPoint(double x, double y)
    {
        if (_l01_a <= 0 || _l12_a <= 0)
        {
            _path.LineTo(_x2, _y2);
            return;
        }

        double a = 2 * _l01_2a + 3 * _l01_a * _l12_a + _l12_2a;
        double b = 2 * _l12_2a + 3 * _l12_a * _l01_a + _l01_2a;
        double n = 3 * _l01_a * (_l01_a + _l12_a);
        double m = 3 * _l12_a * (_l12_a + _l01_a);

        double cp1x = n > 0 ? (a * _x1 - _l12_2a * _x0 + _l01_2a * _x2) / n : _x1;
        double cp1y = n > 0 ? (a * _y1 - _l12_2a * _y0 + _l01_2a * _y2) / n : _y1;
        double cp2x = m > 0 ? (b * _x2 + _l01_2a * x - _l12_2a * _x1) / m : _x2;
        double cp2y = m > 0 ? (b * _y2 + _l01_2a * y - _l12_2a * _y1) / m : _y2;

        _path.BezierCurveTo(cp1x, cp1y, cp2x, cp2y, _x2, _y2);
    }
}

// ─── curveMonotoneX ─────────────────────────────────────────────────────

internal sealed class CurveMonotoneX : ICurve
{
    private readonly PathBuilder _path;
    private int _point;
    private double _x0, _y0, _x1, _y1;
    private double _t0;

    public CurveMonotoneX(PathBuilder path) { _path = path; }

    public void LineStart() { _point = 0; }

    public void LineEnd()
    {
        switch (_point)
        {
            case 2: _path.LineTo(_x1, _y1); break;
            case 3: MonotonePoint(_t0); break;
        }
    }

    public void Point(double x, double y)
    {
        double t1 = 0;
        if (x == _x1 && y == _y1) return; // skip coincident points
        switch (_point)
        {
            case 0:
                _point = 1;
                _path.MoveTo(x, y);
                break;
            case 1:
                _point = 2;
                break;
            case 2:
                _point = 3;
                double dx2 = x - _x0;
                t1 = dx2 != 0 ? (3 * (_y1 - _y0) / dx2 + Slope2(_x0, _y0, _x1, _y1, x, y)) / 2 : 0;
                MonotonePoint(t1);
                break;
            default:
                t1 = Slope3(_x0, _y0, _x1, _y1, x, y);
                MonotonePoint(t1);
                break;
        }
        _x0 = _x1; _y0 = _y1;
        _x1 = x; _y1 = y;
        _t0 = t1;
    }

    private void MonotonePoint(double t1)
    {
        double dx = (_x1 - _x0) / 3;
        _path.BezierCurveTo(_x0 + dx, _y0 + dx * _t0, _x1 - dx, _y1 - dx * t1, _x1, _y1);
    }

    private static double Sign(double x) => x < 0 ? -1 : 1;
    private static double Slope2(double x0, double y0, double x1, double y1, double x2, double y2)
    {
        double dx0 = x1 - x0, dy0 = y1 - y0;
        double dx1 = x2 - x1, dy1 = y2 - y1;
        double s0 = dx0 != 0 ? dy0 / dx0 : 0;
        double s1 = dx1 != 0 ? dy1 / dx1 : 0;
        double p = (s0 * dx1 + s1 * dx0) / (dx0 + dx1);
        return (Sign(s0) + Sign(s1)) * Math.Min(Math.Abs(s0), Math.Min(Math.Abs(s1), 0.5 * Math.Abs(p)));
    }

    private static double Slope3(double x0, double y0, double x1, double y1, double x2, double y2)
    {
        double h0 = x1 - x0, h1 = x2 - x1;
        double s0 = h0 != 0 ? (y1 - y0) / h0 : 0;
        double s1 = h1 != 0 ? (y2 - y1) / h1 : 0;
        double p = (s0 * h1 + s1 * h0) / (h0 + h1);
        return (Sign(s0) + Sign(s1)) * Math.Min(Math.Abs(s0), Math.Min(Math.Abs(s1), 0.5 * Math.Abs(p)));
    }
}
