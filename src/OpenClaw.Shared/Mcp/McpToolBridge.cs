using System;
using System.Collections.Generic;
using System.IO;
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
    /// The complete set of commands documented in <see cref="CommandDescriptions"/>.
    /// Exposed as a stable surface so out-of-process documentation (winnode's
    /// skill.md) can be drift-tested against the canonical capability surface.
    /// </summary>
    public static IReadOnlyCollection<string> KnownCommands => CommandDescriptions.Keys;

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
        ["canvas.a2ui.dump"] =
            "READ-ALL: Return the full state of every currently-rendered A2UI surface — the component tree, every data-model entry, and any registered secret paths (values redacted). Operators granting MCP access should treat this as equivalent to a screenshot of every open surface, not a normal observability tool.",
        ["canvas.caps"] =
            "Report the A2UI feature flags this canvas runtime supports (component catalog, max surfaces, render depth, value-size caps). Diagnostic; no side effects.",
        ["canvas.a2ui.pushJSONL"] =
            "Streaming variant of canvas.a2ui.push for very large surfaces. Same protocol contract; jsonlPath argument must live under the system temp directory and is opened via FileStream + GetFinalPathNameByHandle to defeat reparse-point traversal.",

        // screen.* — names match the canonical OpenClaw protocol
        // (apps/shared/OpenClawKit/Sources/OpenClawKit/ScreenCommands.swift).
        // No screen.list or screen.capture exist in the protocol; previous
        // drift advertised tools that didn't actually resolve.
        ["screen.snapshot"] =
            "Capture a screenshot of the specified display. Args: format ('png'|'jpeg', default 'png'), maxWidth (int, default 1920), quality (int 1-100, default 80), monitor / screenIndex (int, default 0 = primary), includePointer (bool, default true). Returns { format, width, height, base64, image } where image is a data: URL.",
        ["screen.record"] =
            "Record the specified display for a bounded duration. Args: durationMs (int, required, max 300000), format ('mp4'|'webm', default 'mp4'), monitor / screenIndex (int, default 0 = primary), maxWidth (int, default 1920), fps (int, default 30). Returns { format, durationMs, base64 }.",

        // camera.*
        ["camera.list"] =
            "List cameras attached to the Windows node. Returns { cameras: [{ deviceId, name, isDefault }, ...] }.",
        ["camera.snap"] =
            "Capture a still photo from a camera. Args: deviceId (string, optional — defaults to system default camera), format ('jpeg'|'png', default 'jpeg'), maxWidth (int, default 1280), quality (int 1-100, default 80). Returns { format, width, height, base64 }.",
        ["camera.clip"] =
            "Record a short clip from a camera. Args: deviceId (string, optional), durationMs (int, required, max 60000), format ('mp4'|'webm', default 'mp4'), maxWidth (int, default 1280). Returns { format, durationMs, base64 }.",

        // stt.* — microphone capture → text. Default-off; privacy-sensitive.
        // Single engine: Whisper.net runs locally on the device.
        ["stt.transcribe"] =
            "Capture microphone audio for a bounded duration and return the transcribed text. Args: maxDurationMs (int, required, > 0, max 30000), language (string, optional BCP-47 tag like 'en-US' or 'auto' — falls back to the configured SttLanguage setting). Returns { transcribed, text, durationMs, language, engineEffective ('whisper') }. Whisper model is downloaded on first use; until then this returns an error pointing to Voice Settings. Requires NodeSttEnabled.",
        ["stt.listen"] =
            "Capture microphone audio with voice-activity detection and return when the user stops speaking, or after timeoutMs. Args: timeoutMs (int, optional, default 30000, range 1000..120000), language (string, optional BCP-47 tag or 'auto', default 'auto'). Returns { text, language, durationMs, segments[{ text, startMs, endMs }], engineEffective ('whisper') }. Result is the full silence-bounded utterance (all Whisper segments concatenated), not a partial first segment. Requires NodeSttEnabled.",
        ["stt.status"] =
            "Report STT engine readiness. No args. Returns { engine ('whisper'), readiness ('ready'|'initializing'|'model-downloading'|'model-not-downloaded'|'unavailable'), modelDownloadProgress (0..1 or null), isListenWithVadSupported (bool), isBoundedTranscribeSupported (bool) }. Carries no PII (no transcript history, no language history, no device IDs, no model paths).",

        // tts.*
        ["tts.speak"] =
            "Speak text aloud on the Windows node. Args: text (string, required), provider ('piper'|'windows'|'elevenlabs', optional — falls back to the configured TtsProvider setting, default 'piper' for fresh installs), voiceId (string, optional — overrides the per-provider configured voice), model (string, optional, ElevenLabs only), interrupt (bool, default false — interrupts any in-progress playback). Returns { spoken, provider, contentType, durationMs }.",

        // app.*
        ["app.navigate"] =
            "Navigate the companion app to a specific page (e.g., 'home', 'sessions', 'settings'). Args: page (string, required). Returns { navigated, page }.",
        ["app.status"] =
            "Get current connection status, node state, and gateway info. Returns { connectionStatus, nodeConnected, nodePaired, nodePendingApproval, gatewayVersion, sessionCount, nodeCount }.",
        ["app.sessions"] =
            "List active sessions with optional agent filter. Args: agentId (string, optional). Returns array of { Key, Status, Model, AgeText, tokens }.",
        ["app.agents"] =
            "List agents from the connected gateway. Returns the raw agents JSON array.",
        ["app.nodes"] =
            "List connected nodes and their capabilities. Returns array of { DisplayName, NodeId, IsOnline, Platform, CapabilityCount }.",
        ["app.config.get"] =
            "Read gateway configuration value at a dot-path. Args: path (string, optional). Returns the config subtree or full config.",
        ["app.settings.get"] =
            "Read a local app setting by name. Args: name (string, required). Returns the setting value.",
        ["app.settings.set"] =
            "Set a local app setting (name and value). Args: name (string, required), value (string, required). Returns { name, value }.",
        ["app.menu"] =
            "Get tray menu state (status, session count, node count). Returns array of menu items.",
        ["app.search"] =
            "Search the command palette and return matching commands. Args: query (string, required). Returns array of { Title, Subtitle, Icon }.",
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
        INodeCapability? capability = null;
        foreach (var c in caps)
        {
            if (!c.CanHandle(name)) continue;
            capability = c;
            break;
        }
        if (capability == null)
            throw new McpToolException($"Unknown tool: {name}");

        var request = new NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString(),
            Command = name,
            Args = args,
        };

        _logger.Debug($"[MCP] tools/call {name}");
        // Pass the cancellation token through. Capabilities that override the
        // CT-aware overload (long-running screen/camera capture) will stop
        // their underlying pipeline on timeout; legacy capabilities fall back
        // to the no-CT signature and still benefit from WaitAsync freeing the
        // bridge's handler slot.
        NodeInvokeResponse response;
        try
        {
            response = await capability.ExecuteAsync(request, cancellationToken).WaitAsync(cancellationToken);
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
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
