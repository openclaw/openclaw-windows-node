using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Which node fields the walker should populate. Summary is the cheap default
/// (spec §9); full adds layout/context/visual data used when chasing a layout bug.
/// </summary>
internal enum TreeView
{
    Summary,
    Full,
}

/// <summary>Node shape emitted by <c>reactor.tree</c>. Full-view fields are nullable and only set when view=full.</summary>
internal sealed class TreeNode
{
    // -- summary fields (always populated) --------------------------------------
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string? AutomationId { get; set; }
    public string? AutomationName { get; set; }
    public BoundsBox Bounds { get; set; }
    public string? Text { get; set; }
    public bool IsVisible { get; set; }
    public string? ParentId { get; set; }
    public List<string> ChildIds { get; set; } = new();
    public object? Reactor { get; set; }

    // -- full-view fields (populated only when view=full) -----------------------
    public string? TypeFullName { get; set; }
    public string? Tag { get; set; }
    public string? AutomationControlType { get; set; }

    /// <summary>Backing bool? keeps summary serialization tight — serializer skips nulls.</summary>
    public bool? IsEnabled { get; set; }

    public bool? IsKeyboardFocusable { get; set; }
    public SizeBox? DesiredSize { get; set; }
    public SizeBox? ActualSize { get; set; }
    public LayoutInfo? Layout { get; set; }
    public ContextInfo? Context { get; set; }
    public VisualInfo? Visual { get; set; }

    /// <summary>
    /// UIA patterns the element's automation peer exposes (full-view only).
    /// Lets agents look up "what verbs can I call here?" instead of discovering
    /// by trial-and-error. Sparse: omitted entirely when empty.
    /// </summary>
    public List<string>? SupportedPatterns { get; set; }
}

internal readonly record struct BoundsBox(double X, double Y, double Width, double Height);
internal readonly record struct SizeBox(double Width, double Height);

/// <summary>Default-on layout-debug fields (spec §9 "layout").</summary>
internal sealed class LayoutInfo
{
    public double[]? Margin { get; set; }
    public double[]? Padding { get; set; }
    public string? HorizontalAlignment { get; set; }
    public string? VerticalAlignment { get; set; }
    public string? HorizontalContentAlignment { get; set; }
    public string? VerticalContentAlignment { get; set; }
}

/// <summary>Constraint context from parent (spec §9 "context").</summary>
internal sealed class ContextInfo
{
    public string? ParentType { get; set; }
    public string? StackOrientation { get; set; }
    public int? GridRow { get; set; }
    public int? GridColumn { get; set; }
    public int? GridRowSpan { get; set; }
    public int? GridColumnSpan { get; set; }
    public double? CanvasLeft { get; set; }
    public double? CanvasTop { get; set; }
}

/// <summary>Visual-debug fields (spec §9 "visual"). Identity transforms and null clips are omitted.</summary>
internal sealed class VisualInfo
{
    public double? Opacity { get; set; }
    public BoundsBox? Clip { get; set; }
    public int? ZIndex { get; set; }
    public double[]? RenderTransform { get; set; }
}

/// <summary>
/// Walks a WinUI visual tree rooted at <c>Window.Content</c> and emits a flat
/// <see cref="TreeNode"/> array plus a pinned <c>$schema</c> tag. Must be called
/// on the UI dispatcher — <see cref="VisualTreeHelper.GetChild"/> requires it.
/// </summary>
internal sealed class TreeWalker
{
    public const string SchemaVersion = "reactor-tree/1";

    private readonly string _windowId;
    private readonly NodeRegistry _registry;
    private readonly TreeView _view;

    public TreeWalker(string windowId, NodeRegistry registry, TreeView view = TreeView.Summary)
    {
        _windowId = windowId;
        _registry = registry;
        _view = view;
    }

    /// <summary>Walks the subtree rooted at <paramref name="root"/> and returns flat nodes.</summary>
    public List<TreeNode> Walk(UIElement? root)
    {
        var list = new List<TreeNode>();
        if (root is null) return list;
        WalkInto(root, parent: null, parentElement: null, ancestor: null, siblingIndex: 0, list);
        return list;
    }

    private void WalkInto(
        UIElement element,
        NodeDescriptor? parent,
        UIElement? parentElement,
        NodeDescriptor? ancestor,
        int siblingIndex,
        List<TreeNode> sink)
    {
        var typeName = element.GetType().Name;
        var automationId = AutomationProperties.GetAutomationId(element);
        var automationName = AutomationProperties.GetName(element);
        var elementName = (element as FrameworkElement)?.Name;
        var componentName = InferComponentName(element) ?? parent?.ComponentName ?? typeName;

        var descriptor = new NodeDescriptor(
            WindowId: _windowId,
            ComponentName: componentName,
            AutomationId: string.IsNullOrEmpty(automationId) ? null : automationId,
            ReactorSource: null,
            TypeName: typeName,
            SiblingIndex: siblingIndex,
            StableAncestor: ancestor);

        var id = _registry.GetOrCreate(descriptor, element);

        var node = new TreeNode
        {
            Id = id,
            Type = typeName,
            Name = string.IsNullOrEmpty(elementName) ? null : elementName,
            AutomationId = string.IsNullOrEmpty(automationId) ? null : automationId,
            AutomationName = string.IsNullOrEmpty(automationName) ? null : automationName,
            Bounds = ReadBounds(element),
            Text = ExtractText(element),
            IsVisible = element.Visibility == Visibility.Visible,
            ParentId = parent is null ? null : NodeIdBuilder.Build(parent),
        };

        if (_view == TreeView.Full)
            PopulateFullView(node, element, parentElement);

        sink.Add(node);

        // Always thread the immediate parent through as the descriptor's ancestor
        // chain. NodeIdBuilder anchors to the nearest *stable* ancestor (one with
        // an AutomationId or source location); if none exists, it walks the full
        // chain up to the root and includes each parent's type+siblingIndex in
        // the content-addressed id. Without this, two siblings of the same type
        // with the same parent-local index but different grandparents would
        // collide — e.g., two TextBoxes each as the 2nd child of their own
        // StackPanel got identical ids before the fix.
        var nextAncestor = descriptor;

        int childCount = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is UIElement child)
            {
                WalkInto(child, parent: descriptor, parentElement: element, ancestor: nextAncestor, siblingIndex: i, sink);
                // Backfill the parent's childIds once we know the child id.
                var childDesc = new NodeDescriptor(
                    WindowId: _windowId,
                    ComponentName: InferComponentName(child) ?? componentName,
                    AutomationId: string.IsNullOrEmpty(AutomationProperties.GetAutomationId(child))
                        ? null
                        : AutomationProperties.GetAutomationId(child),
                    ReactorSource: null,
                    TypeName: child.GetType().Name,
                    SiblingIndex: i,
                    StableAncestor: nextAncestor);
                node.ChildIds.Add(NodeIdBuilder.Build(childDesc));
            }
        }
    }

    private static BoundsBox ReadBounds(UIElement element)
    {
        try
        {
            if (element is FrameworkElement fe)
                return new BoundsBox(0, 0, fe.ActualWidth, fe.ActualHeight);
        }
        catch { }
        return new BoundsBox(0, 0, 0, 0);
    }

    private static string? ExtractText(UIElement element) => element switch
    {
        TextBlock tb => tb.Text,
        TextBox tx => tx.Text,
        Button b => b.Content?.ToString(),
        ContentControl cc => cc.Content?.ToString(),
        _ => null,
    };

    private static void PopulateFullView(TreeNode node, UIElement element, UIElement? parentElement)
    {
        node.TypeFullName = element.GetType().FullName;
        node.Tag = ExtractTag(element);

        // AutomationControlType comes from the UIA peer; creating peers is cheap
        // in-process and safe on the UI dispatcher.
        try
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
            node.AutomationControlType = peer?.GetAutomationControlType().ToString();
        }
        catch { /* peer creation can throw for templated parts — leave null */ }

        if (element is Control ctl)
        {
            node.IsEnabled = ctl.IsEnabled;
            node.IsKeyboardFocusable = ctl.IsTabStop && ctl.IsEnabled;
        }
        else
        {
            node.IsEnabled = null;
            node.IsKeyboardFocusable = null;
        }

        if (element is FrameworkElement fe)
        {
            node.DesiredSize = new SizeBox(fe.DesiredSize.Width, fe.DesiredSize.Height);
            node.ActualSize = new SizeBox(fe.ActualWidth, fe.ActualHeight);
            node.Layout = BuildLayout(fe);
        }

        node.Context = BuildContext(element, parentElement);
        node.Visual = BuildVisual(element);
        node.SupportedPatterns = ProbeSupportedPatterns(element);
    }

    // Probes the element's UIA peer for each pattern an agent might reach for.
    // Creating the peer is cheap in-process and already used above for the
    // AutomationControlType probe, so the incremental cost is O(patterns).
    private static readonly (PatternInterface Pattern, string Name)[] s_probedPatterns = new[]
    {
        (PatternInterface.Invoke, "Invoke"),
        (PatternInterface.Toggle, "Toggle"),
        (PatternInterface.SelectionItem, "SelectionItem"),
        (PatternInterface.Selection, "Selection"),
        (PatternInterface.Value, "Value"),
        (PatternInterface.RangeValue, "RangeValue"),
        (PatternInterface.ExpandCollapse, "ExpandCollapse"),
        (PatternInterface.Scroll, "Scroll"),
        (PatternInterface.ScrollItem, "ScrollItem"),
        (PatternInterface.Text, "Text"),
        (PatternInterface.Grid, "Grid"),
        (PatternInterface.Table, "Table"),
    };

    private static List<string>? ProbeSupportedPatterns(UIElement element)
    {
        List<string>? supported = null;
        try
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
            if (peer is null) return null;
            foreach (var (pattern, name) in s_probedPatterns)
            {
                try
                {
                    if (peer.GetPattern(pattern) is not null)
                        (supported ??= new List<string>()).Add(name);
                }
                catch { /* individual pattern probes throw on templated parts */ }
            }
        }
        catch { /* peer creation can throw for templated parts */ }
        return supported;
    }

    private static string? ExtractTag(UIElement element)
    {
        if (element is FrameworkElement fe && fe.Tag is { } tag)
        {
            // Only primitives as a ToString; complex Tags are opaque per spec.
            return tag switch
            {
                string s => s,
                bool b => b ? "true" : "false",
                int i => i.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                long l => l.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                float f => f.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        return null;
    }

    private static LayoutInfo BuildLayout(FrameworkElement fe)
    {
        var info = new LayoutInfo
        {
            Margin = ToThickness(fe.Margin),
            HorizontalAlignment = fe.HorizontalAlignment.ToString(),
            VerticalAlignment = fe.VerticalAlignment.ToString(),
        };

        if (fe is Control ctl)
        {
            info.Padding = ToThickness(ctl.Padding);
            info.HorizontalContentAlignment = ctl.HorizontalContentAlignment.ToString();
            info.VerticalContentAlignment = ctl.VerticalContentAlignment.ToString();
        }
        else if (fe is ContentPresenter cp)
        {
            info.Padding = ToThickness(cp.Padding);
            info.HorizontalContentAlignment = cp.HorizontalContentAlignment.ToString();
            info.VerticalContentAlignment = cp.VerticalContentAlignment.ToString();
        }

        return info;
    }

    private static double[] ToThickness(Thickness t) => new[] { t.Left, t.Top, t.Right, t.Bottom };

    private static ContextInfo? BuildContext(UIElement element, UIElement? parent)
    {
        if (parent is null) return null;
        var ctx = new ContextInfo { ParentType = parent.GetType().Name };

        switch (parent)
        {
            case StackPanel sp:
                ctx.StackOrientation = sp.Orientation.ToString();
                break;
            case Grid when element is FrameworkElement gfe:
                ctx.GridRow = Grid.GetRow(gfe);
                ctx.GridColumn = Grid.GetColumn(gfe);
                var rowSpan = Grid.GetRowSpan(gfe);
                var colSpan = Grid.GetColumnSpan(gfe);
                if (rowSpan != 1) ctx.GridRowSpan = rowSpan;
                if (colSpan != 1) ctx.GridColumnSpan = colSpan;
                break;
            case Canvas:
                var left = Canvas.GetLeft(element);
                var top = Canvas.GetTop(element);
                if (!double.IsNaN(left)) ctx.CanvasLeft = left;
                if (!double.IsNaN(top)) ctx.CanvasTop = top;
                break;
        }

        return ctx;
    }

    private static VisualInfo? BuildVisual(UIElement element)
    {
        var visual = new VisualInfo { Opacity = element.Opacity };

        // Z-index is a Canvas attached property; applies anywhere but defaults to 0.
        var zIndex = Canvas.GetZIndex(element);
        if (zIndex != 0) visual.ZIndex = zIndex;

        if (element.Clip is RectangleGeometry rg)
        {
            var r = rg.Rect;
            visual.Clip = new BoundsBox(r.X, r.Y, r.Width, r.Height);
        }

        if (element.RenderTransform is Transform t && !IsIdentity(t))
            visual.RenderTransform = MatrixValues(t);

        // Strip fully-default visual blocks — identity transform, no clip,
        // opacity 1, no z-index — to honor the spec's "skip identity" rule.
        bool isDefault =
            (visual.Opacity is null or 1.0) &&
            visual.Clip is null &&
            visual.ZIndex is null &&
            visual.RenderTransform is null;
        return isDefault ? null : visual;
    }

    private static bool IsIdentity(Transform t)
    {
        var m = t.TransformPoint(new global::Windows.Foundation.Point(0, 0));
        var mx = t.TransformPoint(new global::Windows.Foundation.Point(1, 0));
        var my = t.TransformPoint(new global::Windows.Foundation.Point(0, 1));
        return m.X == 0 && m.Y == 0 && mx.X == 1 && mx.Y == 0 && my.X == 0 && my.Y == 1;
    }

    private static double[] MatrixValues(Transform t)
    {
        // Return the 3×2 affine matrix [m11, m12, m21, m22, offsetX, offsetY]
        // by sampling three reference points — avoids naming every Transform subclass.
        var o = t.TransformPoint(new global::Windows.Foundation.Point(0, 0));
        var x = t.TransformPoint(new global::Windows.Foundation.Point(1, 0));
        var y = t.TransformPoint(new global::Windows.Foundation.Point(0, 1));
        return new[] { x.X - o.X, x.Y - o.Y, y.X - o.X, y.Y - o.Y, o.X, o.Y };
    }

    /// <summary>
    /// Best-effort component inference. The root component instance is threaded
    /// into a window tag by the host (Phase 2.8+ wiring); without that tag the
    /// walker falls back to the element's type name so ids still work.
    /// </summary>
    private static string? InferComponentName(UIElement element)
    {
        // Reserved hook for source-map integration (Phase 3 §3.2). For now the
        // root component name is supplied by the caller through the initial
        // descriptor chain; nested components get their own name once the source
        // mapper lands.
        _ = element;
        return null;
    }
}

/// <summary>
/// Convenience payload with the pinned <c>$schema</c> tag alongside the flat
/// node array. The <c>$schema</c> property uses a literal JSON name — the
/// camelCase policy doesn't touch leading <c>$</c>.
/// </summary>
internal sealed class TreeResult
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = TreeWalker.SchemaVersion;

    public List<TreeNode> Nodes { get; set; } = new();
    public string? WindowId { get; set; }
}
