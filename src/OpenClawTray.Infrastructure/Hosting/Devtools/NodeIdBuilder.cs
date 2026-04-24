namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// A descriptor of a live UI node captured during a tree walk. The id is derived
/// purely from the descriptor — there is no reference to the live element on this
/// record, so the builder can be exercised in a pure unit test.
/// </summary>
internal sealed record NodeDescriptor(
    string WindowId,
    string? ComponentName,
    string? AutomationId,
    ReactorSourceRef? ReactorSource,
    string TypeName,
    int SiblingIndex,
    NodeDescriptor? StableAncestor);

internal sealed record ReactorSourceRef(string File, int Line, int SiblingIndex);

/// <summary>
/// Builds stable <c>r:&lt;window&gt;/&lt;local&gt;</c> ids per spec §13. The three rules,
/// tried in order:
///   1. Prefer <c>AutomationId</c>.
///   2. Else, if a Reactor source map resolves, use the source file/line/sibling.
///   3. Else, a content-addressed path from the nearest stable ancestor.
/// The same live element re-walked later must produce the same id as long as its
/// identity-signalling properties (AutomationId, source location, sibling path)
/// haven't changed.
/// </summary>
internal static class NodeIdBuilder
{
    public static string Build(NodeDescriptor node)
    {
        var local = BuildLocal(node);
        return $"r:{node.WindowId}/{local}";
    }

    private static string BuildLocal(NodeDescriptor node)
    {
        var componentPrefix = string.IsNullOrEmpty(node.ComponentName) ? node.TypeName : node.ComponentName!;

        if (!string.IsNullOrEmpty(node.AutomationId))
            return $"{componentPrefix}.{node.AutomationId}";

        if (node.ReactorSource is { } src)
            return $"{componentPrefix}.{src.File}:{src.Line}:{src.SiblingIndex}";

        // Content-addressed: walk the ancestor chain until either the root or an
        // ancestor with a stable (AutomationId / source) id, then append the type
        // + sibling-index path from that anchor.
        var segments = new List<string> { $"{node.TypeName}[{node.SiblingIndex}]" };
        var cur = node.StableAncestor;
        while (cur is not null)
        {
            if (!string.IsNullOrEmpty(cur.AutomationId) || cur.ReactorSource is not null)
            {
                // Anchor found — emit its local id first, then the path.
                var anchor = BuildLocal(cur);
                segments.Reverse();
                return $"{anchor}/~{string.Join('/', segments)}";
            }
            segments.Add($"{cur.TypeName}[{cur.SiblingIndex}]");
            cur = cur.StableAncestor;
        }

        segments.Reverse();
        return $"{componentPrefix}~/{string.Join('/', segments)}";
    }
}
