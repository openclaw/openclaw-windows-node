// Port of d3-shape/src/arc.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Generates SVG path data for circular or annular arcs.
/// Direct port of d3.arc(). Works with PieArc data from PieGenerator.
/// </summary>
public sealed class ArcGenerator
{
    private double _innerRadius = 0;
    private double _outerRadius = 100;
    private double _cornerRadius = 0;
    private int? _digits = 3;

    private const double Epsilon = 1e-12;
    private const double Pi = Math.PI;
    private const double HalfPi = Pi / 2;
    private const double Tau = 2 * Pi;

    /// <summary>Generates the SVG path string for the given arc descriptor.</summary>
    public string? Generate<T>(PieArc<T> arc) =>
        Generate(arc.StartAngle, arc.EndAngle, arc.PadAngle, _innerRadius, _outerRadius);

    /// <summary>Generates the SVG path string for explicit angle parameters.</summary>
    public string? Generate(double startAngle, double endAngle, double padAngle = 0,
        double? innerRadius = null, double? outerRadius = null)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();

        double r0 = innerRadius ?? _innerRadius;
        double r1 = outerRadius ?? _outerRadius;
        double a0 = startAngle - HalfPi;
        double a1 = endAngle - HalfPi;
        double da = Math.Abs(a1 - a0);
        bool cw = a1 > a0;

        // Ensure r1 >= r0
        if (r1 < r0) (r0, r1) = (r1, r0);

        if (!(r1 > Epsilon))
        {
            // Degenerate — just a point
            path.MoveTo(0, 0);
        }
        else if (da > Tau - Epsilon)
        {
            // Full circle (or nearly)
            path.MoveTo(r1 * Math.Cos(a0), r1 * Math.Sin(a0));
            path.Arc(0, 0, r1, a0, a1, !cw);
            if (r0 > Epsilon)
            {
                path.MoveTo(r0 * Math.Cos(a1), r0 * Math.Sin(a1));
                path.Arc(0, 0, r0, a1, a0, cw);
            }
        }
        else
        {
            double a01 = a0, a11 = a1;
            double da1 = da;
            double rc = Math.Min(Math.Abs(r1 - r0) / 2, _cornerRadius);

            // Pad angle handling
            double ap = padAngle / 2;
            if (ap > Epsilon)
            {
                double rp = Math.Sqrt(r0 * r0 + r1 * r1);
                double p1 = SafeAsin(rp / r1 * Math.Sin(ap));
                if ((da1 -= p1 * 2) > Epsilon)
                {
                    p1 *= (cw ? 1 : -1);
                    a01 += p1;
                    a11 -= p1;
                }
                else
                {
                    da1 = 0;
                    a01 = a11 = (a0 + a1) / 2;
                }
            }

            double x01 = r1 * Math.Cos(a01);
            double y01 = r1 * Math.Sin(a01);
            double x10 = r0 * Math.Cos(a11 /* should be a10 but simplified */);
            double y10 = r0 * Math.Sin(a11);

            if (!(da1 > Epsilon))
            {
                path.MoveTo(x01, y01);
            }
            else if (rc > Epsilon)
            {
                // Corner radius path — full d3 corner tangent computation not yet ported
                throw new NotImplementedException(
                    "Corner radius on arcs is not yet implemented. Set corner radius to 0 or omit it.");
            }
            else
            {
                path.MoveTo(x01, y01);
                path.Arc(0, 0, r1, a01, a11, !cw);
            }

            // Inner arc (bottom)
            if (r0 > Epsilon)
            {
                path.Arc(0, 0, r0, a11, a01, cw);
            }
            else
            {
                path.LineTo(0, 0);
            }
        }

        path.ClosePath();
        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    /// <summary>
    /// Computes the centroid of an arc — the midpoint of the inner and outer radii,
    /// and the midpoint of the start and end angles.
    /// </summary>
    public (double x, double y) Centroid(double startAngle, double endAngle,
        double? innerRadius = null, double? outerRadius = null)
    {
        double r = ((innerRadius ?? _innerRadius) + (outerRadius ?? _outerRadius)) / 2;
        double a = (startAngle + endAngle) / 2 - Pi / 2;
        return (Math.Cos(a) * r, Math.Sin(a) * r);
    }

    public ArcGenerator SetInnerRadius(double r) { _innerRadius = r; return this; }
    public ArcGenerator SetOuterRadius(double r) { _outerRadius = r; return this; }
    public ArcGenerator SetCornerRadius(double r) { _cornerRadius = r; return this; }
    public ArcGenerator SetDigits(int? digits) { _digits = digits; return this; }

    /// <summary>
    /// Static convenience: computes the centroid of an arc without constructing an instance.
    /// Returns the midpoint of the inner/outer radii at the midpoint angle.
    /// </summary>
    public static (double x, double y) Centroid(double startAngle, double endAngle,
        double innerRadius = 0, double outerRadius = 100)
    {
        double r = (innerRadius + outerRadius) / 2;
        double a = (startAngle + endAngle) / 2 - Pi / 2;
        return (Math.Cos(a) * r, Math.Sin(a) * r);
    }

    private static double SafeAsin(double x) =>
        x >= 1 ? HalfPi : x <= -1 ? -HalfPi : Math.Asin(x);
}
