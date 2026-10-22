using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Preserves structured Gateway rejection data. Details are never diagnostic log content.</summary>
public sealed class GatewayRequestException : InvalidOperationException
{
    public string? Code { get; }
    public JsonElement? Details { get; }

    public GatewayRequestException(string message, string? code = null, JsonElement? details = null)
        : base(message)
    {
        Code = code;
        Details = details?.Clone();
    }

    public string? DetailType => Details is { ValueKind: JsonValueKind.Object } details &&
        details.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() : null;

    internal static GatewayRequestException FromResponse(JsonElement response, string message)
    {
        if (!response.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return new(message);
        return new(message,
            error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString() : null,
            error.TryGetProperty("details", out var details) ? details : null);
    }
}
