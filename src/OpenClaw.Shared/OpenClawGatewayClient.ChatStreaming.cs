using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private readonly GatewayChatStreamProjection _chatStreamProjection = new();

    private void HandleChatEventForConnection(JsonElement root, int rawMessageLength, long generation)
    {
        JsonElement? projected;
        bool missingBaseline;
        lock (_chatStreamProjection)
            projected = _chatStreamProjection.Project(root, generation, out missingBaseline);
        if (missingBaseline)
        {
            _logger.Warn("Chat stream baseline unavailable. Reload conversation history to recover.");
            var payload = root.GetProperty("payload");
            AgentEventReceived?.Invoke(this, new AgentEventInfo
            {
                Stream = "notice",
                SessionKey = payload.GetProperty("sessionKey").GetString() ?? "",
                RunId = payload.GetProperty("runId").GetString() ?? "",
                Data = JsonSerializer.SerializeToElement(new
                {
                    reason = "chat_stream_incomplete",
                    message = "Live response is incomplete. Reconnect or reload conversation history to recover.",
                }),
            });
            // Resume through the existing bounded reconnect loop and history recovery, not a second client.
            AbortCurrentWebSocket(generation);
        }
        if (projected is { } value)
            HandleChatEvent(value, rawMessageLength);
    }
}
