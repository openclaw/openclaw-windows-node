using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Defines the Windows clients' supported Gateway wire protocol and validates
/// the minimal successful handshake contract.
/// </summary>
public static class GatewayProtocolContract
{
    public const int SupportedVersion = 4;
    public const int CurrentVersion = SupportedVersion;
    public const int MinimumSupportedVersion = 3;
    public const int MaximumSupportedVersion = CurrentVersion;
    public const string HelloOkType = "hello-ok";

    public static bool IsHelloOk(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), HelloOkType, StringComparison.Ordinal);

    public static bool TryValidateHelloOk(JsonElement payload, out string error)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "hello-ok payload must be an object";
            return false;
        }

        if (!IsHelloOk(payload))
        {
            error = "connect success payload must have type hello-ok";
            return false;
        }

        if (!payload.TryGetProperty("protocol", out var protocol) ||
            protocol.ValueKind != JsonValueKind.Number ||
            !protocol.TryGetInt32(out var version))
        {
            error = "hello-ok protocol must be an integer";
            return false;
        }

        // hello-ok.protocol is the Gateway's current protocol constant, not a
        // negotiated selection. Once the Gateway accepts our advertised range,
        // future protocol values remain valid unless they fall below our floor.
        if (version < MinimumSupportedVersion)
        {
            error = $"hello-ok protocol {version} is unsupported";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryGetProtocol(JsonElement payload, out int protocol)
    {
        protocol = default;
        return payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("protocol", out var protocolValue) &&
            protocolValue.ValueKind == JsonValueKind.Number &&
            protocolValue.TryGetInt32(out protocol);
    }

    public static GatewayProtocolCompatibility ParseMismatch(JsonElement response)
    {
        if (!TryGetErrorDetails(response, out var details))
            return GatewayProtocolCompatibility.FromGatewayExpectation(expectedProtocol: null);

        return GatewayProtocolCompatibility.FromGatewayExpectation(
            TryGetInteger(details, "expectedProtocol"),
            TryGetInteger(details, "minimumProbeProtocol"));
    }

    private static bool TryGetErrorDetails(JsonElement response, out JsonElement details)
    {
        details = default;
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (error.TryGetProperty("details", out details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        return error.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("details", out details) &&
            details.ValueKind == JsonValueKind.Object;
    }

    private static int? TryGetInteger(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var integer)
            ? integer
            : null;
}
