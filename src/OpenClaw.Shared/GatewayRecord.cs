using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// Minimal record for a paired gateway. Only auth-critical fields are persisted;
/// display metadata (name, version, uptime) comes from the gateway at connect time.
/// </summary>
public class GatewayRecord
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string? OperatorDeviceToken { get; set; }
    public string? NodeDeviceToken { get; set; }
    /// <summary>
    /// Transient bootstrap token for pairing. Cleared after pairing completes.
    /// </summary>
    public string? BootstrapToken { get; set; }
    /// <summary>
    /// The gateway's shared secret token (gateway.auth.token). Used for web dashboard auth.
    /// </summary>
    public string? SharedGatewayToken { get; set; }

    /// <summary>
    /// Generates a stable, human-readable ID from a gateway URL.
    /// Examples: ws://localhost:18789 → "localhost-18789",
    ///           wss://gw.example.com → "gw-example-com-443"
    /// </summary>
    public static string GenerateId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "unknown";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.Replace('.', '-').ToLowerInvariant();
            var port = uri.Port > 0
                ? uri.Port
                : (uri.Scheme is "wss" or "https" ? 443 : 80);
            return $"{host}-{port}";
        }

        // Fallback: sanitize raw string
        return new string(url.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray())
            .ToLowerInvariant();
    }

    internal GatewayRecord Clone() => new()
    {
        Id = Id,
        Url = Url,
        OperatorDeviceToken = OperatorDeviceToken,
        NodeDeviceToken = NodeDeviceToken,
        BootstrapToken = BootstrapToken,
        SharedGatewayToken = SharedGatewayToken,
    };
}

/// <summary>
/// Top-level JSON envelope for gateways.json.
/// </summary>
public class GatewayRegistryData
{
    public string? ActiveGatewayId { get; set; }
    public List<GatewayRecord> Gateways { get; set; } = new();

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    public static GatewayRegistryData? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GatewayRegistryData>(json, s_options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
