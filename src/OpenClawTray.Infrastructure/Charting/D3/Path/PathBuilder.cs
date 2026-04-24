// Port of d3-path/src/path.js — ISC License, Copyright 2010-2023 Mike Bostock

using System.Globalization;
using System.Text;

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Builds SVG path data strings. Direct port of d3-path's Path class.
/// This is the fundamental rendering primitive — all D3 shapes produce path strings
/// through this builder.
/// </summary>
public sealed class PathBuilder
{
    private readonly StringBuilder _sb = new();
    private readonly int? _digits;
    private double? _x0, _y0; // start of current subpath
    private double? _x1, _y1; // current point

    private const double Pi = Math.PI;
    private const double Tau = 2 * Pi;
    private const double Epsilon = 1e-6;
    private const double TauEpsilon = Tau - Epsilon;

    public PathBuilder(int? digits = null)
    {
        _digits = digits;
    }

    private string F(double v)
    {
        if (_digits is int d)
        {
            double k = Math.Pow(10, d);
            return (Math.Round(v * k) / k).ToString(CultureInfo.InvariantCulture);
        }
        return v.ToString(CultureInfo.InvariantCulture);
    }

    public PathBuilder MoveTo(double x, double y)
    {
        _x0 = _x1 = x;
        _y0 = _y1 = y;
        _sb.Append($"M{F(x)},{F(y)}");
        return this;
    }

    public PathBuilder ClosePath()
    {
        if (_x1 is not null)
        {
            _x1 = _x0;
            _y1 = _y0;
            _sb.Append('Z');
        }
        return this;
    }

    public PathBuilder LineTo(double x, double y)
    {
        _x1 = x;
        _y1 = y;
        _sb.Append($"L{F(x)},{F(y)}");
        return this;
    }

    public PathBuilder QuadraticCurveTo(double x1, double y1, double x, double y)
    {
        _x1 = x;
        _y1 = y;
        _sb.Append($"Q{F(x1)},{F(y1)},{F(x)},{F(y)}");
        return this;
    }

    public PathBuilder BezierCurveTo(double x1, double y1, double x2, double y2, double x, double y)
    {
        _x1 = x;
        _y1 = y;
        _sb.Append($"C{F(x1)},{F(y1)},{F(x2)},{F(y2)},{F(x)},{F(y)}");
        return this;
    }

    public PathBuilder ArcTo(double x1, double y1, double x2, double y2, double r)
    {
        if (r < 0) throw new ArgumentException($"Negative radius: {r}", nameof(r));

        double x0 = _x1 ?? 0;
        double y0 = _y1 ?? 0;
        double x21 = x2 - x1, y21 = y2 - y1;
        double x01 = x0 - x1, y01 = y0 - y1;
        double l01_2 = x01 * x01 + y01 * y01;

        if (_x1 is null)
        {
            // Path is empty — move to (x1, y1)
            _x1 = x1; _y1 = y1;
            _sb.Append($"M{F(x1)},{F(y1)}");
        }
        else if (!(l01_2 > Epsilon))
        {
            // Current point coincides with (x1, y1) — skip
        }
        else if (!(Math.Abs(y01 * x21 - y21 * x01) > Epsilon) || r == 0)
        {
            // Collinear or zero radius — draw line to (x1, y1)
            _x1 = x1; _y1 = y1;
            _sb.Append($"L{F(x1)},{F(y1)}");
        }
        else
        {
            double x20 = x2 - x0, y20 = y2 - y0;
            double l21_2 = x21 * x21 + y21 * y21;
            double l20_2 = x20 * x20 + y20 * y20;
            double l21 = Math.Sqrt(l21_2);
            double l01 = Math.Sqrt(l01_2);
            double l = r * Math.Tan((Pi - Math.Acos((l21_2 + l01_2 - l20_2) / (2 * l21 * l01))) / 2);
            double t01 = l / l01;
            double t21 = l / l21;

            if (Math.Abs(t01 - 1) > Epsilon)
            {
                _sb.Append($"L{F(x1 + t01 * x01)},{F(y1 + t01 * y01)}");
            }

            double endX = x1 + t21 * x21;
            double endY = y1 + t21 * y21;
            _x1 = endX; _y1 = endY;
            int sweep = y01 * x20 > x01 * y20 ? 1 : 0;
            _sb.Append($"A{F(r)},{F(r)},0,0,{sweep},{F(endX)},{F(endY)}");
        }
        return this;
    }

    public PathBuilder Arc(double x, double y, double r, double a0, double a1, bool ccw = false)
    {
        if (r < 0) throw new ArgumentException($"Negative radius: {r}", nameof(r));

        double dx = r * Math.Cos(a0);
        double dy = r * Math.Sin(a0);
        double x0 = x + dx;
        double y0 = y + dy;
        int cw = ccw ? 0 : 1;
        double da = ccw ? a0 - a1 : a1 - a0;

        if (_x1 is null)
        {
            _sb.Append($"M{F(x0)},{F(y0)}");
        }
        else if (Math.Abs(_x1.Value - x0) > Epsilon || Math.Abs(_y1!.Value - y0) > Epsilon)
        {
            _sb.Append($"L{F(x0)},{F(y0)}");
        }

        if (r == 0) return this;

        if (da < 0) da = da % Tau + Tau;

        if (da > TauEpsilon)
        {
            // Full circle — two arcs
            double mx = x - dx, my = y - dy;
            _sb.Append($"A{F(r)},{F(r)},0,1,{cw},{F(mx)},{F(my)}A{F(r)},{F(r)},0,1,{cw},{F(x0)},{F(y0)}");
            _x1 = x0; _y1 = y0;
        }
        else if (da > Epsilon)
        {
            double endX = x + r * Math.Cos(a1);
            double endY = y + r * Math.Sin(a1);
            int largeArc = da >= Pi ? 1 : 0;
            _sb.Append($"A{F(r)},{F(r)},0,{largeArc},{cw},{F(endX)},{F(endY)}");
            _x1 = endX; _y1 = endY;
        }

        return this;
    }

    public PathBuilder Rect(double x, double y, double w, double h)
    {
        _x0 = _x1 = x;
        _y0 = _y1 = y;
        _sb.Append($"M{F(x)},{F(y)}h{F(w)}v{F(h)}h{F(-w)}Z");
        return this;
    }

    public override string ToString() => _sb.ToString();

    /// <summary>Creates a new PathBuilder with default (no rounding) settings.</summary>
    public static PathBuilder Create() => new();

    /// <summary>Creates a new PathBuilder that rounds to the given number of digits.</summary>
    public static PathBuilder CreateRound(int digits = 3) => new(digits);
}
