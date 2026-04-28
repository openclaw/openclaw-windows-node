using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.A2UI.Actions;

/// <summary>
/// Per-node identity / session context the transport needs to format the
/// CANVAS_A2UI message. Supplied by NodeService — sessionKey can change over
/// the lifetime of the node (re-resolved on each delivery), the rest is
/// effectively immutable per process.
/// </summary>
public interface IGatewayActionContext
{
    /// <summary>Logical session the action should be appended to. Defaults to "main".</summary>
    string SessionKey { get; }
    /// <summary>Display name of this node (e.g. "Windows Node (DESKTOP-123)") — shown to the model as <c>host=</c>.</summary>
    string Host { get; }
    /// <summary>Stable per-device id, lowercased — shown to the model as <c>instance=</c>.</summary>
    string InstanceId { get; }
}

/// <summary>
/// Raised after a transport attempt so the renderer can clear a spinner / show
/// an error. Mirrors the Android <c>jsDispatchA2UIActionStatus</c> path but
/// stays in-process.
/// </summary>
public sealed class A2UIActionStatusEventArgs : EventArgs
{
    public required string ActionId { get; init; }
    public required bool Ok { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Sends A2UI actions to the gateway by formatting them as a tagged user
/// message and delivering through the gateway's <c>agent.request</c> node-event
/// channel (the same path Android uses, see
/// <c>NodeRuntime.handleCanvasA2UIActionFromWebView</c>). The gateway appends
/// the message as a user turn in the named session and runs an agent step;
/// there is no server-side action→tool registry — the model decides.
/// </summary>
public sealed class GatewayActionTransport : IA2UIActionTransport
{
    private readonly Func<WindowsNodeClient?> _clientProvider;
    private readonly IGatewayActionContext _context;
    private readonly IOpenClawLogger _logger;

    /// <summary>Raised after each delivery attempt — successful or not.</summary>
    public event EventHandler<A2UIActionStatusEventArgs>? ActionStatus;

    public GatewayActionTransport(
        Func<WindowsNodeClient?> clientProvider,
        IGatewayActionContext context,
        IOpenClawLogger logger)
    {
        _clientProvider = clientProvider;
        _context = context;
        _logger = logger;
    }

    public bool IsAvailable => _clientProvider()?.IsConnected == true;

    public async Task DeliverAsync(Protocol.A2UIAction action)
    {
        // Capture once: between IsAvailable and here the dispatcher may have
        // disconnected/recreated the client, and a second call to the provider
        // can return a different instance.
        var client = _clientProvider();
        if (client == null || !client.IsConnected)
        {
            RaiseStatus(action.Id, ok: false, error: "gateway not connected");
            throw new InvalidOperationException("Gateway not connected");
        }

        var payload = BuildAgentRequestPayload(action, _context);
        var sent = await client.SendNodeEventAsync("agent.request", payload).ConfigureAwait(false);
        if (!sent)
        {
            RaiseStatus(action.Id, ok: false, error: "send failed");
            throw new InvalidOperationException("Gateway send failed");
        }

        RaiseStatus(action.Id, ok: true, error: null);
    }

    /// <summary>
    /// Build the <c>agent.request</c> deep-link payload that the gateway
    /// receives via <c>node.event</c>. Pure helper — exposed for tests so the
    /// wire contract can be asserted without spinning up a real node client.
    /// </summary>
    public static JsonObject BuildAgentRequestPayload(Protocol.A2UIAction action, IGatewayActionContext context)
    {
        // Sanitize the top-level sessionKey, not just the value rendered into
        // the CANVAS_A2UI tag line. The gateway uses this field to *route* the
        // message to a session record; an unsanitized value can carry path
        // separators, control chars, or whitespace that the gateway never
        // expected. Match the same character class as the tag formatter.
        var rawSessionKey = string.IsNullOrWhiteSpace(context.SessionKey) ? "main" : context.SessionKey;
        var sessionKey = AgentMessageFormatter.SanitizeTagValue(rawSessionKey);
        if (sessionKey == "-") sessionKey = "main";

        var contextJson = action.Context?.ToJsonString();

        var message = AgentMessageFormatter.FormatAgentMessage(
            actionName: action.Name,
            sessionKey: sessionKey,
            surfaceId: action.SurfaceId,
            sourceComponentId: action.SourceComponentId ?? string.Empty,
            host: context.Host,
            instanceId: context.InstanceId,
            contextJson: contextJson);

        // deliver=false keeps the raw CANVAS_A2UI line out of the visible
        // chat; only the model's response is shown to the user. thinking=low
        // matches the Android budget hint for a quick agentic step.
        return new JsonObject
        {
            ["message"] = message,
            ["sessionKey"] = sessionKey,
            ["thinking"] = "low",
            ["deliver"] = false,
            ["key"] = action.Id,
        };
    }

    private void RaiseStatus(string actionId, bool ok, string? error)
    {
        try
        {
            ActionStatus?.Invoke(this, new A2UIActionStatusEventArgs
            {
                ActionId = actionId,
                Ok = ok,
                Error = error,
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"[A2UI] ActionStatus listener threw: {ex.Message}");
        }
    }
}

/// <summary>
/// Logs the action and stores the last N for diagnostics. Used as a final
/// fallback when no real transport is available, so MCP-only nodes don't
/// silently drop interactions during development.
/// </summary>
public sealed class LoggingActionTransport : IA2UIActionTransport
{
    /// <summary>
    /// When true, log the full serialized envelope including action context.
    /// Default false: context can carry user-typed form values that the spec
    /// considers privacy-relevant (and the agent already sees over the wire).
    /// </summary>
    public bool LogFullEnvelope { get; set; }

    private readonly IOpenClawLogger _logger;
    public LoggingActionTransport(IOpenClawLogger logger) { _logger = logger; }
    public bool IsAvailable => true;
    public Task DeliverAsync(Protocol.A2UIAction action)
    {
        if (LogFullEnvelope)
        {
            _logger.Info($"[A2UI] action '{action.Name}' from {action.SourceComponentId ?? "?"} on surface '{action.SurfaceId}' (no remote sink): {A2UIActionEnvelope.Serialize(action)}");
        }
        else
        {
            // Default: identifiers only — drops the action context payload that
            // would otherwise carry form/PII data into the log file.
            _logger.Info($"[A2UI] action '{action.Name}' from {action.SourceComponentId ?? "?"} on surface '{action.SurfaceId}' (no remote sink)");
        }
        return Task.CompletedTask;
    }
}
