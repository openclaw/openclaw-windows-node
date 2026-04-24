// Port of d3-hierarchy/src/pack.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A circle-packing layout. Encodes hierarchical data as nested circles.
/// Direct port of d3.pack().
/// </summary>
public sealed class PackLayout<T>
{
    private double _radius = 500;
    private double _padding = 0;

    /// <summary>
    /// Builds a pack node hierarchy from data with a children accessor,
    /// then sums values and computes the layout.
    /// </summary>
    public PackNode<T> Layout(T root, Func<T, IEnumerable<T>?> children, Func<T, double> value)
    {
        var node = BuildNode(root, children, null, 0);
        SumValues(node, value);
        PackCircles(node);
        NormalizeRadius(node, _radius);
        return node;
    }

    public PackLayout<T> Size(double radius) { _radius = radius; return this; }
    public PackLayout<T> SetPadding(double padding) { _padding = padding; return this; }

    private PackNode<T> BuildNode(T data, Func<T, IEnumerable<T>?> childrenAccessor, PackNode<T>? parent, int depth)
    {
        var node = new PackNode<T> { Data = data, Parent = parent, Depth = depth };
        var kids = childrenAccessor(data);
        if (kids != null)
        {
            foreach (var child in kids)
                node.Children.Add(BuildNode(child, childrenAccessor, node, depth + 1));
        }
        return node;
    }

    private void SumValues(PackNode<T> node, Func<T, double> value)
    {
        if (node.Children.Count == 0)
        {
            node.Value = Math.Max(0, value(node.Data));
            node.R = Math.Sqrt(node.Value);
        }
        else
        {
            double sum = 0;
            foreach (var child in node.Children)
            {
                SumValues(child, value);
                sum += child.Value;
            }
            node.Value = sum;
        }
    }

    private void PackCircles(PackNode<T> node)
    {
        if (node.Children.Count == 0) return;

        foreach (var child in node.Children)
            PackCircles(child);

        // Simple front-chain packing algorithm
        var circles = node.Children.OrderByDescending(c => c.R).ToList();

        if (circles.Count == 1)
        {
            circles[0].X = 0;
            circles[0].Y = 0;
            node.R = circles[0].R + _padding;
        }
        else if (circles.Count == 2)
        {
            double r0 = circles[0].R + _padding;
            double r1 = circles[1].R + _padding;
            circles[0].X = -r0;
            circles[0].Y = 0;
            circles[1].X = r1;
            circles[1].Y = 0;
            node.R = r0 + r1;
        }
        else
        {
            // Place first two
            double a = circles[0].R + _padding;
            double b = circles[1].R + _padding;
            circles[0].X = -a;
            circles[0].Y = 0;
            circles[1].X = b;
            circles[1].Y = 0;

            // Place remaining circles
            for (int i = 2; i < circles.Count; i++)
            {
                double ri = circles[i].R + _padding;
                // Find the best position adjacent to two existing circles
                double bestX = 0, bestY = 0;
                double bestDist = double.MaxValue;

                for (int j = 0; j < i; j++)
                {
                    for (int k = j + 1; k < i; k++)
                    {
                        var positions = PlaceCircle(circles[j], circles[k], ri);
                        foreach (var (px, py) in positions)
                        {
                            bool overlaps = false;
                            for (int m = 0; m < i; m++)
                            {
                                double dx = px - circles[m].X;
                                double dy = py - circles[m].Y;
                                if (Math.Sqrt(dx * dx + dy * dy) < ri + circles[m].R + _padding - 1e-6)
                                {
                                    overlaps = true;
                                    break;
                                }
                            }
                            if (!overlaps)
                            {
                                double dist = Math.Sqrt(px * px + py * py);
                                if (dist < bestDist)
                                {
                                    bestDist = dist;
                                    bestX = px;
                                    bestY = py;
                                }
                            }
                        }
                    }
                }
                circles[i].X = bestX;
                circles[i].Y = bestY;
            }

            // Compute enclosing radius
            double maxR = 0;
            foreach (var c in circles)
            {
                double d = Math.Sqrt(c.X * c.X + c.Y * c.Y) + c.R + _padding;
                if (d > maxR) maxR = d;
            }
            node.R = maxR;
        }
    }

    private static List<(double x, double y)> PlaceCircle(PackNode<T> a, PackNode<T> b, double r)
    {
        var results = new List<(double, double)>();
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        double ra = a.R + r, rb = b.R + r;

        if (d > ra + rb || d < Math.Abs(ra - rb) || d == 0)
            return results;

        double aa = (ra * ra - rb * rb + d * d) / (2 * d);
        double h2 = ra * ra - aa * aa;
        if (h2 < 0) return results;
        double h = Math.Sqrt(h2);

        double mx = a.X + aa * dx / d;
        double my = a.Y + aa * dy / d;

        results.Add((mx + h * dy / d, my - h * dx / d));
        results.Add((mx - h * dy / d, my + h * dx / d));
        return results;
    }

    private static void NormalizeRadius(PackNode<T> node, double targetRadius)
    {
        if (node.R == 0) return;
        double scale = targetRadius / node.R;
        ScaleNode(node, scale, 0, 0);
    }

    private static void ScaleNode(PackNode<T> node, double scale, double dx, double dy)
    {
        node.X = dx + node.X * scale;
        node.Y = dy + node.Y * scale;
        node.R *= scale;
        foreach (var child in node.Children)
            ScaleNode(child, scale, node.X, node.Y);
    }
}

/// <summary>A node in a circle-packing layout.</summary>
public sealed class PackNode<T>
{
    public required T Data { get; init; }
    public PackNode<T>? Parent { get; init; }
    public List<PackNode<T>> Children { get; } = [];
    public int Depth { get; init; }
    public double Value { get; internal set; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double R { get; internal set; }

    /// <summary>Returns all descendant nodes (including this one) in pre-order.</summary>
    public IEnumerable<PackNode<T>> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var desc in child.Descendants())
                yield return desc;
    }

    /// <summary>Returns the ancestor that is a direct child of the root (for branch coloring). Returns this node if it has no parent.</summary>
    public PackNode<T> TopAncestor
    {
        get
        {
            var current = this;
            while (current.Parent is { Parent: not null })
                current = current.Parent;
            return current;
        }
    }

    /// <summary>Returns all leaf nodes.</summary>
    public IEnumerable<PackNode<T>> Leaves()
    {
        return Descendants().Where(n => n.Children.Count == 0);
    }
}

public static class PackLayout
{
    public static PackLayout<T> Create<T>() => new();
}
