using System.Text;
using System.Text.Json;
using OpenClaw.Connection;

namespace OpenClawTray.Helpers;

/// <summary>A nonce challenge received from the trusted personal Gateway Dashboard.</summary>
/// <param name="Id">The request identifier to correlate with the Dashboard reply.</param>
/// <param name="Nonce">The Gateway-issued nonce included in the signed response.</param>
/// <param name="SignedAt">The Gateway-issued Unix timestamp, in milliseconds.</param>
public sealed record PersonalGatewayAuthChallenge(string Id, string Nonce, long SignedAt);

/// <summary>Validates and parses Dashboard challenges for the current personal Gateway route.</summary>
public static class PersonalGatewayAuthChallengeParser
{
    /// <summary>Parses a challenge only when its source belongs to the expected Gateway Dashboard.</summary>
    /// <param name="messageJson">The serialized message received from the Dashboard.</param>
    /// <param name="sourceUrl">The sending document URL reported by the native host.</param>
    /// <param name="expectedGatewayUrl">The Gateway route whose identity is being verified.</param>
    /// <param name="challenge">The parsed challenge, or <see langword="null"/> when rejected.</param>
    /// <returns><see langword="true"/> only for a well-formed challenge from the expected trusted route.</returns>
    public static bool TryParse(
        string messageJson,
        string sourceUrl,
        string expectedGatewayUrl,
        out PersonalGatewayAuthChallenge? challenge)
    {
        challenge = null;
        if (!GatewayPersonalAuthenticationPolicy.IsTrustedDashboardSource(sourceUrl, expectedGatewayUrl))
            return false;

        try
        {
            using var outer = JsonDocument.Parse(messageJson);
            var body = outer.RootElement;
            if (body.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(body.GetString() ?? string.Empty);
                return TryReadChallenge(inner.RootElement, out challenge);
            }

            return TryReadChallenge(body, out challenge);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadChallenge(JsonElement body, out PersonalGatewayAuthChallenge? challenge)
    {
        challenge = null;
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != 3 ||
            !body.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !body.TryGetProperty("nonce", out var nonce) || nonce.ValueKind != JsonValueKind.String ||
            !body.TryGetProperty("signedAt", out var signedAt) ||
            signedAt.ValueKind != JsonValueKind.Number ||
            !signedAt.TryGetInt64(out var signedAtMs))
        {
            return false;
        }

        var idValue = id.GetString() ?? string.Empty;
        var nonceValue = nonce.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(idValue) || Encoding.UTF8.GetByteCount(idValue) > 128 ||
            string.IsNullOrWhiteSpace(nonceValue) || Encoding.UTF8.GetByteCount(nonceValue) > 512 ||
            nonceValue.Contains('|') ||
            nonceValue.Any(character => char.IsControl(character) || character == '\uFEFF') ||
            !nonceValue.Any(character => !char.IsWhiteSpace(character)) ||
            signedAtMs <= 0 || signedAtMs > 9_007_199_254_740_991)
        {
            return false;
        }

        challenge = new PersonalGatewayAuthChallenge(idValue, nonceValue, signedAtMs);
        return true;
    }
}
