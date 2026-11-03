using System.Net;

namespace OpenClaw.Connection;

public static class GatewayPersonalAuthenticationPolicy
{
    public const string AuthMethod = "tailscale";
    public const string CredentialSource = "auth.tailscale";

    public static bool TryNormalizeGatewayUrl(string? value, out string normalizedUrl, out string error) =>
        OpenClaw.Shared.PersonalTailscaleGatewayRoute.TryNormalizeGatewayUrl(value, out normalizedUrl, out error);

    public static bool TryGetCanonicalProfileId(System.Text.Json.JsonElement response, out string profileId)
    {
        profileId = string.Empty;
        if (response.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !response.TryGetProperty("profile", out var profile) ||
            profile.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !profile.TryGetProperty("id", out var id) ||
            id.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return false;
        }

        profileId = id.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(profileId) &&
            string.Equals(profileId, profileId.Trim(), StringComparison.Ordinal);
    }

    public static bool IsTrustedDashboardSource(string sourceUrl, string expectedGatewayUrl)
    {
        if (!TryNormalizeGatewayUrl(expectedGatewayUrl, out var route, out _) ||
            !Uri.TryCreate(route, UriKind.Absolute, out var expected) ||
            !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source))
        {
            return false;
        }

        return string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(source.UserInfo) &&
            source.Port == expected.Port &&
            string.Equals(source.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
            !IPAddress.TryParse(source.Host, out _);
    }
}

public sealed record PersonalOperatorLease(
    string GatewayId,
    string GatewayUrl,
    string IdentityProfileId,
    string RecoveryScope,
    string DeviceId,
    long Generation,
    long TransportEpoch);