using System.Text.Json;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Describes an MCP tool: the wire name, a short human-readable description,
/// and an input-schema fragment that `tools/list` echoes back to agents.
/// </summary>
internal sealed record McpToolDescriptor(
    string Name,
    string Description,
    object InputSchema);

/// <summary>
/// A tool handler receives the raw <c>params</c> element from the JSON-RPC call and
/// returns a JSON-serializable result (usually an anonymous record or Dictionary).
/// It may also throw <see cref="McpToolException"/> to surface a structured error
/// payload back to the agent without the server translating generic exceptions.
/// </summary>
internal delegate object? McpToolHandler(JsonElement? @params);

/// <summary>
/// Structured error raised by a tool handler. The wire error code + data blob are
/// serialized into the JSON-RPC error payload.
/// </summary>
internal sealed class McpToolException : Exception
{
    public int Code { get; }
    public object? Payload { get; }

    public McpToolException(string message, int code = JsonRpcErrorCodes.ToolExecution, object? data = null)
        : base(message)
    {
        Code = code;
        Payload = data;
    }
}

/// <summary>
/// Collects the set of tools the MCP server exposes. Order is preserved so
/// <c>tools/list</c> responses are stable across runs — helpful for cached agents.
/// </summary>
internal sealed class McpToolRegistry
{
    private readonly List<McpToolDescriptor> _descriptors = new();
    private readonly Dictionary<string, McpToolHandler> _handlers = new(StringComparer.Ordinal);

    public void Register(McpToolDescriptor descriptor, McpToolHandler handler)
    {
        if (_handlers.ContainsKey(descriptor.Name))
            throw new InvalidOperationException($"Tool '{descriptor.Name}' is already registered.");
        _descriptors.Add(descriptor);
        _handlers[descriptor.Name] = handler;
    }

    public IReadOnlyList<McpToolDescriptor> List() => _descriptors;

    public bool TryGet(string name, out McpToolHandler handler)
    {
        if (_handlers.TryGetValue(name, out var h))
        {
            handler = h;
            return true;
        }
        handler = _ => null;
        return false;
    }
}
