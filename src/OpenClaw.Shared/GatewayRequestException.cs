using System.Text.Json;

namespace OpenClaw.Shared;

public sealed class GatewayRequestException(
    string message,
    JsonElement? details = null)
    : InvalidOperationException(message)
{
    public JsonElement? Details { get; } = details;
}
