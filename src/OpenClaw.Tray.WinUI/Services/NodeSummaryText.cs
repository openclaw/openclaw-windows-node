using System;
using System.Collections.Generic;
using System.Linq;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Pure projection for the "copy node summary" tray action: turns the current
/// gateway nodes into the plaintext block placed on the clipboard. Kept separate
/// from the clipboard/toast side effects so the formatting is unit-testable
/// without a running app.
/// </summary>
internal static class NodeSummaryText
{
    /// <summary>
    /// One line per node: "&lt;state&gt;: &lt;name&gt; (&lt;short id&gt;) · &lt;detail&gt;",
    /// where state is online/offline and name falls back to the short id when the
    /// node has no display name. Returns an empty string when there are no nodes.
    /// </summary>
    public static string Build(IReadOnlyList<GatewayNodeInfo> nodes)
    {
        if (nodes is null || nodes.Count == 0)
            return string.Empty;

        var lines = nodes.Select(node =>
        {
            var state = node.IsOnline ? "online" : "offline";
            var name = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName;
            return $"{state}: {name} ({node.ShortId}) · {node.DetailText}";
        });

        return string.Join(Environment.NewLine, lines);
    }
}
