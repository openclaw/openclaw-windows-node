// Port of D3's tree layout (Reingold-Tilford algorithm)
// Computes x, y positions for nodes in a tree hierarchy.

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A node in a tree hierarchy. Wraps user data and holds layout results.
/// </summary>
public sealed class TreeNode<T>
{
    public T Data { get; }
    public TreeNode<T>? Parent { get; internal set; }
    public List<TreeNode<T>> Children { get; } = [];
    public int Depth { get; internal set; }
    public double X { get; internal set; }
    public double Y { get; internal set; }

    // Reingold-Tilford working fields
    internal double Prelim;
    internal double Mod;
    internal TreeNode<T>? Thread;
    internal TreeNode<T>? Ancestor;
    internal double Shift;
    internal double Change;
    internal int Number;

    public TreeNode(T data) { Data = data; Ancestor = this; }

    /// <summary>Returns all descendant nodes (including this one) in pre-order.</summary>
    public IEnumerable<TreeNode<T>> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var desc in child.Descendants())
                yield return desc;
    }

    /// <summary>Returns the ancestor that is a direct child of the root (for branch coloring). Returns this node if it has no parent.</summary>
    public TreeNode<T> TopAncestor
    {
        get
        {
            var current = this;
            while (current.Parent is { Parent: not null })
                current = current.Parent;
            return current;
        }
    }
}

/// <summary>
/// Computes a tidy tree layout using the Reingold-Tilford algorithm.
/// Port of d3-hierarchy's tree layout.
/// </summary>
public sealed class TreeLayout<T>
{
    private double _width = 400;
    private double _height = 400;
    private double _nodeSeparation = 1;

    public TreeLayout<T> Size(double width, double height) { _width = width; _height = height; return this; }
    public TreeLayout<T> Separation(double sep) { _nodeSeparation = sep; return this; }

    /// <summary>
    /// Build a tree from a root data item and a children accessor.
    /// </summary>
    public TreeNode<T> Hierarchy(T root, Func<T, IEnumerable<T>?> childrenAccessor)
    {
        var node = new TreeNode<T>(root);
        BuildHierarchy(node, childrenAccessor, 0);
        return node;
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

    /// <summary>
    /// Compute the layout positions for all nodes in the tree.
    /// After calling this, each node's X and Y are set.
    /// </summary>
    public TreeNode<T> Layout(TreeNode<T> root)
    {
        // Phase 1: Reingold-Tilford algorithm (bottom-up)
        FirstWalk(root);

        // Phase 2: Determine final x positions (top-down)
        SecondWalk(root, -root.Prelim);

        // Phase 3: Normalize to fit within the specified size
        NormalizePositions(root);

        return root;
    }

    private void FirstWalk(TreeNode<T> v)
    {
        if (v.Children.Count == 0)
        {
            // Leaf node
            var w = LeftSibling(v);
            v.Prelim = w != null ? w.Prelim + _nodeSeparation : 0;
        }
        else
        {
            var defaultAncestor = v.Children[0];

            for (int i = 0; i < v.Children.Count; i++)
            {
                var child = v.Children[i];
                child.Number = i;
                FirstWalk(child);
                defaultAncestor = Apportion(child, defaultAncestor);
            }

            ExecuteShifts(v);

            double midpoint = (v.Children[0].Prelim + v.Children[^1].Prelim) / 2;
            var leftSib = LeftSibling(v);
            if (leftSib != null)
            {
                v.Prelim = leftSib.Prelim + _nodeSeparation;
                v.Mod = v.Prelim - midpoint;
            }
            else
            {
                v.Prelim = midpoint;
            }
        }
    }

    private void SecondWalk(TreeNode<T> v, double m)
    {
        v.X = v.Prelim + m;
        v.Y = v.Depth;

        foreach (var child in v.Children)
        {
            SecondWalk(child, m + v.Mod);
        }
    }

    private TreeNode<T> Apportion(TreeNode<T> v, TreeNode<T> defaultAncestor)
    {
        var w = LeftSibling(v);
        if (w == null) return defaultAncestor;

        var vir = v;   // inner right
        var vor = v;   // outer right
        var vil = w;   // inner left
        var vol = vir.Parent!.Children[0]; // outer left

        double sir = vir.Mod;
        double sor = vor.Mod;
        double sil = vil.Mod;
        double sol = vol.Mod;

        while (NextRight(vil) != null && NextLeft(vir) != null)
        {
            vil = NextRight(vil)!;
            vir = NextLeft(vir)!;
            vol = NextLeft(vol)!;
            vor = NextRight(vor)!;
            vor.Ancestor = v;

            double shift = (vil.Prelim + sil) - (vir.Prelim + sir) + _nodeSeparation;
            if (shift > 0)
            {
                MoveSubtree(GetAncestor(vil, v, defaultAncestor), v, shift);
                sir += shift;
                sor += shift;
            }

            sil += vil.Mod;
            sir += vir.Mod;
            sol += vol.Mod;
            sor += vor.Mod;
        }

        if (NextRight(vil) != null && NextRight(vor) == null)
        {
            vor.Thread = NextRight(vil);
            vor.Mod += sil - sor;
        }

        if (NextLeft(vir) != null && NextLeft(vol) == null)
        {
            vol.Thread = NextLeft(vir);
            vol.Mod += sir - sol;
            defaultAncestor = v;
        }

        return defaultAncestor;
    }

    private static void ExecuteShifts(TreeNode<T> v)
    {
        double shift = 0, change = 0;
        for (int i = v.Children.Count - 1; i >= 0; i--)
        {
            var w = v.Children[i];
            w.Prelim += shift;
            w.Mod += shift;
            change += w.Change;
            shift += w.Shift + change;
        }
    }

    private static void MoveSubtree(TreeNode<T> wl, TreeNode<T> wr, double shift)
    {
        int subtrees = wr.Number - wl.Number;
        if (subtrees == 0) return;
        wr.Change -= shift / subtrees;
        wr.Shift += shift;
        wl.Change += shift / subtrees;
        wr.Prelim += shift;
        wr.Mod += shift;
    }

    private static TreeNode<T> GetAncestor(TreeNode<T> vil, TreeNode<T> v, TreeNode<T> defaultAncestor)
    {
        return vil.Ancestor is { } ancestor && ancestor.Parent == v.Parent ? ancestor : defaultAncestor;
    }

    private static TreeNode<T>? LeftSibling(TreeNode<T> v)
    {
        if (v.Parent == null) return null;
        int idx = v.Parent.Children.IndexOf(v);
        return idx > 0 ? v.Parent.Children[idx - 1] : null;
    }

    private static TreeNode<T>? NextLeft(TreeNode<T> v) =>
        v.Children.Count > 0 ? v.Children[0] : v.Thread;

    private static TreeNode<T>? NextRight(TreeNode<T> v) =>
        v.Children.Count > 0 ? v.Children[^1] : v.Thread;

    private void NormalizePositions(TreeNode<T> root)
    {
        // Find bounds
        double minX = double.MaxValue, maxX = double.MinValue;
        int maxDepth = 0;
        Visit(root, n =>
        {
            if (n.X < minX) minX = n.X;
            if (n.X > maxX) maxX = n.X;
            if (n.Depth > maxDepth) maxDepth = n.Depth;
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
            n.Y = margin + (double)n.Depth / yRange * usableHeight;
        });
    }

    private static void Visit(TreeNode<T> node, Action<TreeNode<T>> action)
    {
        action(node);
        foreach (var child in node.Children) Visit(child, action);
    }
}

public static class TreeLayout
{
    public static TreeLayout<T> Create<T>() => new();
}
