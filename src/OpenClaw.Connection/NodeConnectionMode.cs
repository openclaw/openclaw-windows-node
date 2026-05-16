namespace OpenClaw.Connection;

/// <summary>
/// Node connection modes. Determines how the node participates.
/// </summary>
public enum NodeConnectionMode
{
    /// <summary>Normal: connect to gateway via WebSocket as node.</summary>
    Gateway,
    /// <summary>Local-only: expose capabilities via MCP HTTP, no WebSocket.</summary>
    McpOnly,
    /// <summary>Node mode off.</summary>
    Disabled
}
