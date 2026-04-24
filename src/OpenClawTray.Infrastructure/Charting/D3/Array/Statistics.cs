// Port of d3-array statistics functions — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Statistical functions for numeric data arrays.
/// Direct port of d3-array's min, max, sum, mean, median, quantile, variance, deviation.
/// </summary>
public static class D3Statistics
{
    /// <summary>Returns the minimum value in the given values, ignoring NaN.</summary>
    public static double Min(IEnumerable<double> values)
    {
        double min = double.PositiveInfinity;
        bool found = false;
        foreach (var v in values)
        {
            if (!double.IsNaN(v) && (v < min || !found))
            {
                min = v;
                found = true;
            }
        }
        return found ? min : double.NaN;
    }

    /// <summary>Returns the minimum value using an accessor, ignoring NaN.</summary>
    public static double Min<T>(IEnumerable<T> values, Func<T, double> valueOf)
    {
        double min = double.PositiveInfinity;
        bool found = false;
        foreach (var item in values)
        {
            double v = valueOf(item);
            if (!double.IsNaN(v) && (v < min || !found))
            {
                min = v;
                found = true;
            }
        }
        return found ? min : double.NaN;
    }

    /// <summary>Returns the maximum value in the given values, ignoring NaN.</summary>
    public static double Max(IEnumerable<double> values)
    {
        double max = double.NegativeInfinity;
        bool found = false;
        foreach (var v in values)
        {
            if (!double.IsNaN(v) && (v > max || !found))
            {
                max = v;
                found = true;
            }
        }
        return found ? max : double.NaN;
    }

    /// <summary>Returns the maximum value using an accessor, ignoring NaN.</summary>
    public static double Max<T>(IEnumerable<T> values, Func<T, double> valueOf)
    {
        double max = double.NegativeInfinity;
        bool found = false;
        foreach (var item in values)
        {
            double v = valueOf(item);
            if (!double.IsNaN(v) && (v > max || !found))
            {
                max = v;
                found = true;
            }
        }
        return found ? max : double.NaN;
    }

    /// <summary>Returns the sum of the given values, ignoring NaN.</summary>
    public static double Sum(IEnumerable<double> values)
    {
        double sum = 0;
        foreach (var v in values)
        {
            if (!double.IsNaN(v)) sum += v;
        }
        return sum;
    }

    /// <summary>Returns the sum using an accessor, ignoring NaN.</summary>
    public static double Sum<T>(IEnumerable<T> values, Func<T, double> valueOf)
    {
        double sum = 0;
        foreach (var item in values)
        {
            double v = valueOf(item);
            if (!double.IsNaN(v)) sum += v;
        }
        return sum;
    }

    /// <summary>Returns the arithmetic mean of the given values, ignoring NaN.</summary>
    public static double Mean(IEnumerable<double> values)
    {
        double sum = 0;
        int count = 0;
        foreach (var v in values)
        {
            if (!double.IsNaN(v))
            {
                sum += v;
                count++;
            }
        }
        return count > 0 ? sum / count : double.NaN;
    }

    /// <summary>Returns the arithmetic mean using an accessor, ignoring NaN.</summary>
    public static double Mean<T>(IEnumerable<T> values, Func<T, double> valueOf)
    {
        double sum = 0;
        int count = 0;
        foreach (var item in values)
        {
            double v = valueOf(item);
            if (!double.IsNaN(v))
            {
                sum += v;
                count++;
            }
        }
        return count > 0 ? sum / count : double.NaN;
    }

    /// <summary>Returns the median of the given values (the 0.5-quantile).</summary>
    public static double Median(IEnumerable<double> values)
    {
        return Quantile(values, 0.5);
    }

    /// <summary>Returns the median using an accessor.</summary>
    public static double Median<T>(IEnumerable<T> values, Func<T, double> valueOf)
    {
        return Quantile(values.Select(valueOf), 0.5);
    }

    /// <summary>
    /// Returns the p-quantile of the given values using the R-7 method.
    /// The values are sorted internally. p must be in [0, 1].
    /// </summary>
    public static double Quantile(IEnumerable<double> values, double p)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        return QuantileSorted(sorted, p);
    }

    /// <summary>
    /// Returns the p-quantile of pre-sorted values using the R-7 method.
    /// Port of d3-array's quantileSorted.
    /// </summary>
    public static double QuantileSorted(double[] sorted, double p)
    {
        int n = sorted.Length;
        if (n == 0 || double.IsNaN(p)) return double.NaN;
        if (p <= 0 || n < 2) return sorted[0];
        if (p >= 1) return sorted[n - 1];

        double i = (n - 1) * p;
        int i0 = (int)Math.Floor(i);
        double v0 = sorted[i0];
        double v1 = sorted[i0 + 1];
        return v0 + (v1 - v0) * (i - i0);
    }

    /// <summary>Returns the variance of the given values using Welford's algorithm.</summary>
    public static double Variance(IEnumerable<double> values)
    {
        int count = 0;
        double mean = 0;
        double m2 = 0;
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            count++;
            double delta = v - mean;
            mean += delta / count;
            m2 += delta * (v - mean);
        }
        return count > 1 ? m2 / (count - 1) : double.NaN;
    }

    /// <summary>Returns the standard deviation (square root of variance).</summary>
    public static double Deviation(IEnumerable<double> values)
    {
        double v = Variance(values);
        return double.IsNaN(v) ? double.NaN : Math.Sqrt(v);
    }

    /// <summary>Returns the cumulative sum of the given values.</summary>
    public static double[] CumSum(IEnumerable<double> values)
    {
        var result = new List<double>();
        double sum = 0;
        foreach (var v in values)
        {
            sum += double.IsNaN(v) ? 0 : v;
            result.Add(sum);
        }
        return result.ToArray();
    }
}
