// Port of d3-random — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Random number generators for various probability distributions.
/// Direct port of d3-random.
/// </summary>
public static class D3Random
{
    private static readonly ThreadLocal<Random> _rng = new(() => new Random());

    /// <summary>
    /// Returns a function that generates uniform random numbers in [min, max).
    /// Port of d3.randomUniform().
    /// </summary>
    public static Func<double> Uniform(double min = 0, double max = 1)
    {
        return () => min + _rng.Value!.NextDouble() * (max - min);
    }

    /// <summary>
    /// Returns a function that generates random integers in [min, max).
    /// Port of d3.randomInt().
    /// </summary>
    public static Func<int> Int(int min, int max)
    {
        return () => _rng.Value!.Next(min, max);
    }

    /// <summary>
    /// Returns a function that generates normal (Gaussian) random numbers.
    /// Port of d3.randomNormal().
    /// </summary>
    public static Func<double> Normal(double mu = 0, double sigma = 1)
    {
        return () =>
        {
            // Box-Muller transform
            double u1, u2;
            do { u1 = _rng.Value!.NextDouble(); } while (u1 == 0);
            u2 = _rng.Value!.NextDouble();
            return mu + sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        };
    }

    /// <summary>
    /// Returns a function that generates log-normal random numbers.
    /// Port of d3.randomLogNormal().
    /// </summary>
    public static Func<double> LogNormal(double mu = 0, double sigma = 1)
    {
        var normal = Normal(mu, sigma);
        return () => Math.Exp(normal());
    }

    /// <summary>
    /// Returns a function that generates exponential random numbers.
    /// Port of d3.randomExponential().
    /// </summary>
    public static Func<double> Exponential(double lambda = 1)
    {
        if (lambda <= 0) throw new ArgumentOutOfRangeException(nameof(lambda));
        return () => -Math.Log(1 - _rng.Value!.NextDouble()) / lambda;
    }

    /// <summary>
    /// Returns a function that generates Bernoulli random numbers (0 or 1).
    /// Port of d3.randomBernoulli().
    /// </summary>
    public static Func<int> Bernoulli(double p = 0.5)
    {
        return () => _rng.Value!.NextDouble() < p ? 1 : 0;
    }

    /// <summary>
    /// Returns a function that generates binomial random numbers.
    /// Port of d3.randomBinomial().
    /// </summary>
    public static Func<int> Binomial(int n, double p = 0.5)
    {
        var bernoulli = Bernoulli(p);
        return () =>
        {
            int sum = 0;
            for (int i = 0; i < n; i++) sum += bernoulli();
            return sum;
        };
    }

    /// <summary>
    /// Returns a function that generates geometric random numbers.
    /// Port of d3.randomGeometric().
    /// </summary>
    public static Func<int> Geometric(double p = 0.5)
    {
        if (p <= 0 || p > 1) throw new ArgumentOutOfRangeException(nameof(p));
        double logP = Math.Log(1 - p);
        return () => (int)Math.Floor(Math.Log(1 - _rng.Value!.NextDouble()) / logP);
    }

    /// <summary>
    /// Returns a function that generates Poisson random numbers.
    /// Port of d3.randomPoisson().
    /// </summary>
    public static Func<int> Poisson(double lambda = 1)
    {
        if (lambda <= 0) throw new ArgumentOutOfRangeException(nameof(lambda));
        return () =>
        {
            double l = Math.Exp(-lambda);
            int k = 0;
            double p = 1;
            do
            {
                k++;
                p *= _rng.Value!.NextDouble();
            } while (p > l);
            return k - 1;
        };
    }

    /// <summary>
    /// Returns a function that generates Pareto random numbers.
    /// Port of d3.randomPareto().
    /// </summary>
    public static Func<double> Pareto(double alpha = 1)
    {
        if (alpha <= 0) throw new ArgumentOutOfRangeException(nameof(alpha));
        double oneOverAlpha = 1 / alpha;
        return () => Math.Pow(1 - _rng.Value!.NextDouble(), -oneOverAlpha);
    }

    /// <summary>
    /// Returns a function that generates Cauchy random numbers.
    /// Port of d3.randomCauchy().
    /// </summary>
    public static Func<double> Cauchy(double location = 0, double scale = 1)
    {
        return () => location + scale * Math.Tan(Math.PI * _rng.Value!.NextDouble());
    }

    /// <summary>
    /// Returns a function that generates Weibull random numbers.
    /// Port of d3.randomWeibull().
    /// </summary>
    public static Func<double> Weibull(double k = 1, double lambda = 1)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        if (lambda <= 0) throw new ArgumentOutOfRangeException(nameof(lambda));
        return () => lambda * Math.Pow(-Math.Log(1 - _rng.Value!.NextDouble()), 1 / k);
    }

    /// <summary>
    /// Returns a function that generates Irwin-Hall random numbers
    /// (sum of n uniform randoms).
    /// Port of d3.randomIrwinHall().
    /// </summary>
    public static Func<double> IrwinHall(int n)
    {
        return () =>
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += _rng.Value!.NextDouble();
            return sum;
        };
    }

    /// <summary>
    /// Returns a function that generates Bates random numbers
    /// (mean of n uniform randoms).
    /// Port of d3.randomBates().
    /// </summary>
    public static Func<double> Bates(int n)
    {
        var irwinHall = IrwinHall(n);
        return () => irwinHall() / n;
    }
}
