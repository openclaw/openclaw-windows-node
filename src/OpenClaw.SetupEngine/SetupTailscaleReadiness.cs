using System.Text.Json;

namespace OpenClaw.SetupEngine;

public static class SetupTailscaleReadiness
{
    /// <summary>A DNS name alone can survive a disabled MagicDNS setting. Require the current tailnet fact.</summary>
    public static bool IsMagicDnsEnabled(string statusJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("CurrentTailnet", out var tailnet) &&
                tailnet.ValueKind == JsonValueKind.Object &&
                tailnet.TryGetProperty("MagicDNSEnabled", out var enabled) &&
                enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
