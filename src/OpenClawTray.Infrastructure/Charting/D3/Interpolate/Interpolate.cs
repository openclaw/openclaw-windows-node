// Port of d3-interpolate — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Interpolation functions for smooth transitions between values.
/// </summary>
public static class D3Interpolate
{
    /// <summary>
    /// Returns a function that interpolates between <paramref name="a"/> and <paramref name="b"/>
    /// using a parameter t in [0, 1].
    /// </summary>
    public static Func<double, double> Number(double a, double b)
    {
        return t => a * (1 - t) + b * t;
    }

    /// <summary>
    /// Like <see cref="Number"/>, but rounds the result to the nearest integer.
    /// </summary>
    public static Func<double, double> Round(double a, double b)
    {
        return t => Math.Round(a * (1 - t) + b * t);
    }
}
