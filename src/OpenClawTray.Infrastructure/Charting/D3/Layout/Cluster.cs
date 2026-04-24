// Port of d3-hierarchy/src/cluster.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes a cluster (dendrogram) layout where all leaves are at the same depth.
/// Unlike tree layout, cluster places all leaves at the bottom, producing
/// dendrograms with uniform leaf positioning.
/// Direct port of d3.cluster().
/// </summary>
public sealed class ClusterLayout<T>
{
    private double _width = 400;
    private double _height = 400;
    private Func<TreeNode<T>, TreeNode<T>, double>? _separation;

    public ClusterLayout<T> Size(double width, double height) { _width = width; _height = height; return this; }

    /// <summary>
    /// Sets a custom separation function between leaf nodes.
    /// The default is 1 for siblings, 2 for non-siblings.
    /// </summary>
    public ClusterLayout<T> Separation(Func<TreeNode<T>, TreeNode<T>, double> separation)
    {
        _separation = separation;
        return this;
    }

    /// <summary>
    /// Build a hierarchy from root data and a children accessor.
    /// Reuses TreeNode from TreeLayout for compatibility.
    /// </summary>
    public TreeNode<T> Hierarchy(T root, Func<T, IEnumerable<T>?> childrenAccessor)
    {
        var node = new TreeNode<T>(root);
        BuildHierarchy(node, childrenAccessor, 0);
        return node;
    }

    /// <summary>
    /// Compute the cluster layout positions for all nodes.
    /// All leaves are placed at the same y depth.
    /// </summary>
    public TreeNode<T> Layout(TreeNode<T> root)
    {
        // Find max depth
        int maxDepth = 0;
        Visit(root, n => { if (n.Depth > maxDepth) maxDepth = n.Depth; });

        // Step 1: Assign x positions to leaves from left to right
        double previousX = 0;
        TreeNode<T>? previousNode = null;

        VisitAfter(root, node =>
        {
            if (node.Children.Count == 0)
            {
                // Leaf node
                double sep = 1;
                if (previousNode != null)
                {
                    if (_separation != null)
                        sep = _separation(node, previousNode);
                    else
                        sep = node.Parent == previousNode.Parent ? 1 : 2;
                }
                else
                {
                    sep = 0;
                }
                node.X = previousX + sep;
                previousX = node.X;
                previousNode = node;
            }
            else
            {
                // Internal node: center over children
                double first = node.Children[0].X;
                double last = node.Children[^1].X;
                node.X = (first + last) / 2;
            }

            // Y = depth (leaves will be at maxDepth)
            node.Y = node.Children.Count == 0 ? maxDepth : node.Depth;
        });

        // Step 2: Normalize to fit within the specified size
        NormalizePositions(root, maxDepth);

        return root;
    }

    private static void BuildHierarchy(TreeNode<T> node, Func<T, IEnumerable<T>?> childrenAccessor, int depth)
    {
        node.Depth = depth;
        var kids = childrenAccessor(node.Data);
        if (kids == null) return;
        foreach (var child in kids)
        {
            var childNode = new TreeNode<T>(child) { Parent = node };
            node.Children.Add(childNode);
            BuildHierarchy(childNode, childrenAccessor, depth + 1);
        }
    }

    private void NormalizePositions(TreeNode<T> root, int maxDepth)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        Visit(root, n =>
        {
            if (n.X < minX) minX = n.X;
            if (n.X > maxX) maxX = n.X;
        });

        double xRange = maxX - minX;
        if (xRange == 0) xRange = 1;
        double yRange = maxDepth == 0 ? 1 : maxDepth;

        double margin = 40;
        double usableWidth = _width - 2 * margin;
        double usableHeight = _height - 2 * margin;

        Visit(root, n =>
        {
            n.X = margin + (n.X - minX) / xRange * usableWidth;
            n.Y = margin + (double)n.Y / yRange * usableHeight;
        });
    }

    private static void Visit(TreeNode<T> node, Action<TreeNode<T>> action)
    {
        action(node);
        foreach (var child in node.Children) Visit(child, action);
    }

    /// <summary>Post-order traversal (children first, then parent).</summary>
    private static void VisitAfter(TreeNode<T> node, Action<TreeNode<T>> action)
    {
        foreach (var child in node.Children) VisitAfter(child, action);
        action(node);
    }
}

public static class ClusterLayout
{
    public static ClusterLayout<T> Create<T>() => new();
}
