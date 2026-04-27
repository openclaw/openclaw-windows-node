using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Mcp;

/// <summary>
/// Transport-agnostic MCP server core. Auto-discovers tools from the live
/// <see cref="INodeCapability"/> registry — registering a new capability on
/// the node client immediately exposes its commands as MCP tools.
/// </summary>
public class McpToolBridge
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly Func<IReadOnlyList<INodeCapability>> _capabilityProvider;
    private readonly IOpenClawLogger _logger;
    private readonly string _serverName;
    private readonly string _serverVersion;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    public McpToolBridge(
        Func<IReadOnlyList<INodeCapability>> capabilityProvider,
        IOpenClawLogger? logger = null,
        string serverName = "openclaw-tray-mcp",
        string serverVersion = "0.0.0")
    {
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
        _logger = logger ?? NullLogger.Instance;
        _serverName = serverName;
        _serverVersion = serverVersion;
    }

    /// <summary>
    /// Dispatch a JSON-RPC request body and return the response body (or null
    /// for a JSON-RPC notification, which receives no response).
    /// </summary>
    public Task<string?> HandleRequestAsync(string requestBody)
        => HandleRequestAsync(requestBody, CancellationToken.None);

    /// <summary>
    /// Dispatch a JSON-RPC request body, observing a cancellation token (used
    /// by the HTTP transport to enforce a per-request deadline). When the
    /// token fires during a tool dispatch, the call surfaces as a tool error
    /// ("request timed out") so the slot is freed even if the underlying
    /// capability work continues to run.
    /// </summary>
    public async Task<string?> HandleRequestAsync(string requestBody, CancellationToken cancellationToken)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(requestBody);
        }
        catch (JsonException ex)
        {
            return WriteError(null, JsonRpcErrorCode.ParseError, $"Parse error: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return WriteError(null, JsonRpcErrorCode.InvalidRequest, "Request must be a JSON object");

            var idElement = root.TryGetProperty("id", out var idProp) ? idProp : (JsonElement?)null;
            var hasId = idElement.HasValue && idElement.Value.ValueKind != JsonValueKind.Null;

            if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                return hasId
                    ? WriteError(idElement, JsonRpcErrorCode.InvalidRequest, "Missing 'method'")
                    : null;
            }

            var method = methodProp.GetString()!;
            var paramsElement = root.TryGetProperty("params", out var p) ? p : default;

            try
            {
                object? result = method switch
                {
                    "initialize" => HandleInitialize(),
                    "ping" => new { },
                    "notifications/initialized" => null,
                    "tools/list" => HandleToolsList(),
                    "tools/call" => await HandleToolsCallAsync(paramsElement, cancellationToken),
                    // Some clients (notably Cursor) probe these on startup. Returning
                    // empty lists is friendlier than MethodNotFound — both feature sets
                    // are deferred but compatible by being absent rather than failing.
                    "resources/list" => new { resources = Array.Empty<object>() },
                    "prompts/list" => new { prompts = Array.Empty<object>() },
                    _ => throw new McpMethodNotFoundException(method),
                };

                if (!hasId) return null; // notification — no response
                return WriteResult(idElement, result ?? new { });
            }
            catch (McpMethodNotFoundException ex)
            {
                return hasId
                    ? WriteError(idElement, JsonRpcErrorCode.MethodNotFound, ex.Message)
                    : null;
            }
            catch (McpToolException ex)
            {
                return hasId
                    ? WriteToolError(idElement, ex.Message)
                    : null;
            }
            catch (Exception ex)
            {
                // Full exception with stack goes to the log; the wire response
                // gets a generic message so we don't leak internals to clients.
                _logger.Error($"[MCP] Handler error for {method}", ex);
                return hasId
                    ? WriteError(idElement, JsonRpcErrorCode.InternalError, "internal error")
                    : null;
            }
        }
    }

    private object HandleInitialize() => new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new
        {
            tools = new { listChanged = false },
        },
        serverInfo = new
        {
            name = _serverName,
            version = _serverVersion,
        },
    };

    private object HandleToolsList()
    {
        var caps = _capabilityProvider();
        var tools = new List<object>();
        foreach (var cap in caps)
        {
            foreach (var cmd in cap.Commands)
            {
                tools.Add(new
                {
                    name = cmd,
                    description = CommandDescriptions.TryGetValue(cmd, out var desc)
                        ? desc
                        : $"{cap.Category} capability: {cmd}",
                    inputSchema = new
                    {
                        type = "object",
                        additionalProperties = true,
                        properties = new { },
                    },
                });
            }
        }
        return new { tools };
    }

    /// <summary>
    /// Per-command descriptions advertised via <c>tools/list</c>. Sourced from
    /// the OpenClaw docs (docs/nodes/index.md, docs/platforms/mac/canvas.md) and
    /// the capability implementations under <c>OpenClaw.Shared.Capabilities</c>.
    /// Unknown commands fall back to a generic <c>{category} capability: {cmd}</c>
    /// label so newly-added capabilities still render before this table is updated.
    /// </summary>
    private static readonly Dictionary<string, string> CommandDescriptions = new(StringComparer.Ordinal)
    {
        // system.*
        ["system.notify"] =
            "Show a Windows toast notification on the node. Args: title (string, default 'OpenClaw'), body (string), subtitle (string), sound (bool, default true). Returns { sent: true }.",
        ["system.run"] =
            "Execute a shell command on the Windows node host. Args: command (string or string[] argv, required), args (string[]), shell (string), cwd (string), timeoutMs (int, default 30000), env (object). Subject to the local exec approval policy. Returns { stdout, stderr, exitCode, timedOut, durationMs }.",
        ["system.run.prepare"] =
            "Pre-flight a system.run invocation: returns the parsed execution plan (argv, cwd, rawCommand, agentId, sessionKey) without running anything. The gateway uses this to build its approval context before the actual run.",
        ["system.which"] =
            "Resolve executable names to absolute paths by searching PATH (PATHEXT-aware on Windows). Args: bins (string[], required). Returns { bins: { name: resolvedPath, ... } } including only names that were found.",
        ["system.execApprovals.get"] =
            "Return the current exec approval policy: { enabled, defaultAction ('allow'|'deny'|'prompt'), rules: [{ pattern, action, shells, description, enabled }, ...] }.",
        ["system.execApprovals.set"] =
            "Replace the exec approval policy. Args: rules (array of { pattern, action, shells?, description?, enabled? }), defaultAction (string, optional). Persisted to disk; used by future system.run calls.",

        // canvas.* — agent-controlled WebView2 panel for HTML/CSS/JS, A2UI, and small interactive UI surfaces.
        ["canvas.present"] =
            "Show the agent-controlled Canvas window (WebView2). Args: url (string) or html (string), width (int, default 800), height (int, default 600), x/y (int, -1 = center), title (string, default 'Canvas'), alwaysOnTop (bool, default false). The Canvas is a lightweight visual workspace for HTML/CSS/JS, A2UI, and small interactive UI surfaces.",
        ["canvas.hide"] =
            "Hide the Canvas window without destroying its state.",
        ["canvas.navigate"] =
            "Navigate the existing Canvas to a new location. Args: url (string, required) — accepts http(s), file://, or local canvas paths.",
        ["canvas.eval"] =
            "Evaluate a JavaScript expression inside the Canvas WebView and return its result. Args: script | javaScript | javascript (string, required).",
        ["canvas.snapshot"] =
            "Capture the Canvas viewport as a base64-encoded image. Args: format ('png'|'jpeg', default 'png'), maxWidth (int, default 1200), quality (int 1-100, default 80). Returns { format, base64 }.",
        ["canvas.a2ui.push"] =
            "Push A2UI v0.8 server→client messages to the Canvas as JSONL. Supported message kinds: beginRendering, surfaceUpdate, dataModelUpdate, deleteSurface (createSurface / v0.9 is rejected). Args: jsonl (string) or jsonlPath (string, must live under the system temp directory), props (object, optional).",
        ["canvas.a2ui.reset"] =
            "Reset the Canvas A2UI state, clearing any rendered surfaces.",

        // screen.*
        ["screen.capture"] =
            "Capture a screenshot of the specified display. Args: format ('png'|'jpeg', default 'png'), maxWidth (int, default 1920), quality (int 1-100, default 80), monitor / screenIndex (int, default 0 = primary), includePointer (bool, default true). Returns { format, width, height, base64, image } where image is a data: URL.",
        ["screen.list"] =
            "List attached displays. Returns { screens: [{ index, name, primary, bounds: {x,y,width,height}, workingArea: {x,y,width,height} }, ...] }.",

        // camera.*
        ["camera.list"] =
            "List cameras attached to the Windows node. Returns { cameras: [{ deviceId, name, isDefault }, ...] }.",
        ["camera.snap"] =
            "Capture a still photo from a camera. Args: deviceId (string, optional — defaults to system default camera), format ('jpeg'|'png', default 'jpeg'), maxWidth (int, default 1280), quality (int 1-100, default 80). Returns { format, width, height, base64 }.",
    };

    private async Task<object> HandleToolsCallAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new McpToolException("Invalid params: expected object");

        if (!parameters.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            throw new McpToolException("Missing 'name'");

        var name = nameProp.GetString()!;
        if (string.IsNullOrWhiteSpace(name))
            throw new McpToolException("Empty tool name");

        var args = parameters.TryGetProperty("arguments", out var argsProp) ? argsProp : default;
        if (args.ValueKind != JsonValueKind.Undefined
            && args.ValueKind != JsonValueKind.Null
            && args.ValueKind != JsonValueKind.Object)
        {
            throw new McpToolException("'arguments' must be a JSON object if present");
        }

        var caps = _capabilityProvider();
        var capability = caps.FirstOrDefault(c => c.CanHandle(name));
        if (capability == null)
            throw new McpToolException($"Unknown tool: {name}");

        var request = new NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString(),
            Command = name,
            Args = args,
        };

        _logger.Debug($"[MCP] tools/call {name}");
        // INodeCapability does not yet take a CancellationToken (changing it
        // would touch every gateway capability). WaitAsync gives the bridge a
        // hard deadline: when the request CT fires we abandon waiting and
        // return a tool error to free the handler slot. The capability's
        // underlying work may continue but cannot pin the MCP server.
        NodeInvokeResponse response;
        try
        {
            response = await capability.ExecuteAsync(request).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"[MCP] tools/call {name} timed out");
            throw new McpToolException("request timed out");
        }

        if (!response.Ok)
            throw new McpToolException(response.Error ?? "tool execution failed");

        var payloadJson = response.Payload is null
            ? "null"
            : JsonSerializer.Serialize(response.Payload, PayloadJsonOptions);

        return new
        {
            content = new[]
            {
                new { type = "text", text = payloadJson },
            },
            isError = false,
        };
    }

    private static string WriteResult(JsonElement? id, object result)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            WriteId(w, id);
            w.WritePropertyName("result");
            JsonSerializer.Serialize(w, result, PayloadJsonOptions);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string WriteError(JsonElement? id, int code, string message)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            WriteId(w, id);
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Tool execution failures are reported as a successful JSON-RPC result
    /// with isError=true (per MCP spec), not as a JSON-RPC error.
    /// </summary>
    private static string WriteToolError(JsonElement? id, string message)
    {
        var result = new
        {
            content = new[] { new { type = "text", text = message } },
            isError = true,
        };
        return WriteResult(id, result);
    }

    private static void WriteId(Utf8JsonWriter w, JsonElement? id)
    {
        w.WritePropertyName("id");
        if (!id.HasValue || id.Value.ValueKind == JsonValueKind.Null)
        {
            w.WriteNullValue();
            return;
        }
        switch (id.Value.ValueKind)
        {
            case JsonValueKind.Number:
                // Preserve the original number form — fractional, big-int, etc.
                // GetInt64 would throw on non-integer or out-of-range ids and
                // strip the request id from the error response, breaking the
                // client's response correlation.
                w.WriteRawValue(id.Value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.String:
                w.WriteStringValue(id.Value.GetString());
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }

    private static class JsonRpcErrorCode
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InternalError = -32603;
    }

    private sealed class McpMethodNotFoundException : Exception
    {
        public McpMethodNotFoundException(string method) : base($"Method not found: {method}") { }
    }

    private sealed class McpToolException : Exception
    {
        public McpToolException(string message) : base(message) { }
    }
}
