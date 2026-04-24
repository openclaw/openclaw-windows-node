// Port of d3-ease — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Easing functions for smooth animation transitions.
/// Each method takes t in [0, 1] and returns an eased value.
/// Direct port of d3-ease.
/// </summary>
public static class D3Ease
{
    // ─── Linear ─────────────────────────────────────────────────────────

    /// <summary>Linear easing (identity). Port of d3.easeLinear.</summary>
    public static double Linear(double t) => t;

    // ─── Quad ───────────────────────────────────────────────────────────

    /// <summary>Quadratic easing in. Port of d3.easeQuadIn.</summary>
    public static double QuadIn(double t) => t * t;

    /// <summary>Quadratic easing out. Port of d3.easeQuadOut.</summary>
    public static double QuadOut(double t) => t * (2 - t);

    /// <summary>Quadratic easing in-out. Port of d3.easeQuad.</summary>
    public static double Quad(double t) =>
        ((t *= 2) <= 1 ? t * t : --t * (2 - t) + 1) / 2;

    // ─── Cubic ──────────────────────────────────────────────────────────

    /// <summary>Cubic easing in. Port of d3.easeCubicIn.</summary>
    public static double CubicIn(double t) => t * t * t;

    /// <summary>Cubic easing out. Port of d3.easeCubicOut.</summary>
    public static double CubicOut(double t) => --t * t * t + 1;

    /// <summary>Cubic easing in-out. Port of d3.easeCubic.</summary>
    public static double Cubic(double t) =>
        ((t *= 2) <= 1 ? t * t * t : (t -= 2) * t * t + 2) / 2;

    // ─── Polynomial (generic power) ────────────────────────────────────

    /// <summary>Creates polynomial easing in with the given exponent.</summary>
    public static Func<double, double> PolyIn(double e = 3) =>
        t => Math.Pow(t, e);

    /// <summary>Creates polynomial easing out with the given exponent.</summary>
    public static Func<double, double> PolyOut(double e = 3) =>
        t => 1 - Math.Pow(1 - t, e);

    /// <summary>Creates polynomial easing in-out with the given exponent.</summary>
    public static Func<double, double> Poly(double e = 3) =>
        t => ((t *= 2) <= 1 ? Math.Pow(t, e) : 2 - Math.Pow(2 - t, e)) / 2;

    // ─── Sin ────────────────────────────────────────────────────────────

    private static readonly double HalfPi = Math.PI / 2;

    /// <summary>Sinusoidal easing in. Port of d3.easeSinIn.</summary>
    public static double SinIn(double t) => t == 1 ? 1 : 1 - Math.Cos(t * HalfPi);

    /// <summary>Sinusoidal easing out. Port of d3.easeSinOut.</summary>
    public static double SinOut(double t) => Math.Sin(t * HalfPi);

    /// <summary>Sinusoidal easing in-out. Port of d3.easeSin.</summary>
    public static double Sin(double t) => (1 - Math.Cos(Math.PI * t)) / 2;

    // ─── Exp ────────────────────────────────────────────────────────────

    /// <summary>Exponential easing in. Port of d3.easeExpIn.</summary>
    public static double ExpIn(double t) => Tpmt(1 - t);

    /// <summary>Exponential easing out. Port of d3.easeExpOut.</summary>
    public static double ExpOut(double t) => 1 - Tpmt(t);

    /// <summary>Exponential easing in-out. Port of d3.easeExp.</summary>
    public static double Exp(double t) =>
        ((t *= 2) <= 1 ? Tpmt(1 - t) : 2 - Tpmt(t - 1)) / 2;

    // ─── Circle ─────────────────────────────────────────────────────────

    /// <summary>Circular easing in. Port of d3.easeCircleIn.</summary>
    public static double CircleIn(double t) => 1 - Math.Sqrt(1 - t * t);

    /// <summary>Circular easing out. Port of d3.easeCircleOut.</summary>
    public static double CircleOut(double t) => Math.Sqrt(1 - --t * t);

    /// <summary>Circular easing in-out. Port of d3.easeCircle.</summary>
    public static double Circle(double t) =>
        ((t *= 2) <= 1 ? 1 - Math.Sqrt(1 - t * t) : Math.Sqrt(1 - (t -= 2) * t) + 1) / 2;

    // ─── Elastic ────────────────────────────────────────────────────────

    private static readonly double Tau = 2 * Math.PI;

    /// <summary>Creates elastic easing in. Port of d3.easeElasticIn.</summary>
    public static Func<double, double> ElasticIn(double amplitude = 1, double period = 0.3)
    {
        double s = Math.Asin(1 / (amplitude = Math.Max(1, amplitude))) * (period /= Tau);
        return t => amplitude * Tpmt(-(--t)) * Math.Sin((s - t) / period);
    }

    /// <summary>Creates elastic easing out. Port of d3.easeElasticOut.</summary>
    public static Func<double, double> ElasticOut(double amplitude = 1, double period = 0.3)
    {
        double s = Math.Asin(1 / (amplitude = Math.Max(1, amplitude))) * (period /= Tau);
        return t => 1 - amplitude * Tpmt(t = +t) * Math.Sin((t + s) / period);
    }

    /// <summary>Creates elastic easing in-out. Port of d3.easeElastic.</summary>
    public static Func<double, double> Elastic(double amplitude = 1, double period = 0.3)
    {
        double s = Math.Asin(1 / (amplitude = Math.Max(1, amplitude))) * (period /= Tau);
        return t => ((t = t * 2 - 1) < 0
            ? amplitude * Tpmt(-t) * Math.Sin((s - t) / period)
            : 2 - amplitude * Tpmt(t) * Math.Sin((s + t) / period)) / 2;
    }

    // ─── Back ───────────────────────────────────────────────────────────

    private const double S_Default = 1.70158;

    /// <summary>Creates back easing in (overshooting). Port of d3.easeBackIn.</summary>
    public static Func<double, double> BackIn(double s = S_Default) =>
        t =>
        {
            double ts = (t = +t) * t;
            return ts * (t * (s + 1) - s);
        };

    /// <summary>Creates back easing out. Port of d3.easeBackOut.</summary>
    public static Func<double, double> BackOut(double s = S_Default) =>
        t =>
        {
            double ts = (t = t - 1) * t;
            return ts * (t * (s + 1) + s) + 1;
        };

    /// <summary>Creates back easing in-out. Port of d3.easeBack.</summary>
    public static Func<double, double> Back(double s = S_Default) =>
        t => ((t *= 2) < 1
            ? t * t * ((s + 1) * t - s)
            : (t -= 2) * t * ((s + 1) * t + s) + 2) / 2;

    // ─── Bounce ─────────────────────────────────────────────────────────

    private const double B1 = 4.0 / 11;
    private const double B2 = 6.0 / 11;
    private const double B3 = 8.0 / 11;
    private const double B4 = 3.0 / 4;
    private const double B5 = 9.0 / 11;
    private const double B6 = 10.0 / 11;
    private const double B7 = 15.0 / 16;
    private const double B8 = 21.0 / 22;
    private const double B9 = 63.0 / 64;
    private const double B0 = 1.0 / B1 / B1;

    /// <summary>Bounce easing in. Port of d3.easeBounceIn.</summary>
    public static double BounceIn(double t) => 1 - BounceOut(1 - t);

    /// <summary>Bounce easing out. Port of d3.easeBounceOut.</summary>
    public static double BounceOut(double t)
    {
        return (t = +t) < B1 ? B0 * t * t
            : t < B3 ? B0 * (t -= B2) * t + B4
            : t < B6 ? B0 * (t -= B5) * t + B7
            : B0 * (t -= B8) * t + B9;
    }

    /// <summary>Bounce easing in-out. Port of d3.easeBounce.</summary>
    public static double Bounce(double t) =>
        ((t *= 2) <= 1 ? 1 - BounceOut(1 - t) : BounceOut(t - 1) + 1) / 2;

    // ─── Helper ─────────────────────────────────────────────────────────

    /// <summary>tpmt(x) = 2^(-10x), but exact for special values.</summary>
    private static double Tpmt(double x)
    {
        return (Math.Pow(2, -10 * x) - 0.0009765625) * 1.0009775171065494;
    }
}
