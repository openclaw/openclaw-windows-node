using OpenClawTray.Infrastructure.Charting.Accessibility;
using OpenClawTray.Infrastructure.Charting.D3;
// Tree and Force Graph chart DSL for Reactor integration

using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Hosting;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinShapes = Microsoft.UI.Xaml.Shapes;
using static OpenClawTray.Infrastructure.Charting.D3Dsl;

namespace OpenClawTray.Infrastructure.Charting;

public static partial class ChartDsl
{
    /// <summary>
    /// Creates a tree diagram from hierarchical data.
    /// </summary>
    public static TreeChartElement<T> TreeChart<T>(
        T root,
        Func<T, IEnumerable<T>?> children,
        Func<T, string>? label = null) =>
        new()
        {
            Root = root,
            ChildrenAccessor = children,
            LabelAccessor = label,
        };

    /// <summary>
    /// Creates a force-directed graph.
    /// </summary>
    public static ForceGraphElement ForceGraph(
        IReadOnlyList<ForceNode> nodes,
        IReadOnlyList<ForceLink> links) =>
        new()
        {
            InputNodes = nodes,
            InputLinks = links,
        };
}

/// <summary>
/// Tree diagram element for Reactor's virtual tree — renders as native D3 elements.
/// </summary>
public sealed class TreeChartElement<T> : IChartAccessibilityData
{
    internal T Root { get; init; } = default!;
    internal Func<T, IEnumerable<T>?> ChildrenAccessor { get; init; } = _ => null;
    internal Func<T, string>? LabelAccessor { get; init; }

    private double _width = 600;
    private double _height = 400;
    private string _linkColor = "#999999";
    private string _nodeColor = "#4285f4";
    private double _nodeRadius = 6;
    private Action<TreeChartHandle>? _onReady;

    // Accessibility fields
    private string? _title;
    private string? _description;
    private Element? _alternateView;

    public TreeChartElement<T> Width(double w) { _width = w; return this; }
    public TreeChartElement<T> Height(double h) { _height = h; return this; }
    public TreeChartElement<T> LinkColor(string c) { _linkColor = c; return this; }
    public TreeChartElement<T> NodeColor(string c) { _nodeColor = c; return this; }
    public TreeChartElement<T> NodeRadius(double r) { _nodeRadius = r; return this; }
    public TreeChartElement<T> OnReady(Action<TreeChartHandle> callback) { _onReady = callback; return this; }

    /// <summary>Sets visible title + accessible name for the chart.</summary>
    public TreeChartElement<T> Title(string title) { _title = title; return this; }

    /// <summary>Overrides auto-generated accessible description/summary.</summary>
    public TreeChartElement<T> Description(string description) { _description = description; return this; }

    /// <summary>Enables alternate-view toggle (T / Alt+Shift+F11).</summary>
    public TreeChartElement<T> AlternateView(Element view) { _alternateView = view; return this; }

    public Element ToElement()
    {
        var chart = BuildElement(Root);
        if (_alternateView is { } alt)
            chart = Accessibility.ChartAlternateViewWrapper.Wrap(chart, alt);
        return chart;
    }
    public static implicit operator Element(TreeChartElement<T> chart) => chart.ToElement();

    private Element BuildElement(T rootData)
    {
        var layout = TreeLayout.Create<T>().Size(_width, _height);
        var root = layout.Hierarchy(rootData, ChildrenAccessor);
        layout.Layout(root);

        var linkBrush = Brush(_linkColor);
        var nodeBrush = Brush(_nodeColor);
        var whiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
        var textBrush = Gray(60, 200);

        var allNodes = root.Descendants().ToList();

        // Links (drawn behind nodes)
        var links = allNodes
            .SelectMany((node, ni) => node.Children.Select((child, ci) =>
                (Element)D3Link(node.X, node.Y, child.X, child.Y,
                    stroke: linkBrush, strokeWidth: 1.5)
                    .WithKey($"link-{ni}-{ci}")))
            .ToArray();

        // Nodes
        var nodes = allNodes.Select((node, i) =>
            (Element)(D3Circle(node.X, node.Y, _nodeRadius)
                with
                {
                    Fill = node.Children.Count > 0 ? nodeBrush : whiteBrush,
                    Stroke = nodeBrush,
                    StrokeThickness = 2,
                    Key = $"node-{i}",
                }))
            .ToArray();

        // Labels
        var labels = LabelAccessor != null
            ? allNodes.Select((node, i) =>
            {
                double labelX = node.Children.Count > 0 ? node.X - 15 : node.X + _nodeRadius + 4;
                double labelY = node.Children.Count > 0 ? node.Y - _nodeRadius - 14 : node.Y - 6;
                return (Element)D3Dsl.Text(labelX, labelY, LabelAccessor(node.Data), 10, textBrush)
                    .WithKey($"label-{i}");
            }).ToArray()
            : [];

        var canvas = D3Canvas(_width, _height, [.. links, .. nodes, .. labels]);

        if (_onReady is { } cb)
            canvas = canvas.Set(c => cb(new TreeChartHandle(c)));

        return canvas with { ChartData = this };
    }

    // ── IChartAccessibilityData ──────────────────────────────────────

    string? IChartAccessibilityData.Name => _title;
    string? IChartAccessibilityData.Description => _description;
    string IChartAccessibilityData.ChartTypeName => "Tree";

    IReadOnlyList<ChartSeriesDescriptor> IChartAccessibilityData.Series
    {
        get
        {
            var layout = TreeLayout.Create<T>().Size(_width, _height);
            var root = layout.Hierarchy(Root, ChildrenAccessor);
            layout.Layout(root);
            var allNodes = root.Descendants().ToList();

            // Tree charts expose nodes as a single series with label and depth
            var points = allNodes.Select((node, i) =>
            {
                var label = LabelAccessor?.Invoke(node.Data) ?? $"Node {i + 1}";
                return new ChartPointDescriptor(label, node.Depth);
            }).ToArray();

            return [new ChartSeriesDescriptor("Nodes", points)];
        }
    }

    IReadOnlyList<ChartAxisDescriptor> IChartAccessibilityData.Axes => [];
    ChartViewport? IChartAccessibilityData.Viewport => null;
}

/// <summary>
/// Handle returned by OnReady — exposes the underlying Canvas for escape-hatch scenarios.
/// </summary>
public sealed class TreeChartHandle
{
    private readonly Canvas _canvas;
    internal TreeChartHandle(Canvas canvas) { _canvas = canvas; }
    public Canvas Canvas => _canvas;

    /// <summary>Re-renders the tree with new data. Prefer state-driven re-renders instead.</summary>
    [Obsolete("Use state-driven re-renders (e.g. setRoot(newRoot)) instead of TreeChartHandle.Redraw. " +
              "Tree charts are now native Reactor elements that diff efficiently.")]
    public void Redraw<T>(T root) { }
}

/// <summary>
/// Force-directed graph element for Reactor's virtual tree.
/// Pure renderer — draws nodes, links, labels from a ForceSimulation's current state.
/// Interaction (drag, animation) is the caller's responsibility.
///
/// Note: ForceGraph intentionally uses XamlHostElement for 60fps direct manipulation
/// via SyncPositions(). See ductd3-native-chart-migration.md §5, Option A.
/// </summary>
public sealed class ForceGraphElement : IChartAccessibilityData
{
    internal IReadOnlyList<ForceNode> InputNodes { get; init; } = [];
    internal IReadOnlyList<ForceLink> InputLinks { get; init; } = [];

    private double _width = 600;
    private double _height = 400;
    private string _linkColor = "#cccccc";
    private string _nodeColor = "#4285f4";
    private double _chargeStrength = -100;
    private double _linkDistance = 60;
    private int _iterations = 300;
    private Action<ForceGraphHandle>? _onReady;

    // Accessibility fields
    private string? _title;
    private string? _description;
    private Element? _alternateView;

    public ForceGraphElement Width(double w) { _width = w; return this; }
    public ForceGraphElement Height(double h) { _height = h; return this; }
    public ForceGraphElement LinkColor(string c) { _linkColor = c; return this; }
    public ForceGraphElement NodeColor(string c) { _nodeColor = c; return this; }
    public ForceGraphElement Charge(double strength) { _chargeStrength = strength; return this; }
    public ForceGraphElement Distance(double d) { _linkDistance = d; return this; }
    public ForceGraphElement Iterations(int n) { _iterations = n; return this; }

    /// <summary>Sets visible title + accessible name for the graph.</summary>
    public ForceGraphElement Title(string title) { _title = title; return this; }

    /// <summary>Overrides auto-generated accessible description/summary.</summary>
    public ForceGraphElement Description(string description) { _description = description; return this; }

    /// <summary>Enables alternate-view toggle (T / Alt+Shift+F11).</summary>
    public ForceGraphElement AlternateView(Element view) { _alternateView = view; return this; }

    /// <summary>
    /// Called after the graph is rendered with a handle that exposes the simulation,
    /// WinUI elements, and a <c>SyncPositions</c> method. Use this to wire up
    /// drag behaviour, animation timers, or anything else from the call site.
    /// </summary>
    public ForceGraphElement OnReady(Action<ForceGraphHandle> callback) { _onReady = callback; return this; }

    public Element ToElement()
    {
        Element chart = new XamlHostElement(BuildCanvas, UpdateCanvas) { TypeKey = "ChartingD3Force" };
        if (_alternateView is { } alt)
            chart = Accessibility.ChartAlternateViewWrapper.Wrap(chart, alt);
        return chart;
    }
    public static implicit operator Element(ForceGraphElement chart) => chart.ToElement();

    private ForceSimulation? _sim;

    private FrameworkElement BuildCanvas()
    {
        var canvas = new Canvas
        {
            Width = _width,
            Height = _height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        RenderForceGraph(canvas);
        return canvas;
    }

    private void UpdateCanvas(FrameworkElement fe)
    {
        if (fe is Canvas canvas)
        {
            canvas.Children.Clear();
            canvas.Width = _width;
            canvas.Height = _height;
            RenderForceGraph(canvas);
        }
    }

    private void RenderForceGraph(Canvas canvas)
    {
        if (InputNodes.Count == 0) return;

        if (_sim == null)
        {
            _sim = new ForceSimulation()
                .SetNodes(InputNodes)
                .SetLinks(InputLinks)
                .ChargeStrength(_chargeStrength)
                .Center(_width / 2, _height / 2)
                .LinkDistance(_linkDistance)
                .CollisionRadius(12)
                .InitializePositions()
                .Run(_iterations);
        }

        var linkBrush = ChartElement<object>.ColorToBrush(_linkColor);
        var palette = D3Color.Category10;

        var lines = new WinShapes.Line[_sim.Links.Count];
        var ellipses = new WinShapes.Ellipse[_sim.Nodes.Count];
        var labels = new TextBlock?[_sim.Nodes.Count];

        // Draw links
        for (int li = 0; li < _sim.Links.Count; li++)
        {
            var link = _sim.Links[li];
            if (link.Source < 0 || link.Source >= _sim.Nodes.Count ||
                link.Target < 0 || link.Target >= _sim.Nodes.Count) continue;

            var s = _sim.Nodes[link.Source];
            var t = _sim.Nodes[link.Target];
            var line = new WinShapes.Line
            {
                X1 = s.X, Y1 = s.Y,
                X2 = t.X, Y2 = t.Y,
                Stroke = linkBrush,
                StrokeThickness = 1,
            };
            lines[li] = line;
            canvas.Children.Add(line);
        }

        // Draw nodes
        for (int i = 0; i < _sim.Nodes.Count; i++)
        {
            var node = _sim.Nodes[i];
            var color = palette[i % palette.Count];
            var brush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                (byte)(color.Opacity * 255), color.R, color.G, color.B));

            var ellipse = new WinShapes.Ellipse
            {
                Width = node.Radius * 2,
                Height = node.Radius * 2,
                Fill = brush,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(ellipse, node.X - node.Radius);
            Canvas.SetTop(ellipse, node.Y - node.Radius);
            ellipses[i] = ellipse;
            canvas.Children.Add(ellipse);

            if (node.Label != null)
            {
                var label = new TextBlock
                {
                    Text = node.Label,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(180, 60, 60, 60)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, node.X + node.Radius + 3);
                Canvas.SetTop(label, node.Y - 7);
                labels[i] = label;
                canvas.Children.Add(label);
            }
        }

        // Hand the live references to the caller
        _onReady?.Invoke(new ForceGraphHandle(_sim, canvas, ellipses, labels, lines));
    }

    // ── IChartAccessibilityData ──────────────────────────────────────

    string? IChartAccessibilityData.Name => _title;
    string? IChartAccessibilityData.Description => _description;
    string IChartAccessibilityData.ChartTypeName => "Force graph";

    IReadOnlyList<ChartSeriesDescriptor> IChartAccessibilityData.Series
    {
        get
        {

            // Force graphs expose edges as {source, target, weight} rows
            var points = InputLinks.Select((link, i) =>
            {
                var sourceName = link.Source >= 0 && link.Source < InputNodes.Count
                    ? InputNodes[link.Source].Label ?? $"Node {link.Source}"
                    : $"Node {link.Source}";
                var targetName = link.Target >= 0 && link.Target < InputNodes.Count
                    ? InputNodes[link.Target].Label ?? $"Node {link.Target}"
                    : $"Node {link.Target}";

                return new ChartPointDescriptor(
                    $"{sourceName} → {targetName}",
                    link.Strength,
                    $"{sourceName} to {targetName}, weight {link.Strength}");
            }).ToArray();

            return [new ChartSeriesDescriptor("Edges", points)];
        }
    }

    IReadOnlyList<ChartAxisDescriptor> IChartAccessibilityData.Axes => [];
    ChartViewport? IChartAccessibilityData.Viewport => null;
}

/// <summary>
/// Exposes the live simulation and WinUI elements so callers can implement
/// drag, animation, hover, or any other interaction outside the library.
/// </summary>
public sealed class ForceGraphHandle
{
    public ForceSimulation Simulation { get; }
    public Canvas Canvas { get; }
    public WinShapes.Ellipse[] NodeEllipses { get; }
    public TextBlock?[] NodeLabels { get; }
    public WinShapes.Line[] LinkLines { get; }

    internal ForceGraphHandle(
        ForceSimulation sim, Canvas canvas,
        WinShapes.Ellipse[] ellipses, TextBlock?[] labels, WinShapes.Line[] lines)
    {
        Simulation = sim;
        Canvas = canvas;
        NodeEllipses = ellipses;
        NodeLabels = labels;
        LinkLines = lines;
    }

    /// <summary>
    /// Pushes current ForceNode positions into the WinUI elements (ellipses, labels, link endpoints).
    /// Call this after Simulation.Tick() to animate the display.
    /// </summary>
    public void SyncPositions()
    {
        var nodes = Simulation.Nodes;
        var links = Simulation.Links;

        for (int i = 0; i < nodes.Count && i < NodeEllipses.Length; i++)
        {
            var n = nodes[i];
            Canvas.SetLeft(NodeEllipses[i], n.X - n.Radius);
            Canvas.SetTop(NodeEllipses[i], n.Y - n.Radius);

            if (i < NodeLabels.Length && NodeLabels[i] is TextBlock lbl)
            {
                Canvas.SetLeft(lbl, n.X + n.Radius + 3);
                Canvas.SetTop(lbl, n.Y - 7);
            }
        }

        for (int li = 0; li < links.Count && li < LinkLines.Length; li++)
        {
            var link = links[li];
            if (LinkLines[li] == null) continue;
            if (link.Source >= 0 && link.Source < nodes.Count)
            {
                LinkLines[li].X1 = nodes[link.Source].X;
                LinkLines[li].Y1 = nodes[link.Source].Y;
            }
            if (link.Target >= 0 && link.Target < nodes.Count)
            {
                LinkLines[li].X2 = nodes[link.Target].X;
                LinkLines[li].Y2 = nodes[link.Target].Y;
            }
        }
    }
}
