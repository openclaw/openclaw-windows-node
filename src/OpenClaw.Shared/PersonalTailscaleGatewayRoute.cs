using System.Net;

namespace OpenClaw.Shared;

public static class PersonalTailscaleGatewayRoute
{
    public static bool TryNormalizeGatewayUrl(string? value, out string normalizedUrl, out string error)
    {
        normalizedUrl = string.Empty;
        error = "Personal Tailscale authentication requires a credential-free wss:// host.ts.net URL.";
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port < 1 ||
            IPAddress.TryParse(uri.Host, out _) ||
            uri.Host.EndsWith(".", StringComparison.Ordinal) ||
            !uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Length <= ".ts.net".Length)
        {
            return false;
        }

        var normalized = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeWss,
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };
        normalizedUrl = normalized.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

}
