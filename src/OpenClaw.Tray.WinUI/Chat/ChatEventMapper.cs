using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ChatApprovalIdentity(string RequestId, string? AlternateId);
internal sealed record ChatEventMapping(ChatEvent? Event, ChatApprovalIdentity? Approval = null);
internal sealed record ChatTerminalApprovalMapping(
    string Phase,
    string ApprovalId,
    string ApprovalSlug,
    string Decision);

internal sealed record ChatFlattenedToolEvents(
    ChatToolStartEvent Start,
    ChatToolOutputEvent Output);

internal static class ChatEventMapper
{
    internal static ChatEventMapping Map(AgentEventInfo evt)
    {
        var stream = evt.Stream?.ToLowerInvariant();
        if (string.IsNullOrEmpty(stream))
            return new(null);

        return stream switch
        {
            "assistant" => new(MapAssistant(evt)),
            "reasoning" => new(MapReasoning(evt)),
            "lifecycle" => new(MapLifecycle(evt)),
            "tool" => new(MapTool(evt)),
            "item" => new(MapItem(evt)),
            "command_output" => new(MapCommandOutput(evt)),
            "patch" => new(MapPatch(evt)),
            "job" => new(MapJob(evt)),
            "approval" => MapApproval(evt),
            _ => new(null),
        };
    }

    internal static bool IsLifecycleStart(AgentEventInfo evt) =>
        string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
        evt.Data.ValueKind == JsonValueKind.Object &&
        evt.Data.TryGetProperty("phase", out var phase) &&
        string.Equals(phase.GetString(), "start", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTerminalRunEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return false;
        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("phase", out var phase))
        {
            var value = phase.GetString();
            return string.Equals(value, "end", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("state", out var state))
        {
            var value = state.GetString();
            return string.Equals(value, "done", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    internal static ChatResponseOutputKind? ClassifyInboundOutput(
        AgentEventInfo evt,
        ChatEvent mapped)
    {
        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return mapped switch
        {
            ChatMessageEvent or ChatMessageDeltaEvent => ChatResponseOutputKind.Assistant,
            ChatThinkingEvent or ChatReasoningEvent or ChatReasoningDeltaEvent or
                ChatIntentEvent => ChatResponseOutputKind.Reasoning,
            ChatToolStartEvent or ChatToolPresentationEvent or
                ChatToolOutputEvent or ChatToolErrorEvent or
                ChatPermissionRequestEvent => ChatResponseOutputKind.Tool,
            ChatStatusEvent or ChatErrorEvent or ChatReasoningEndEvent or
                ChatTurnEndEvent or ChatUserMessageEvent => null,
            _ => null,
        };
    }

    internal static bool IsTerminalApprovalPhase(string phase)
    {
        if (string.IsNullOrEmpty(phase))
            return false;

        return string.Equals(phase, "resolved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "denied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "aborted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "expired", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "timeout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "error", StringComparison.OrdinalIgnoreCase);
    }

    internal static ChatTerminalApprovalMapping? MapTerminalApproval(
        AgentEventInfo evt)
    {
        if (!string.Equals(evt.Stream, "approval", StringComparison.OrdinalIgnoreCase) ||
            evt.Data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var phase = StringProperty(evt.Data, "phase");
        return IsTerminalApprovalPhase(phase)
            ? new ChatTerminalApprovalMapping(
                phase,
                StringProperty(evt.Data, "approvalId"),
                StringProperty(evt.Data, "approvalSlug"),
                StringProperty(evt.Data, "decision"))
            : null;
    }

    internal static ChatPermissionDecision MapTerminalApprovalDecision(
        string phase,
        string? decision = null)
    {
        if (string.Equals(phase, "resolved", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(
                    decision,
                    ChatPermissionActionKeys.AllowAlways,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ChatPermissionDecision.AllowedAlways;
            }
            if (string.Equals(
                    decision,
                    ChatPermissionActionKeys.Deny,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ChatPermissionDecision.Denied;
            }
            return ChatPermissionDecision.Allowed;
        }

        return string.Equals(phase, "denied", StringComparison.OrdinalIgnoreCase)
            ? ChatPermissionDecision.Denied
            : ChatPermissionDecision.Expired;
    }

    internal static ChatFlattenedToolEvents MapFlattenedToolOutput(
        string text,
        string? runId)
    {
        var kind = NativeToolProjector.ClassifyFlattenedToolOutput(text);
        var label = NativeToolProjector.ExtractFlattenedToolSummary(text);
        return new(
            new ChatToolStartEvent(
                label,
                kind,
                IdentityStrength:
                    NativeToolProjector.ClassifyHistoryIdentityStrength(kind),
                RunId: runId),
            new ChatToolOutputEvent(text, RunId: runId));
    }

    private static ChatEvent? MapAssistant(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return null;
        if (evt.Data.TryGetProperty("delta", out var deltaProperty) &&
            deltaProperty.ValueKind == JsonValueKind.String &&
            deltaProperty.GetString() is { Length: > 0 } delta)
        {
            return new ChatMessageDeltaEvent(delta);
        }
        return null;
    }

    private static ChatEvent? MapReasoning(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return null;
        if (evt.Data.TryGetProperty("delta", out var deltaProperty) &&
            deltaProperty.ValueKind == JsonValueKind.String &&
            deltaProperty.GetString() is { Length: > 0 } delta)
        {
            return new ChatReasoningDeltaEvent(delta);
        }

        var content = evt.Data.TryGetProperty("content", out var contentProperty) &&
                      contentProperty.ValueKind == JsonValueKind.String
            ? contentProperty.GetString()
            : evt.Data.TryGetProperty("text", out var textProperty) &&
              textProperty.ValueKind == JsonValueKind.String
                ? textProperty.GetString()
                : null;
        return string.IsNullOrEmpty(content) ? null : new ChatReasoningEvent(content);
    }

    private static ChatEvent? MapLifecycle(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object ||
            !evt.Data.TryGetProperty("phase", out var phaseProperty))
        {
            return null;
        }

        return phaseProperty.GetString()?.ToLowerInvariant() switch
        {
            "start" => new ChatThinkingEvent(""),
            "end" => new ChatTurnEndEvent(),
            "error" => new ChatErrorEvent(
                evt.Summary ??
                (evt.Data.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "Agent error"
                    : "Agent error")),
            _ => null,
        };
    }

    private static ChatEvent? MapTool(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return null;

        var phase = NativeToolProjector.GetStringProperty(evt.Data, "phase");
        var identity = NativeToolProjector.ExtractToolIdentity(evt.Data);
        var toolArgs = NativeToolProjector.ExtractSafeToolDisplayArgs(evt.Data);
        var label = NativeToolProjector.ExtractToolLabel(evt.Data, toolArgs);
        var toolCallId = NativeToolProjector.ExtractToolCorrelationId(evt.Data);

        return phase.ToLowerInvariant() switch
        {
            "start" => new ChatToolStartEvent(
                label,
                identity.Name,
                ToolArgs: toolArgs,
                ToolCallId: toolCallId,
                IdentityStrength: identity.Strength,
                RunId: evt.RunId),
            "result" when NativeToolProjector.IsToolResultError(evt.Data) =>
                new ChatToolErrorEvent(
                    NativeToolProjector.ExtractToolResultErrorText(evt.Data),
                    ToolCallId: toolCallId,
                    RunId: evt.RunId,
                    ErrorTextQuality: NativeToolProjector.HasSafeToolErrorSummary(evt.Data)
                        ? ChatToolErrorTextQuality.SafeSummary
                        : ChatToolErrorTextQuality.Unspecified),
            "result" => new ChatToolOutputEvent(
                NativeToolProjector.ExtractToolResultText(evt.Data, string.Empty),
                ToolCallId: toolCallId,
                RunId: evt.RunId),
            "error" => new ChatToolErrorEvent(
                NativeToolProjector.ExtractToolErrorText(evt.Data, label),
                ToolCallId: toolCallId,
                RunId: evt.RunId),
            _ => null,
        };
    }

    private static ChatEvent? MapItem(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return null;

        var kind = NativeToolProjector.GetStringProperty(evt.Data, "kind");
        var phase = NativeToolProjector.GetStringProperty(evt.Data, "phase");
        if (string.Equals(kind, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(phase, "end", StringComparison.OrdinalIgnoreCase)
                ? new ChatReasoningEndEvent()
                : null;
        }

        if (string.Equals(kind, "command", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "patch", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedPhase = phase.ToLowerInvariant();
            if (normalizedPhase is not ("start" or "update" or "end"))
                return null;

            var toolCallId = NativeToolProjector.ExtractToolCorrelationId(evt.Data);
            var childStatus = NativeToolProjector.GetStringProperty(evt.Data, "status");
            var childIdentity = NativeToolProjector.ExtractToolIdentity(evt.Data);
            var commandArgs = NativeToolProjector.ExtractSafeToolDisplayArgs(evt.Data);
            if (normalizedPhase == "end")
            {
                if (IsErrorLikeToolStatus(childStatus))
                {
                    return new ChatToolErrorEvent(
                        NativeToolProjector.ExtractToolErrorText(
                            evt.Data,
                            ToolStatusFallback(childStatus)),
                        ToolCallId: toolCallId,
                        RunId: evt.RunId);
                }

                if (IsCompletedToolStatus(childStatus))
                {
                    return new ChatToolOutputEvent(
                        string.Empty,
                        ToolCallId: toolCallId,
                        RunId: evt.RunId);
                }

                return string.IsNullOrWhiteSpace(toolCallId)
                    ? null
                    : new ChatToolPresentationEvent(
                        toolCallId,
                        childIdentity.Name,
                        childIdentity.Strength,
                        commandArgs,
                        ActivatesTurn: false,
                        RunId: evt.RunId);
            }

            if (string.IsNullOrWhiteSpace(toolCallId))
                return null;

            return new ChatToolPresentationEvent(
                toolCallId,
                childIdentity.Name,
                childIdentity.Strength,
                commandArgs,
                ActivatesTurn: normalizedPhase == "start",
                RunId: evt.RunId);
        }

        if (!string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
            return null;

        var title = NativeToolProjector.GetStringProperty(evt.Data, "title");
        var identity = NativeToolProjector.ExtractToolIdentity(evt.Data);
        var toolArgs = NativeToolProjector.ExtractSafeToolDisplayArgs(evt.Data);
        var label = NativeToolProjector.FirstToolDisplayValue(toolArgs);
        if (string.IsNullOrWhiteSpace(label))
            label = NativeToolProjector.SanitizeToolDisplayValue(title);
        var itemId = NativeToolProjector.ExtractToolCorrelationId(evt.Data);
        var status = NativeToolProjector.GetStringProperty(evt.Data, "status");

        return phase.ToLowerInvariant() switch
        {
            "start" => new ChatToolStartEvent(
                label,
                identity.Name,
                ToolArgs: toolArgs,
                ToolCallId: itemId,
                IdentityStrength: identity.Strength,
                RunId: evt.RunId),
            "end" when IsErrorLikeToolStatus(status) => new ChatToolErrorEvent(
                NativeToolProjector.ExtractToolErrorText(
                    evt.Data,
                    ToolStatusFallback(status)),
                ToolCallId: itemId,
                RunId: evt.RunId),
            "end" => new ChatToolOutputEvent(
                string.Empty,
                ToolCallId: itemId,
                RunId: evt.RunId),
            "error" => new ChatToolErrorEvent(
                NativeToolProjector.SanitizeToolDisplayValue(title),
                ToolCallId: itemId,
                RunId: evt.RunId),
            _ => null,
        };
    }

    private static ChatEvent? MapCommandOutput(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object ||
            !string.Equals(
                StringProperty(evt.Data, "phase"),
                "end",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var output = NativeToolProjector.ExtractCommandOutputText(evt.Data);
        var itemId = NativeToolProjector.ExtractToolCorrelationId(evt.Data);
        var status = NativeToolProjector.GetStringProperty(evt.Data, "status");
        if (IsErrorLikeToolStatus(status))
        {
            var fallback = string.IsNullOrEmpty(output)
                ? ToolStatusFallback(status)
                : output;
            return new ChatToolErrorEvent(
                NativeToolProjector.ExtractToolErrorText(evt.Data, fallback),
                ToolCallId: itemId,
                RunId: evt.RunId);
        }

        if (string.IsNullOrEmpty(output) && !IsCompletedToolStatus(status))
            return null;

        return new ChatToolOutputEvent(
            output,
            ToolCallId: itemId,
            RunId: evt.RunId);
    }

    private static ChatEvent? MapPatch(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object ||
            !string.Equals(
                NativeToolProjector.GetStringProperty(evt.Data, "phase"),
                "end",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ChatToolOutputEvent(
            NativeToolProjector.ExtractPatchSummaryText(evt.Data),
            ToolCallId: NativeToolProjector.ExtractToolCorrelationId(evt.Data),
            RunId: evt.RunId);
    }

    private static bool IsErrorLikeToolStatus(string status) =>
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletedToolStatus(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static string ToolStatusFallback(string status) =>
        string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase)
            ? "Tool blocked"
            : "Tool failed";

    private static ChatEvent? MapJob(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return null;
        return StringProperty(evt.Data, "state").ToLowerInvariant() switch
        {
            "done" => new ChatTurnEndEvent(),
            "error" => new ChatErrorEvent(evt.Summary ?? "Agent error"),
            _ => null,
        };
    }

    private static ChatEventMapping MapApproval(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object ||
            !string.Equals(
                StringProperty(evt.Data, "phase"),
                "requested",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(null);
        }

        var approvalId = StringProperty(evt.Data, "approvalId");
        var slug = StringProperty(evt.Data, "approvalSlug");
        var requestId = !string.IsNullOrEmpty(slug) ? slug : approvalId;
        if (string.IsNullOrEmpty(requestId))
            return new(null);

        var host = StringProperty(evt.Data, "host");
        var command = StringProperty(evt.Data, "command");
        var title = StringProperty(evt.Data, "title");
        var message = StringProperty(evt.Data, "message");
        var detail = string.IsNullOrEmpty(message)
            ? command
            : string.IsNullOrEmpty(command) ? message : message + "\n\n" + command;
        var mapped = new ChatPermissionRequestEvent(
            requestId,
            !string.IsNullOrEmpty(title) ? title : "Exec approval",
            !string.IsNullOrEmpty(host) ? host : "node",
            detail,
            ChatPermissionActionKeys.ExecApprovalDefaults);
        var alternateId = !string.IsNullOrEmpty(slug) ? approvalId : slug;
        return new(mapped, new ChatApprovalIdentity(requestId, alternateId));
    }

    private static string StringProperty(JsonElement data, string name) =>
        data.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
