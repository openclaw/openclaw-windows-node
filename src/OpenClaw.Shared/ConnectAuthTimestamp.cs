using System.Text.Json;

namespace OpenClaw.Shared;

internal static class ConnectAuthTimestamp
{
    public static long? ReadChallengeTimestamp(JsonElement payload)
    {
        if (!payload.TryGetProperty("ts", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.Number ||
            !timestamp.TryGetInt64(out var timestampMs) ||
            timestampMs <= 0)
        {
            return null;
        }

        return timestampMs;
    }

    public static long ResolveSignedAt(long? challengeTimestampMs, TimeProvider? timeProvider = null)
    {
        if (challengeTimestampMs is > 0)
        {
            return challengeTimestampMs.Value;
        }

        return (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
    }
}
