using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    public const string HeartbeatName = "agent-heartbeat";

    private sealed record HeartbeatFixture(JsonElement Payload, string[] Scopes, bool Advertise, string? ErrorCode);

    /// <summary>Pinned system.ts/heartbeat-events.ts read contract. Never enables or runs a heartbeat.</summary>
    public static GatewayScenario CreateHeartbeat(
        JsonElement? payload = null, string[]? scopes = null, bool advertise = true, string? errorCode = null)
    {
        var browse = CreateBrowse();
        var heartbeat = payload?.Clone() ?? JsonSerializer.SerializeToElement(new
        {
            ts = 1791257718360L, status = "skipped", reason = "target-none",
            message = "Synthetic fixture heartbeat.", silent = true, durationMs = 42.5
        });
        return new(browse._sessions, browse._reads,
            heartbeat: new HeartbeatFixture(heartbeat, scopes ?? ["operator.read"], advertise, errorCode));
    }
}
