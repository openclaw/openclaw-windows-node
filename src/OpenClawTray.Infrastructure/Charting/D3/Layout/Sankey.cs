// Port of d3-sankey — ISC License, Copyright 2015-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes the layout for a Sankey (flow) diagram.
/// Positions nodes in columns and computes link paths between them.
/// Direct port of d3-sankey.
/// </summary>
public sealed class SankeyLayout
{
    private double _x0 = 0, _y0 = 0, _x1 = 1, _y1 = 1;
    private double _nodeWidth = 24;
    private double _nodePadding = 8;
    private int _iterations = 6;
    private SankeyNodeAlign _align = SankeyNodeAlign.Justify;

    public SankeyLayout Size(double x1, double y1) { _x0 = 0; _y0 = 0; _x1 = x1; _y1 = y1; return this; }
    public SankeyLayout SetNodeWidth(double width) { _nodeWidth = width; return this; }
    public SankeyLayout SetNodePadding(double padding) { _nodePadding = padding; return this; }
    public SankeyLayout SetIterations(int iterations) { _iterations = iterations; return this; }
    public SankeyLayout SetAlign(SankeyNodeAlign align) { _align = align; return this; }

    /// <summary>
    /// Computes the Sankey layout for the given nodes and links.
    /// Modifies nodes and links in place with computed positions.
    /// </summary>
    public SankeyGraph Layout(SankeyGraph graph)
    {
        ComputeNodeLinks(graph);
        ComputeNodeValues(graph);
        ComputeNodeDepths(graph);
        ComputeNodeHeights(graph);
        ComputeNodeBreadths(graph);
        ComputeLinkBreadths(graph);
        return graph;
    }

    private static void ComputeNodeLinks(SankeyGraph graph)
    {
        // Build index
        var nodeIndex = new Dictionary<string, SankeyNode>();
        foreach (var node in graph.Nodes)
        {
            node.SourceLinks.Clear();
            node.TargetLinks.Clear();
            nodeIndex[node.Id] = node;
        }

        foreach (var link in graph.Links)
        {
            if (nodeIndex.TryGetValue(link.SourceId, out var source))
            {
                link.Source = source;
                source.SourceLinks.Add(link);
            }
            if (nodeIndex.TryGetValue(link.TargetId, out var target))
            {
                link.Target = target;
                target.TargetLinks.Add(link);
            }
        }
    }

    private static void ComputeNodeValues(SankeyGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            double sourceSum = node.SourceLinks.Sum(l => l.Value);
            double targetSum = node.TargetLinks.Sum(l => l.Value);
            node.Value = Math.Max(sourceSum, targetSum);
        }
    }

    private void ComputeNodeDepths(SankeyGraph graph)
    {
        var remaining = new HashSet<SankeyNode>(graph.Nodes);
        int depth = 0;

        while (remaining.Count > 0)
        {
            var current = remaining
                .Where(n => n.TargetLinks.All(l => l.Source == null || !remaining.Contains(l.Source)))
                .ToList();

            if (current.Count == 0) // Cycle detected, break
            {
                foreach (var n in remaining) n.Depth = depth;
                break;
            }

            foreach (var node in current)
            {
                node.Depth = depth;
                remaining.Remove(node);
            }
            depth++;
        }
    }

    private static void ComputeNodeHeights(SankeyGraph graph)
    {
        if (graph.Nodes.Count == 0) return;
        int maxDepth = graph.Nodes.Max(n => n.Depth);
        var remaining = new HashSet<SankeyNode>(graph.Nodes);
        int height = 0;

        while (remaining.Count > 0)
        {
            var current = remaining
                .Where(n => n.SourceLinks.All(l => l.Target == null || !remaining.Contains(l.Target)))
                .ToList();

            if (current.Count == 0)
            {
                foreach (var n in remaining) n.Height = height;
                break;
            }

            foreach (var node in current)
            {
                node.Height = height;
                remaining.Remove(node);
            }
            height++;
        }
    }

    private void ComputeNodeBreadths(SankeyGraph graph)
    {
        int maxDepth = graph.Nodes.Count > 0 ? graph.Nodes.Max(n => n.Depth) : 0;
        int columns = maxDepth + 1;

        // Horizontal position based on depth
        double kx = columns > 1 ? (_x1 - _x0 - _nodeWidth) / (columns - 1) : 0;
        foreach (var node in graph.Nodes)
        {
            int col = _align switch
            {
                SankeyNodeAlign.Left => node.Depth,
                SankeyNodeAlign.Right => maxDepth - node.Height,
                SankeyNodeAlign.Center => node.SourceLinks.Count > 0
                    ? node.Depth
                    : node.TargetLinks.Count > 0 ? maxDepth - node.Height : 0,
                _ => node.Depth, // Justify
            };

            node.X0 = _x0 + col * kx;
            node.X1 = node.X0 + _nodeWidth;
        }

        // Vertical position: stack within each column
        var byColumn = graph.Nodes
            .GroupBy(n => n.Depth)
            .OrderBy(g => g.Key);

        foreach (var group in byColumn)
        {
            var nodes = group.OrderByDescending(n => n.Value).ToList();
            double totalValue = nodes.Sum(n => n.Value);
            double availableHeight = _y1 - _y0;
            double totalPadding = Math.Max(0, (nodes.Count - 1) * _nodePadding);
            double ky = totalValue > 0 ? (availableHeight - totalPadding) / totalValue : 0;

            double y = _y0;
            foreach (var node in nodes)
            {
                node.Y0 = y;
                double h = node.Value * ky;
                node.Y1 = y + h;
                y += h + _nodePadding;
            }

            // Relax positions iteratively
            for (int i = 0; i < _iterations; i++)
                ResolveCollisions(nodes, _y0, _y1);
        }
    }

    private void ResolveCollisions(List<SankeyNode> nodes, double yMin, double yMax)
    {
        nodes.Sort((a, b) => a.Y0.CompareTo(b.Y0));

        double y = yMin;
        for (int i = 0; i < nodes.Count; i++)
        {
            double dy = y - nodes[i].Y0;
            if (dy > 0)
            {
                double h = nodes[i].Y1 - nodes[i].Y0;
                nodes[i].Y0 = y;
                nodes[i].Y1 = y + h;
            }
            y = nodes[i].Y1 + _nodePadding;
        }

        // Push back if exceeding bounds
        double overflow = y - _nodePadding - yMax;
        if (overflow > 0)
        {
            double h = nodes[^1].Y1 - nodes[^1].Y0;
            nodes[^1].Y0 -= overflow;
            nodes[^1].Y1 = nodes[^1].Y0 + h;

            for (int i = nodes.Count - 2; i >= 0; i--)
            {
                double gap = nodes[i].Y1 + _nodePadding - nodes[i + 1].Y0;
                if (gap > 0)
                {
                    double ih = nodes[i].Y1 - nodes[i].Y0;
                    nodes[i].Y0 -= gap;
                    nodes[i].Y1 = nodes[i].Y0 + ih;
                }
            }
        }
    }

    private static void ComputeLinkBreadths(SankeyGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            // Sort source links
            node.SourceLinks.Sort((a, b) => (a.Target?.Y0 ?? 0).CompareTo(b.Target?.Y0 ?? 0));
            node.TargetLinks.Sort((a, b) => (a.Source?.Y0 ?? 0).CompareTo(b.Source?.Y0 ?? 0));
        }

        // Cache link widths from finalized node positions
        foreach (var link in graph.Links)
        {
            if (link.Source != null)
                link.Width = Math.Max(1, link.Value * ((link.Source.Y1 - link.Source.Y0) / Math.Max(1, link.Source.Value)));
        }

        foreach (var node in graph.Nodes)
        {
            double y0 = node.Y0;
            foreach (var link in node.SourceLinks)
            {
                link.Y0 = y0 + link.Width / 2;
                y0 += link.Width;
            }

            double y1 = node.Y0;
            foreach (var link in node.TargetLinks)
            {
                link.Y1 = y1 + link.Width / 2;
                y1 += link.Width;
            }
        }
    }

    /// <summary>
    /// Generates the SVG path for a Sankey link (curved band).
    /// </summary>
    public static string? LinkPath(SankeyLink link, int? digits = 3)
    {
        if (link.Source == null || link.Target == null) return null;

        var path = digits is int d ? new PathBuilder(d) : new PathBuilder();
        double halfWidth = link.Width / 2;

        double x0 = link.Source.X1;
        double x1 = link.Target.X0;
        double xi = (x0 + x1) / 2; // control point x

        double y0top = link.Y0 - halfWidth;
        double y0bot = link.Y0 + halfWidth;
        double y1top = link.Y1 - halfWidth;
        double y1bot = link.Y1 + halfWidth;

        // Top edge
        path.MoveTo(x0, y0top);
        path.BezierCurveTo(xi, y0top, xi, y1top, x1, y1top);

        // Bottom edge (reverse)
        path.LineTo(x1, y1bot);
        path.BezierCurveTo(xi, y1bot, xi, y0bot, x0, y0bot);
        path.ClosePath();

        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }
}

/// <summary>The complete Sankey graph with nodes and links.</summary>
public sealed class SankeyGraph
{
    public List<SankeyNode> Nodes { get; set; } = [];
    public List<SankeyLink> Links { get; set; } = [];
}

/// <summary>A node in the Sankey diagram.</summary>
public sealed class SankeyNode
{
    public required string Id { get; init; }
    public string? Label { get; set; }
    public double Value { get; internal set; }
    public int Depth { get; internal set; }
    public int Height { get; internal set; }
    public double X0 { get; internal set; }
    public double X1 { get; internal set; }
    public double Y0 { get; internal set; }
    public double Y1 { get; internal set; }
    public List<SankeyLink> SourceLinks { get; } = [];
    public List<SankeyLink> TargetLinks { get; } = [];
}

/// <summary>A link (flow) in the Sankey diagram.</summary>
public sealed class SankeyLink
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public double Value { get; set; }
    public SankeyNode? Source { get; internal set; }
    public SankeyNode? Target { get; internal set; }
    public double Y0 { get; internal set; }
    public double Y1 { get; internal set; }
    public double Width { get; internal set; } = 1;
}

public enum SankeyNodeAlign
{
    Left,
    Right,
    Center,
    Justify
}
