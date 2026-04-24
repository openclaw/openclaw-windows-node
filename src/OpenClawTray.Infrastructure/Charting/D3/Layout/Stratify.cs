// Port of d3-hierarchy/src/stratify.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Builds a tree hierarchy from tabular (flat) data using id/parentId relationships.
/// Direct port of d3.stratify().
/// </summary>
public sealed class Stratify<T>
{
    private Func<T, string> _id = _ => "";
    private Func<T, string?> _parentId = _ => null;

    public Stratify<T> SetId(Func<T, string> id) { _id = id; return this; }
    public Stratify<T> SetParentId(Func<T, string?> parentId) { _parentId = parentId; return this; }

    /// <summary>
    /// Builds a TreeNode hierarchy from flat data.
    /// The root node is the one whose parentId is null.
    /// </summary>
    public TreeNode<T> Build(IEnumerable<T> data)
    {
        var nodes = new Dictionary<string, TreeNode<T>>();
        var items = data.ToList();

        // Create all nodes
        foreach (var item in items)
        {
            string id = _id(item);
            if (nodes.ContainsKey(id))
                throw new InvalidOperationException($"Duplicate id: {id}");
            nodes[id] = new TreeNode<T>(item);
        }

        // Link parent-child relationships
        TreeNode<T>? root = null;
        foreach (var item in items)
        {
            string id = _id(item);
            string? pid = _parentId(item);
            var node = nodes[id];

            if (pid == null || pid == "")
            {
                if (root != null)
                    throw new InvalidOperationException("Multiple roots found");
                root = node;
                node.Depth = 0;
            }
            else
            {
                if (!nodes.TryGetValue(pid, out var parent))
                    throw new InvalidOperationException($"Missing parent: {pid} for node {id}");
                node.Parent = parent;
                parent.Children.Add(node);
            }
        }

        if (root == null)
            throw new InvalidOperationException("No root found (no node with null parentId)");

        // Compute depths
        ComputeDepths(root, 0);

        return root;
    }

    /// <summary>
    /// Builds a TreemapNode hierarchy from flat data (for use with TreemapLayout).
    /// </summary>
    public TreemapNode<T> BuildTreemap(IEnumerable<T> data, Func<T, double> value)
    {
        var treeRoot = Build(data);
        return ConvertToTreemap(treeRoot, null, value);
    }

    /// <summary>
    /// Builds a PartitionNode hierarchy from flat data (for use with PartitionLayout).
    /// </summary>
    public PartitionNode<T> BuildPartition(IEnumerable<T> data, Func<T, double> value)
    {
        var treeRoot = Build(data);
        return ConvertToPartition(treeRoot, null, value);
    }

    private static void ComputeDepths(TreeNode<T> node, int depth)
    {
        node.Depth = depth;
        foreach (var child in node.Children)
            ComputeDepths(child, depth + 1);
    }

    private static TreemapNode<T> ConvertToTreemap(TreeNode<T> source, TreemapNode<T>? parent, Func<T, double> value)
    {
        var node = new TreemapNode<T> { Data = source.Data, Parent = parent, Depth = source.Depth };
        foreach (var child in source.Children)
            node.Children.Add(ConvertToTreemap(child, node, value));

        if (node.Children.Count == 0)
            node.Value = Math.Max(0, value(node.Data));
        else
            node.Value = node.Children.Sum(c => c.Value);

        return node;
    }

    private static PartitionNode<T> ConvertToPartition(TreeNode<T> source, PartitionNode<T>? parent, Func<T, double> value)
    {
        var node = new PartitionNode<T> { Data = source.Data, Parent = parent, Depth = source.Depth };
        foreach (var child in source.Children)
            node.Children.Add(ConvertToPartition(child, node, value));

        if (node.Children.Count == 0)
            node.Value = Math.Max(0, value(node.Data));
        else
            node.Value = node.Children.Sum(c => c.Value);

        return node;
    }
}

public static class Stratify
{
    public static Stratify<T> Create<T>() => new();
}
