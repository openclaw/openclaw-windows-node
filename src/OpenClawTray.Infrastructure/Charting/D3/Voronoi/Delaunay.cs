// Port of d3-delaunay — ISC License, Copyright 2010-2023 Mike Bostock
// Uses a sweep-line Delaunay triangulation algorithm.

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes the Delaunay triangulation for a set of 2D points.
/// Direct port of d3.Delaunay.from().
/// </summary>
public sealed class Delaunay
{
    /// <summary>The input points.</summary>
    public (double x, double y)[] Points { get; }

    /// <summary>
    /// Triangle indices. Every three consecutive values form a triangle.
    /// triangles[i*3], triangles[i*3+1], triangles[i*3+2] are point indices for triangle i.
    /// </summary>
    public int[] Triangles { get; }

    /// <summary>
    /// Half-edge indices. For each half-edge i, halfedges[i] is the opposite half-edge,
    /// or -1 if on the convex hull.
    /// </summary>
    public int[] Halfedges { get; }

    /// <summary>Convex hull as an ordered list of point indices.</summary>
    public int[] Hull { get; }

    private Delaunay((double x, double y)[] points, int[] triangles, int[] halfedges, int[] hull)
    {
        Points = points;
        Triangles = triangles;
        Halfedges = halfedges;
        Hull = hull;
    }

    /// <summary>
    /// Computes the Delaunay triangulation for the given points.
    /// Uses the Bowyer-Watson incremental insertion algorithm.
    /// </summary>
    public static Delaunay From(IReadOnlyList<(double x, double y)> points)
    {
        var pts = points.ToArray();
        int n = pts.Length;

        if (n < 3)
        {
            return new Delaunay(pts, [], [], n >= 1 ? Enumerable.Range(0, n).ToArray() : []);
        }

        // Find a seed triangle (points not collinear)
        int i0 = 0, i1 = -1, i2 = -1;

        // Pick a center point
        double cx = 0, cy = 0;
        for (int i = 0; i < n; i++) { cx += pts[i].x; cy += pts[i].y; }
        cx /= n; cy /= n;

        // Find closest to center
        double minDist = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double d = Dist2(pts[i].x, pts[i].y, cx, cy);
            if (d < minDist) { minDist = d; i0 = i; }
        }

        // Find closest to i0
        minDist = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (i == i0) continue;
            double d = Dist2(pts[i].x, pts[i].y, pts[i0].x, pts[i0].y);
            if (d < minDist) { minDist = d; i1 = i; }
        }

        // Find point that makes smallest circumcircle with i0, i1
        double minRadius = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (i == i0 || i == i1) continue;
            double r = Circumradius(pts[i0], pts[i1], pts[i]);
            if (r < minRadius) { minRadius = r; i2 = i; }
        }

        if (i1 < 0 || i2 < 0 || double.IsInfinity(minRadius))
        {
            // Degenerate: all points collinear
            return new Delaunay(pts, [], [], Enumerable.Range(0, n).ToArray());
        }

        // Bowyer-Watson incremental insertion with super-triangle
        double pxmin = double.MaxValue, pymin = double.MaxValue;
        double pxmax = double.MinValue, pymax = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (pts[i].x < pxmin) pxmin = pts[i].x;
            if (pts[i].y < pymin) pymin = pts[i].y;
            if (pts[i].x > pxmax) pxmax = pts[i].x;
            if (pts[i].y > pymax) pymax = pts[i].y;
        }
        double dw = pxmax - pxmin, dh = pymax - pymin;
        double dm = Math.Max(dw, dh);
        double pmx = (pxmin + pxmax) / 2, pmy = (pymin + pymax) / 2;

        // Super-triangle vertices at indices n, n+1, n+2 (CCW)
        var allPts = new (double x, double y)[n + 3];
        Array.Copy(pts, allPts, n);
        allPts[n]     = (pmx - 20 * dm, pmy - dm);
        allPts[n + 1] = (pmx + 20 * dm, pmy - dm);
        allPts[n + 2] = (pmx, pmy + 20 * dm);

        var tris = new List<(int a, int b, int c)> { (n, n + 1, n + 2) };

        for (int i = 0; i < n; i++)
        {
            // Find all triangles whose circumcircle contains point i
            var bad = new List<int>();
            for (int t = 0; t < tris.Count; t++)
            {
                var tri = tris[t];
                if (InCircumcircle(allPts[tri.a], allPts[tri.b], allPts[tri.c], allPts[i]))
                    bad.Add(t);
            }

            // Find boundary edges of the polygonal hole
            var boundary = new List<(int ea, int eb)>();
            foreach (int t in bad)
            {
                var tri = tris[t];
                (int, int)[] edges = [(tri.a, tri.b), (tri.b, tri.c), (tri.c, tri.a)];
                foreach (var (ea, eb) in edges)
                {
                    bool shared = false;
                    foreach (int t2 in bad)
                    {
                        if (t2 == t) continue;
                        var tri2 = tris[t2];
                        if ((tri2.a == eb && tri2.b == ea) ||
                            (tri2.b == eb && tri2.c == ea) ||
                            (tri2.c == eb && tri2.a == ea))
                        { shared = true; break; }
                    }
                    if (!shared) boundary.Add((ea, eb));
                }
            }

            // Remove bad triangles (reverse order to preserve indices)
            bad.Sort();
            for (int j = bad.Count - 1; j >= 0; j--)
                tris.RemoveAt(bad[j]);

            // Create new triangles connecting point to each boundary edge
            foreach (var (ea, eb) in boundary)
                tris.Add((i, ea, eb));
        }

        // Remove triangles that reference super-triangle vertices
        tris.RemoveAll(t => t.a >= n || t.b >= n || t.c >= n);

        // Build flat triangle array
        var triangles = new int[tris.Count * 3];
        for (int t = 0; t < tris.Count; t++)
        {
            triangles[t * 3] = tris[t].a;
            triangles[t * 3 + 1] = tris[t].b;
            triangles[t * 3 + 2] = tris[t].c;
        }

        // Build halfedges by matching opposite half-edges
        var halfedges = new int[triangles.Length];
        Array.Fill(halfedges, -1);
        var edgeMap = new Dictionary<(int, int), int>();
        for (int e = 0; e < triangles.Length; e++)
        {
            int next = (e % 3 == 2) ? e - 2 : e + 1;
            int a = triangles[e], b = triangles[next];
            if (edgeMap.TryGetValue((b, a), out int opp))
            {
                halfedges[e] = opp;
                halfedges[opp] = e;
                edgeMap.Remove((b, a));
            }
            else
            {
                edgeMap[(a, b)] = e;
            }
        }

        var hull = ComputeConvexHull(pts);
        return new Delaunay(pts, triangles, halfedges, hull);
    }

    /// <summary>
    /// Returns the index of the point closest to (x, y).
    /// </summary>
    public int Find(double x, double y)
    {
        int closest = 0;
        double minDist = double.MaxValue;
        for (int i = 0; i < Points.Length; i++)
        {
            double d = Dist2(Points[i].x, Points[i].y, x, y);
            if (d < minDist) { minDist = d; closest = i; }
        }
        return closest;
    }

    /// <summary>
    /// Returns the neighbors of the given point index.
    /// </summary>
    public IEnumerable<int> Neighbors(int index)
    {
        var neighbors = new HashSet<int>();
        for (int i = 0; i < Triangles.Length; i += 3)
        {
            int a = Triangles[i], b = Triangles[i + 1], c = Triangles[i + 2];
            if (a == index) { neighbors.Add(b); neighbors.Add(c); }
            else if (b == index) { neighbors.Add(a); neighbors.Add(c); }
            else if (c == index) { neighbors.Add(a); neighbors.Add(b); }
        }
        return neighbors;
    }

    /// <summary>
    /// Computes the Voronoi diagram for this triangulation within the given bounds.
    /// </summary>
    public Voronoi Voronoi(double xmin = 0, double ymin = 0, double xmax = 960, double ymax = 500)
    {
        return new Voronoi(this, xmin, ymin, xmax, ymax);
    }

    private static double Dist2(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static double Orient((double x, double y) a, (double x, double y) b, (double x, double y) c)
    {
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    private static double Circumradius((double x, double y) a, (double x, double y) b, (double x, double y) c)
    {
        double dx = b.x - a.x, dy = b.y - a.y;
        double ex = c.x - a.x, ey = c.y - a.y;
        double bl = dx * dx + dy * dy;
        double cl = ex * ex + ey * ey;
        double d = 2 * (dx * ey - dy * ex);
        if (Math.Abs(d) < 1e-10) return double.PositiveInfinity;
        double ux = (ey * bl - dy * cl) / d;
        double uy = (dx * cl - ex * bl) / d;
        return ux * ux + uy * uy;
    }

    /// <summary>
    /// Returns true if point p lies inside the circumcircle of CCW triangle (a, b, c).
    /// </summary>
    private static bool InCircumcircle(
        (double x, double y) a, (double x, double y) b,
        (double x, double y) c, (double x, double y) p)
    {
        double ax = a.x - p.x, ay = a.y - p.y;
        double bx = b.x - p.x, by = b.y - p.y;
        double cx = c.x - p.x, cy = c.y - p.y;
        double al = ax * ax + ay * ay;
        double bl = bx * bx + by * by;
        double cl = cx * cx + cy * cy;
        return ax * (by * cl - cy * bl)
             - ay * (bx * cl - cx * bl)
             + al * (bx * cy - cx * by) > 0;
    }

    private static int[] ComputeConvexHull((double x, double y)[] points)
    {
        int n = points.Length;
        var indices = Enumerable.Range(0, n).OrderBy(i => points[i].x).ThenBy(i => points[i].y).ToArray();

        var lower = new List<int>();
        for (int i = 0; i < n; i++)
        {
            while (lower.Count >= 2 &&
                   Orient(points[lower[^2]], points[lower[^1]], points[indices[i]]) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(indices[i]);
        }

        var upper = new List<int>();
        for (int i = n - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 &&
                   Orient(points[upper[^2]], points[upper[^1]], points[indices[i]]) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(indices[i]);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower.ToArray();
    }
}

/// <summary>
/// Computes the Voronoi diagram from a Delaunay triangulation.
/// Direct port of d3.Delaunay.voronoi().
/// </summary>
public sealed class Voronoi
{
    private readonly Delaunay _delaunay;
    private readonly double _xmin, _ymin, _xmax, _ymax;

    /// <summary>
    /// The circumcenters of each Delaunay triangle — these are the Voronoi vertices.
    /// </summary>
    public (double x, double y)[] Circumcenters { get; }

    internal Voronoi(Delaunay delaunay, double xmin, double ymin, double xmax, double ymax)
    {
        _delaunay = delaunay;
        _xmin = xmin;
        _ymin = ymin;
        _xmax = xmax;
        _ymax = ymax;

        // Compute circumcenters for each triangle
        var triangles = delaunay.Triangles;
        var points = delaunay.Points;
        int numTriangles = triangles.Length / 3;
        Circumcenters = new (double x, double y)[numTriangles];

        for (int t = 0; t < numTriangles; t++)
        {
            var a = points[triangles[t * 3]];
            var b = points[triangles[t * 3 + 1]];
            var c = points[triangles[t * 3 + 2]];
            Circumcenters[t] = ComputeCircumcenter(a, b, c);
        }
    }

    /// <summary>
    /// Returns the Voronoi cell polygon for the given point index,
    /// clipped to the bounding box.
    /// </summary>
    public (double x, double y)[]? CellPolygon(int index)
    {
        var points = _delaunay.Points;
        if (index < 0 || index >= points.Length) return null;

        // Find all triangles containing this point
        var triangleIndices = new List<int>();
        var triangles = _delaunay.Triangles;
        for (int t = 0; t < triangles.Length / 3; t++)
        {
            if (triangles[t * 3] == index ||
                triangles[t * 3 + 1] == index ||
                triangles[t * 3 + 2] == index)
            {
                triangleIndices.Add(t);
            }
        }

        if (triangleIndices.Count == 0)
        {
            // Fallback: create a bounding box cell for isolated points
            return [(points[index].x - 1, points[index].y - 1),
                    (points[index].x + 1, points[index].y - 1),
                    (points[index].x + 1, points[index].y + 1),
                    (points[index].x - 1, points[index].y + 1)];
        }

        // Collect circumcenters and sort them around the point
        var cell = triangleIndices
            .Select(t => Circumcenters[t])
            .ToList();

        // Sort by angle around the point
        double px = points[index].x, py = points[index].y;
        cell.Sort((a, b) =>
        {
            double angleA = Math.Atan2(a.y - py, a.x - px);
            double angleB = Math.Atan2(b.y - py, b.x - px);
            return angleA.CompareTo(angleB);
        });

        // Clip to bounding box
        var clipped = ClipToBounds(cell);
        return clipped.Count >= 3 ? clipped.ToArray() : null;
    }

    /// <summary>
    /// Returns true if the given point is inside the Voronoi cell of point index.
    /// </summary>
    public bool Contains(int index, double x, double y)
    {
        var points = _delaunay.Points;
        double d = Dist2(x, y, points[index].x, points[index].y);

        for (int i = 0; i < points.Length; i++)
        {
            if (i == index) continue;
            if (Dist2(x, y, points[i].x, points[i].y) < d) return false;
        }
        return true;
    }

    private List<(double x, double y)> ClipToBounds(List<(double x, double y)> polygon)
    {
        // Sutherland-Hodgman polygon clipping against bounding box
        var result = new List<(double x, double y)>(polygon);
        result = ClipToEdge(result, 0); // left:   x >= xmin
        result = ClipToEdge(result, 1); // right:  x <= xmax
        result = ClipToEdge(result, 2); // bottom: y >= ymin
        result = ClipToEdge(result, 3); // top:    y <= ymax
        return result;
    }

    private List<(double x, double y)> ClipToEdge(List<(double x, double y)> polygon, int edge)
    {
        if (polygon.Count == 0) return polygon;
        var result = new List<(double x, double y)>();
        var s = polygon[^1];
        foreach (var e in polygon)
        {
            if (EdgeInside(e, edge))
            {
                if (!EdgeInside(s, edge))
                    result.Add(EdgeIntersect(s, e, edge));
                result.Add(e);
            }
            else if (EdgeInside(s, edge))
            {
                result.Add(EdgeIntersect(s, e, edge));
            }
            s = e;
        }
        return result;
    }

    private bool EdgeInside((double x, double y) p, int edge) => edge switch
    {
        0 => p.x >= _xmin,
        1 => p.x <= _xmax,
        2 => p.y >= _ymin,
        _ => p.y <= _ymax,
    };

    private (double x, double y) EdgeIntersect((double x, double y) a, (double x, double y) b, int edge)
    {
        double t = edge switch
        {
            0 => (_xmin - a.x) / (b.x - a.x),
            1 => (_xmax - a.x) / (b.x - a.x),
            2 => (_ymin - a.y) / (b.y - a.y),
            _ => (_ymax - a.y) / (b.y - a.y),
        };
        return (a.x + t * (b.x - a.x), a.y + t * (b.y - a.y));
    }

    private static (double x, double y) ComputeCircumcenter(
        (double x, double y) a, (double x, double y) b, (double x, double y) c)
    {
        double dx = b.x - a.x, dy = b.y - a.y;
        double ex = c.x - a.x, ey = c.y - a.y;
        double bl = dx * dx + dy * dy;
        double cl = ex * ex + ey * ey;
        double d = 2 * (dx * ey - dy * ex);
        if (Math.Abs(d) < 1e-10)
            return ((a.x + b.x + c.x) / 3, (a.y + b.y + c.y) / 3);
        double ux = (ey * bl - dy * cl) / d;
        double uy = (dx * cl - ex * bl) / d;
        return (a.x + ux, a.y + uy);
    }

    private static double Dist2(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }
}
