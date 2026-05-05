using System;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Decodes base64url-encoded setup codes into gateway URL and token.
/// Extracted from ConnectionPage for testability.
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
            // Base64url decode: replace URL-safe chars, add padding
            var b64 = setupCode.Trim().Replace('-', '+').Replace('_', '/');
            var pad = b64.Length % 4;
            if (pad > 0) b64 += new string('=', 4 - pad);

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
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new DecodeResult(false, Error: "Setup code JSON must be an object");

            string? url = null;
            string? token = null;

            if (doc.RootElement.TryGetProperty("url", out var urlProp))
            {
                if (urlProp.ValueKind != JsonValueKind.String)
                    return new DecodeResult(false, Error: "Invalid gateway URL in setup code");
                var decoded = urlProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(decoded))
                {
                    if (!GatewayUrlHelper.IsValidGatewayUrl(decoded))
                        return new DecodeResult(false, Error: "Invalid gateway URL in setup code");
                    url = decoded;
                }
            }

            if (doc.RootElement.TryGetProperty("bootstrapToken", out var tokenProp))
            {
                if (tokenProp.ValueKind != JsonValueKind.String)
                    return new DecodeResult(false, Error: "Invalid bootstrap token in setup code");
                var decoded = tokenProp.GetString() ?? "";
                if (decoded.Length > 512)
                    return new DecodeResult(false, Error: "Bootstrap token exceeds 512 character limit");
                if (!string.IsNullOrEmpty(decoded))
                    token = decoded;
            }

            if (url == null && token == null)
                return new DecodeResult(false, Error: "Setup code must include a gateway URL or bootstrap token");

            return new DecodeResult(true, Url: url, Token: token);
        }
        catch (JsonException ex)
        {
            return new DecodeResult(false, Error: $"Invalid JSON: {ex.Message}");
        }
    }
}
