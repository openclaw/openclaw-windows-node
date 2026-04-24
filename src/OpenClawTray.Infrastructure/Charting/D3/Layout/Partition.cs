// Port of d3-hierarchy/src/partition.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A partition layout subdivides a rectangular area by depth.
/// Produces icicle diagrams (Cartesian) or sunburst diagrams (polar).
/// Direct port of d3.partition().
/// </summary>
public sealed class PartitionLayout<T>
{
    private double _x0 = 0, _y0 = 0, _x1 = 1, _y1 = 1;
    private double _padding = 0;
    private bool _round = false;

    /// <summary>
    /// Builds a partition node hierarchy from data, sums values, and computes the layout.
    /// </summary>
    public PartitionNode<T> Layout(T root, Func<T, IEnumerable<T>?> children, Func<T, double> value)
    {
        var node = BuildNode(root, children, null, 0);
        SumValues(node, value);
        ComputeLayout(node);
        return node;
    }

    /// <summary>
    /// Computes the layout for an already-built hierarchy.
    /// </summary>
    public PartitionNode<T> Layout(PartitionNode<T> root)
    {
        ComputeLayout(root);
        return root;
    }

    public PartitionLayout<T> Size(double x1, double y1) { _x0 = 0; _y0 = 0; _x1 = x1; _y1 = y1; return this; }
    public PartitionLayout<T> SetPadding(double p) { _padding = p; return this; }
    public PartitionLayout<T> SetRound(bool round) { _round = round; return this; }

    private void ComputeLayout(PartitionNode<T> root)
    {
        // Find max depth
        int maxDepth = 0;
        Visit(root, n => { if (n.Depth > maxDepth) maxDepth = n.Depth; });

        double dx = _x1 - _x0;
        double dy = _y1 - _y0;
        double depthHeight = maxDepth > 0 ? dy / (maxDepth + 1) : dy;

        // Root covers full width
        root.X0 = _x0;
        root.X1 = _x1;
        root.Y0 = _y0;
        root.Y1 = _y0 + depthHeight;

        LayoutChildren(root, depthHeight);

        if (_round) RoundAll(root);
    }

    private void LayoutChildren(PartitionNode<T> node, double depthHeight)
    {
        if (node.Children.Count == 0) return;

        double total = node.Value;
        if (total == 0) return;

        double x = node.X0 + _padding;
        double availableWidth = (node.X1 - _padding) - x;
        double y = node.Y1;

        foreach (var child in node.Children)
        {
            double proportion = child.Value / total;
            double childWidth = availableWidth * proportion;

            child.X0 = x;
            child.X1 = x + childWidth;
            child.Y0 = y;
            child.Y1 = y + depthHeight;

            x += childWidth;

            LayoutChildren(child, depthHeight);
        }
    }

    private PartitionNode<T> BuildNode(T data, Func<T, IEnumerable<T>?> childrenAccessor, PartitionNode<T>? parent, int depth)
    {
        var node = new PartitionNode<T> { Data = data, Parent = parent, Depth = depth };
        var kids = childrenAccessor(data);
        if (kids != null)
        {
            foreach (var child in kids)
                node.Children.Add(BuildNode(child, childrenAccessor, node, depth + 1));
        }
        return node;
    }

    private void SumValues(PartitionNode<T> node, Func<T, double> value)
    {
        if (node.Children.Count == 0)
        {
            node.Value = Math.Max(0, value(node.Data));
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

    private static void RoundAll(PartitionNode<T> node)
    {
        node.X0 = Math.Round(node.X0);
        node.X1 = Math.Round(node.X1);
        node.Y0 = Math.Round(node.Y0);
        node.Y1 = Math.Round(node.Y1);
        foreach (var child in node.Children) RoundAll(child);
    }

    private static void Visit(PartitionNode<T> node, Action<PartitionNode<T>> action)
    {
        action(node);
        foreach (var child in node.Children) Visit(child, action);
    }
}

/// <summary>A node in a partition layout with rectangular bounds.</summary>
public sealed class PartitionNode<T>
{
    public required T Data { get; init; }
    public PartitionNode<T>? Parent { get; init; }
    public List<PartitionNode<T>> Children { get; } = [];
    public int Depth { get; init; }
    public double Value { get; internal set; }
    public double X0 { get; internal set; }
    public double Y0 { get; internal set; }
    public double X1 { get; internal set; }
    public double Y1 { get; internal set; }

    /// <summary>Width of the rectangle.</summary>
    public double Width => X1 - X0;

    /// <summary>Height of the rectangle.</summary>
    public double Height => Y1 - Y0;

    /// <summary>Returns all descendant nodes (including this one) in pre-order.</summary>
    public IEnumerable<PartitionNode<T>> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var desc in child.Descendants())
                yield return desc;
    }

    /// <summary>Returns the ancestor that is a direct child of the root (for branch coloring). Returns this node if it has no parent.</summary>
    public PartitionNode<T> TopAncestor
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
    public IEnumerable<PartitionNode<T>> Leaves()
    {
        return Descendants().Where(n => n.Children.Count == 0);
    }

    /// <summary>Returns the ancestors from this node to the root.</summary>
    public IEnumerable<PartitionNode<T>> Ancestors()
    {
        PartitionNode<T>? n = this;
        while (n != null)
        {
            yield return n;
            n = n.Parent;
        }
    }

    /// <summary>
    /// Converts rectangular coordinates to polar for sunburst rendering.
    /// X0/X1 map to startAngle/endAngle, Y0/Y1 map to innerRadius/outerRadius.
    /// Call this after Layout() to get sunburst coordinates.
    /// </summary>
    public (double startAngle, double endAngle, double innerRadius, double outerRadius) ToPolar(
        double totalWidth, double totalHeight, double maxRadius)
    {
        double startAngle = X0 / totalWidth * 2 * Math.PI;
        double endAngle = X1 / totalWidth * 2 * Math.PI;
        double innerRadius = Y0 / totalHeight * maxRadius;
        double outerRadius = Y1 / totalHeight * maxRadius;
        return (startAngle, endAngle, innerRadius, outerRadius);
    }
}

public static class PartitionLayout
{
    public static PartitionLayout<T> Create<T>() => new();
}
