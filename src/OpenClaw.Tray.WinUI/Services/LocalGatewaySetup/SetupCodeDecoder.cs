using System;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Decodes upstream OpenClaw setup codes into gateway URL and bootstrap token fields.
/// </summary>
public static class SetupCodeDecoder
{
    public record DecodeResult(bool Success, string? Url = null, string? Token = null, string? Error = null);

    public static DecodeResult Decode(string setupCode)
    {
        if (string.IsNullOrWhiteSpace(setupCode))
            return new DecodeResult(false, Error: "Setup code is empty");

        if (setupCode.Length > 2048)
            return new DecodeResult(false, Error: "Setup code exceeds 2048 character limit");

        string json;
        try
        {
            var b64 = setupCode.Trim().Replace('-', '+').Replace('_', '/');
            var pad = b64.Length % 4;
            if (pad > 0)
                b64 += new string('=', 4 - pad);

            json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (Exception ex)
        {
            return new DecodeResult(false, Error: $"Invalid base64: {ex.Message}");
        }

        if (json.Length > 4096)
            return new DecodeResult(false, Error: "Decoded JSON exceeds 4KB");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var url = TryReadString(root, "url");
            if (!string.IsNullOrEmpty(url) && !GatewayUrlHelper.IsValidGatewayUrl(url))
                return new DecodeResult(false, Error: "Invalid gateway URL in setup code");

            var token = TryReadString(root, "bootstrapToken")
                ?? TryReadString(root, "bootstrap_token")
                ?? TryReadString(root, "token");
            if (token?.Length > 512)
                token = null;

            return new DecodeResult(true, Url: string.IsNullOrEmpty(url) ? null : url, Token: token);
        }
        catch (JsonException ex)
        {
            return new DecodeResult(false, Error: $"Invalid JSON: {ex.Message}");
        }
    }

    private static string? TryReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
