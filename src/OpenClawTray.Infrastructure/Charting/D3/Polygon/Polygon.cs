// Port of d3-polygon — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Geometric operations on 2D polygons.
/// Direct port of d3-polygon.
/// </summary>
public static class D3Polygon
{
    /// <summary>
    /// Returns the signed area of the given polygon.
    /// Positive if vertices are in counter-clockwise order.
    /// Port of d3.polygonArea().
    /// </summary>
    public static double Area(IReadOnlyList<(double x, double y)> polygon)
    {
        int n = polygon.Count;
        if (n == 0) return 0;
        double area = 0;
        var b = polygon[n - 1];
        for (int i = 0; i < n; i++)
        {
            var a = b;
            b = polygon[i];
            area += a.y * b.x - a.x * b.y;
        }
        return area / 2;
    }

    /// <summary>
    /// Returns the centroid of the given polygon.
    /// Port of d3.polygonCentroid().
    /// </summary>
    public static (double x, double y) Centroid(IReadOnlyList<(double x, double y)> polygon)
    {
        int n = polygon.Count;
        if (n == 0) return (0, 0);
        double cx = 0, cy = 0, k = 0;
        var b = polygon[n - 1];
        for (int i = 0; i < n; i++)
        {
            var a = b;
            b = polygon[i];
            double cross = a.x * b.y - b.x * a.y;
            k += cross;
            cx += (a.x + b.x) * cross;
            cy += (a.y + b.y) * cross;
        }
        k *= 3;
        if (k == 0)
        {
            // Degenerate polygon (collinear points) — return average of vertices.
            double ax = 0, ay = 0;
            for (int i = 0; i < n; i++) { ax += polygon[i].x; ay += polygon[i].y; }
            return (ax / n, ay / n);
        }
        return (cx / k, cy / k);
    }

    /// <summary>
    /// Returns true if the given point is inside the polygon.
    /// Port of d3.polygonContains().
    /// </summary>
    public static bool Contains(IReadOnlyList<(double x, double y)> polygon, (double x, double y) point)
    {
        int n = polygon.Count;
        if (n == 0) return false;
        bool inside = false;
        var b = polygon[n - 1];
        for (int i = 0; i < n; i++)
        {
            var a = b;
            b = polygon[i];
            if ((b.y > point.y) != (a.y > point.y) &&
                point.x < (a.x - b.x) * (point.y - b.y) / (a.y - b.y) + b.x)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Returns the length (perimeter) of the given polygon.
    /// Port of d3.polygonLength().
    /// </summary>
    public static double Length(IReadOnlyList<(double x, double y)> polygon)
    {
        int n = polygon.Count;
        if (n == 0) return 0;
        double length = 0;
        var b = polygon[n - 1];
        for (int i = 0; i < n; i++)
        {
            var a = b;
            b = polygon[i];
            double dx = b.x - a.x, dy = b.y - a.y;
            length += Math.Sqrt(dx * dx + dy * dy);
        }
        return length;
    }

    /// <summary>
    /// Returns the convex hull of the given points using Andrew's monotone chain algorithm.
    /// Port of d3.polygonHull().
    /// </summary>
    public static (double x, double y)[]? Hull(IReadOnlyList<(double x, double y)> points)
    {
        int n = points.Count;
        if (n < 3) return null;

        var sorted = points.OrderBy(p => p.x).ThenBy(p => p.y).ToArray();

        var lower = new List<(double x, double y)>();
        for (int i = 0; i < n; i++)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], sorted[i]) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(sorted[i]);
        }

        var upper = new List<(double x, double y)>();
        for (int i = n - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], sorted[i]) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(sorted[i]);
        }

        // Remove last point of each half because it's repeated
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);

        lower.AddRange(upper);
        return lower.ToArray();
    }

    private static double Cross((double x, double y) o, (double x, double y) a, (double x, double y) b)
    {
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }
}
