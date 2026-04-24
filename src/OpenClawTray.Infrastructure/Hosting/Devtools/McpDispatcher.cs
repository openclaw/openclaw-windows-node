using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OpenClawTray.Infrastructure.Core.Diagnostics;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Pure JSON-RPC dispatch for the MCP tool registry. Takes a request envelope,
/// returns a response envelope. No transport, no HTTP, no dispatcher hops.
/// <see cref="DevtoolsMcpServer"/> delegates to this on every request; tests
/// construct it directly with a registry of test-registered tools.
/// </summary>
internal sealed class McpDispatcher
{
    private readonly McpToolRegistry _tools;
    private readonly DevtoolsLogger? _logger;

    public McpDispatcher(McpToolRegistry tools) : this(tools, null) { }

    public McpDispatcher(McpToolRegistry tools, DevtoolsLogger? logger)
    {
        _tools = tools;
        _logger = logger;
    }

    public JsonRpcResponse Dispatch(string body)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(body, DevtoolsJsonContext.Default.JsonRpcRequest);
        }
        catch (JsonException ex)
        {
            return new JsonRpcResponse
            {
                Error = new JsonRpcError { Code = JsonRpcErrorCodes.ParseError, Message = $"Parse error: {ex.Message}" },
            };
        }

        if (request is null || string.IsNullOrEmpty(request.Method) || request.JsonRpc != "2.0")
        {
            return new JsonRpcResponse
            {
                Id = request?.Id,
                Error = new JsonRpcError { Code = JsonRpcErrorCodes.InvalidRequest, Message = "Invalid JSON-RPC request." },
            };
        }

        try
        {
            object? result = request.Method switch
            {
                "initialize" => HandleInitialize(request.Params),
                "ping" => new { },
                // Notifications have no response per JSON-RPC, but HTTP still
                // needs *something* on the wire. Returning an empty result
                // satisfies both strict MCP clients (which ignore the body
                // when id is absent) and curl-happy humans.
                var m when m.StartsWith("notifications/", StringComparison.Ordinal) => new { },
                "tools/list" => new
                {
                    tools = _tools.List().Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = t.InputSchema,
                    }).ToArray(),
                    // Extensions beyond the MCP spec: agents can read the
                    // selector grammar without a separate GET /mcp hop. MCP
                    // clients that strict-parse tools/list ignore unknown
                    // fields.
                    _selectorGrammar = DevtoolsMcpServer.SelectorGrammarDoc,
                    _treeSchemaVersion = TreeWalker.SchemaVersion,
                },
                // MCP resource / prompt surfaces are not implemented yet; return
                // the empty inventory so `initialize`-speaking clients don't fail
                // their discovery step.
                "resources/list" => new { resources = Array.Empty<object>() },
                "prompts/list" => new { prompts = Array.Empty<object>() },
                "tools/call" => HandleCall(request.Params),
                _ => HandleDirect(request.Method, request.Params),
            };
            return new JsonRpcResponse { Id = request.Id, Result = result };
        }
        catch (McpToolException ex)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = ex.Code, Message = ex.Message, Data = ex.Payload },
            };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = JsonRpcErrorCodes.InternalError, Message = ex.Message },
            };
        }
    }

    /// <summary>
    /// Responds to the MCP <c>initialize</c> handshake. Echoes the client's
    /// requested protocol version back if we recognize it; otherwise pins
    /// <c>2024-11-05</c> (the baseline MCP version this server targets).
    /// Capabilities advertise just <c>tools</c> — resources / prompts are stubbed
    /// in the dispatcher but not populated yet.
    /// </summary>
    private static object HandleInitialize(JsonElement? @params)
    {
        string? requested = null;
        if (@params is { } p && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
        {
            requested = pv.GetString();
        }

        var protocol = requested switch
        {
            "2024-11-05" => requested,
            "2025-03-26" => requested,
            _ => "2024-11-05",
        };

        return new
        {
            protocolVersion = protocol,
            capabilities = new
            {
                tools = new { listChanged = false },
            },
            serverInfo = new
            {
                name = "reactor-devtools",
                version = typeof(McpDispatcher).Assembly
                    .GetName().Version?.ToString() ?? "0.1.0",
            },
        };
    }

    private object? HandleCall(JsonElement? @params)
    {
        if (@params is not { } p || p.ValueKind != JsonValueKind.Object)
            throw new McpToolException("tools/call params must be an object with { name, arguments? }.",
                JsonRpcErrorCodes.InvalidParams);
        if (!p.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            throw new McpToolException("tools/call requires a string 'name' field.", JsonRpcErrorCodes.InvalidParams);

        var name = nameEl.GetString()!;
        JsonElement? args = p.TryGetProperty("arguments", out var argsEl) ? argsEl : null;
        return Invoke(name, args);
    }

    private object? HandleDirect(string method, JsonElement? @params)
    {
        if (_tools.TryGet(method, out _)) return Invoke(method, @params);
        throw new McpToolException($"Method not found: '{method}'", JsonRpcErrorCodes.MethodNotFound);
    }

    private object? Invoke(string name, JsonElement? @params)
    {
        if (!_tools.TryGet(name, out var handler))
            throw new McpToolException($"Tool not found: '{name}'", JsonRpcErrorCodes.MethodNotFound);

        bool traceMcp = ReactorEventSource.Log.IsEnabled(
            global::System.Diagnostics.Tracing.EventLevel.Informational,
            ReactorEventSource.Keywords.Mcp);

        // Fast path: neither the call log nor ETW tracing is active. Skip the
        // selector probe, Stopwatch, and try/catch plumbing entirely.
        if (_logger is null && !traceMcp)
            return handler(@params);

        var selector = TryReadSelector(@params);
        if (traceMcp)
            ReactorEventSource.Log.McpCallStart(name, selector ?? string.Empty);
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = handler(@params);
            sw.Stop();
            // Soft failures (tools like waitFor that return `{ok:false, reason:…}`
            // instead of throwing) should still show up as `err` in the log —
            // otherwise a grep for failed calls misses them. The handler didn't
            // throw, so the wire response is a normal 200 result; the log alone
            // reflects outcome.
            bool softOk = !HasOkFalse(result);
            _logger?.LogCall(name, selector, sw.ElapsedMilliseconds, success: softOk, resultCode: 0);
            if (traceMcp)
                ReactorEventSource.Log.McpCallStop(name, softOk, 0, sw.ElapsedMilliseconds);
            return result;
        }
        catch (McpToolException mte)
        {
            sw.Stop();
            _logger?.LogCall(name, selector, sw.ElapsedMilliseconds, success: false, resultCode: mte.Code);
            if (traceMcp)
                ReactorEventSource.Log.McpCallStop(name, false, mte.Code, sw.ElapsedMilliseconds);
            throw;
        }
        catch (Exception)
        {
            sw.Stop();
            _logger?.LogCall(name, selector, sw.ElapsedMilliseconds,
                success: false, resultCode: JsonRpcErrorCodes.InternalError);
            if (traceMcp)
                ReactorEventSource.Log.McpCallStop(name, false, JsonRpcErrorCodes.InternalError, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// True when <paramref name="result"/> is an object with an <c>ok</c> property
    /// whose value is explicitly <c>false</c>. Used to translate tool-level soft
    /// failures into <c>err</c> log lines.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "HasOkFalse uses reflection on result objects for devtools logging.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "HasOkFalse uses reflection on result objects for devtools logging.")]
    private static bool HasOkFalse(object? result)
    {
        if (result is null) return false;
        var prop = result.GetType().GetProperty("ok", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public);
        if (prop is null || prop.PropertyType != typeof(bool)) return false;
        return prop.GetValue(result) is bool b && !b;
    }

    private static string? TryReadSelector(JsonElement? @params)
    {
        if (@params is not { } p || p.ValueKind != JsonValueKind.Object) return null;
        if (p.TryGetProperty("selector", out var sel) && sel.ValueKind == JsonValueKind.String)
            return sel.GetString();
        return null;
    }
}
