// Port of d3-contour — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes contour polygons using marching squares.
/// Direct port of d3.contours().
/// </summary>
public sealed class ContourGenerator
{
    private int _width;
    private int _height;
    private double[]? _thresholds;
    private int _thresholdCount = 10;

    public ContourGenerator(int width, int height)
    {
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Computes contour polygons for the given grid of values.
    /// Values is a flat array of length width * height in row-major order.
    /// </summary>
    public ContourMultiPolygon[] Generate(double[] values)
    {
        double[] thresholds = _thresholds ?? ComputeThresholds(values);
        var result = new ContourMultiPolygon[thresholds.Length];

        for (int t = 0; t < thresholds.Length; t++)
        {
            result[t] = new ContourMultiPolygon
            {
                Value = thresholds[t],
                Coordinates = MarchingSquares(values, thresholds[t])
            };
        }

        return result;
    }

    public ContourGenerator SetThresholds(params double[] thresholds) { _thresholds = thresholds; return this; }
    public ContourGenerator SetThresholdCount(int count) { _thresholdCount = count; return this; }

    private double[] ComputeThresholds(double[] values)
    {
        var (min, max) = D3Extent.Extent(values);
        return D3Ticks.Ticks(min, max, _thresholdCount);
    }

    /// <summary>
    /// Marching squares algorithm: traces contour lines at the given threshold.
    /// Returns a list of polygons, each polygon being a list of (x, y) points.
    /// </summary>
    private List<List<(double x, double y)>> MarchingSquares(double[] values, double threshold)
    {
        var segments = new List<((double x, double y) a, (double x, double y) b)>();

        for (int j = 0; j < _height - 1; j++)
        {
            for (int i = 0; i < _width - 1; i++)
            {
                double tl = values[j * _width + i];
                double tr = values[j * _width + i + 1];
                double br = values[(j + 1) * _width + i + 1];
                double bl = values[(j + 1) * _width + i];

                int code = 0;
                if (tl >= threshold) code |= 8;
                if (tr >= threshold) code |= 4;
                if (br >= threshold) code |= 2;
                if (bl >= threshold) code |= 1;

                // Skip empty and full cells
                if (code == 0 || code == 15) continue;

                // Interpolation helpers
                double LerpX(double v0, double v1) => v0 == v1 ? 0.5 : (threshold - v0) / (v1 - v0);
                double top = i + LerpX(tl, tr);
                double right = j + LerpX(tr, br);
                double bottom = i + LerpX(bl, br);
                double left = j + LerpX(tl, bl);

                switch (code)
                {
                    case 1: segments.Add(((i, left), (bottom, j + 1))); break;
                    case 2: segments.Add(((bottom, j + 1), (i + 1, right))); break;
                    case 3: segments.Add(((i, left), (i + 1, right))); break;
                    case 4: segments.Add(((top, j), (i + 1, right))); break;
                    case 5: // Saddle
                        segments.Add(((top, j), (i, left)));
                        segments.Add(((bottom, j + 1), (i + 1, right)));
                        break;
                    case 6: segments.Add(((top, j), (bottom, j + 1))); break;
                    case 7: segments.Add(((top, j), (i, left))); break;
                    case 8: segments.Add(((i, left), (top, j))); break;
                    case 9: segments.Add(((bottom, j + 1), (top, j))); break;
                    case 10: // Saddle
                        segments.Add(((i, left), (top, j)));
                        segments.Add(((i + 1, right), (bottom, j + 1)));
                        break;
                    case 11: segments.Add(((i + 1, right), (top, j))); break;
                    case 12: segments.Add(((i, left), (i + 1, right))); break;
                    case 13: segments.Add(((i + 1, right), (bottom, j + 1))); break;
                    case 14: segments.Add(((i, left), (bottom, j + 1))); break;
                }
            }
        }

        // Stitch segments into polygons
        return StitchSegments(segments);
    }

    private static List<List<(double x, double y)>> StitchSegments(
        List<((double x, double y) a, (double x, double y) b)> segments)
    {
        var polygons = new List<List<(double x, double y)>>();
        var used = new bool[segments.Count];

        for (int i = 0; i < segments.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;

            var ring = new List<(double x, double y)> { segments[i].a, segments[i].b };
            var end = segments[i].b;

            // Try to extend the ring
            bool extended = true;
            while (extended)
            {
                extended = false;
                for (int j = 0; j < segments.Count; j++)
                {
                    if (used[j]) continue;
                    if (Near(segments[j].a, end))
                    {
                        ring.Add(segments[j].b);
                        end = segments[j].b;
                        used[j] = true;
                        extended = true;
                    }
                    else if (Near(segments[j].b, end))
                    {
                        ring.Add(segments[j].a);
                        end = segments[j].a;
                        used[j] = true;
                        extended = true;
                    }
                }
            }

            if (ring.Count >= 3) polygons.Add(ring);
        }

        return polygons;
    }

    private static bool Near((double x, double y) a, (double x, double y) b)
    {
        return Math.Abs(a.x - b.x) < 1e-6 && Math.Abs(a.y - b.y) < 1e-6;
    }
}

/// <summary>
/// Computes density contours for scattered 2D point data.
/// Direct port of d3.contourDensity().
/// </summary>
public sealed class DensityContourGenerator
{
    private int _width = 960;
    private int _height = 500;
    private double _bandwidth = 20.4939;
    private int _thresholdCount = 10;
    private double[]? _thresholds;
    private int _cellSize = 4;

    public DensityContourGenerator SetSize(int width, int height) { _width = width; _height = height; return this; }
    public DensityContourGenerator SetBandwidth(double bandwidth) { _bandwidth = bandwidth; return this; }
    public DensityContourGenerator SetThresholds(params double[] thresholds) { _thresholds = thresholds; return this; }
    public DensityContourGenerator SetThresholdCount(int count) { _thresholdCount = count; return this; }
    public DensityContourGenerator SetCellSize(int size) { _cellSize = Math.Max(1, size); return this; }

    /// <summary>
    /// Computes density contours for the given points.
    /// </summary>
    public ContourMultiPolygon[] Generate(IReadOnlyList<(double x, double y)> points)
    {
        int cols = (int)Math.Ceiling((double)_width / _cellSize);
        int rows = (int)Math.Ceiling((double)_height / _cellSize);
        var grid = new double[cols * rows];

        // Apply Gaussian kernel density estimation
        double r = _bandwidth / _cellSize;
        int radius = (int)Math.Ceiling(r * 3);

        foreach (var (px, py) in points)
        {
            int ci = (int)(px / _cellSize);
            int cj = (int)(py / _cellSize);

            for (int dj = -radius; dj <= radius; dj++)
            {
                int gj = cj + dj;
                if (gj < 0 || gj >= rows) continue;
                for (int di = -radius; di <= radius; di++)
                {
                    int gi = ci + di;
                    if (gi < 0 || gi >= cols) continue;
                    double dist2 = (di * di + dj * dj);
                    double weight = Math.Exp(-dist2 / (2 * r * r)) / (2 * Math.PI * r * r);
                    grid[gj * cols + gi] += weight;
                }
            }
        }

        // Normalize by point count
        double n = points.Count;
        if (n > 0)
        {
            for (int i = 0; i < grid.Length; i++)
                grid[i] /= n;
        }

        var contour = new ContourGenerator(cols, rows);
        if (_thresholds != null)
            contour.SetThresholds(_thresholds);
        else
            contour.SetThresholdCount(_thresholdCount);

        var result = contour.Generate(grid);

        // Scale coordinates back from grid space to pixel space
        foreach (var mp in result)
        {
            foreach (var ring in mp.Coordinates)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    ring[i] = (ring[i].x * _cellSize, ring[i].y * _cellSize);
                }
            }
        }

        return result;
    }
}

/// <summary>A multi-polygon contour at a specific threshold value.</summary>
public sealed class ContourMultiPolygon
{
    public required double Value { get; init; }
    public required List<List<(double x, double y)>> Coordinates { get; init; }
}
