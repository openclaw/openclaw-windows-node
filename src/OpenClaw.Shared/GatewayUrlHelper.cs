using System;
using System.Buffers;
using System.Net;

namespace OpenClaw.Shared;

public static class GatewayUrlHelper
{
    public const string InvalidFormatMessage = "Gateway URL must be a valid URL (ws://, wss://, http://, or https://).";
    public const string ValidationMessage = InvalidFormatMessage;
    public const string AllowInsecureGatewayEnvironmentVariable = "OPENCLAW_ALLOW_INSECURE_GATEWAY";
    public const string InsecureGatewayWarningMessage = "This gateway uses plain ws:// without TLS. Only continue on trusted local networks; LAN attackers may intercept or inject chat content.";
    public const string InsecureGatewayBlockedMessage = "Plain ws:// gateways outside loopback/private networks require TLS (wss://). Set OPENCLAW_ALLOW_INSECURE_GATEWAY=1 to allow this insecure connection.";

    private static readonly SearchValues<char> s_authorityTerminators =
        SearchValues.Create("/?#");

    public static bool IsValidGatewayUrl(string? gatewayUrl) =>
        TryValidateGatewayUrl(gatewayUrl, out _, out _);

    public static bool TryValidateGatewayUrl(string? gatewayUrl, out string normalizedUrl, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!TryNormalizeWebSocketUrl(gatewayUrl, out normalizedUrl))
        {
            errorMessage = InvalidFormatMessage;
            return false;
        }

        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
        {
            normalizedUrl = string.Empty;
            errorMessage = InvalidFormatMessage;
            return false;
        }

        if (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) &&
            !IsLoopbackGateway(uri) &&
            !IsPrivateGateway(uri) &&
            !AllowInsecureGatewayOverrideEnabled())
        {
            errorMessage = InsecureGatewayBlockedMessage;
            return false;
        }

        return true;
    }

    public static string GetGatewayUrlValidationMessage(string? gatewayUrl) =>
        TryValidateGatewayUrl(gatewayUrl, out _, out var errorMessage)
            ? string.Empty
            : errorMessage;

    public static bool TryGetInsecureGatewayWarning(string? gatewayUrl, out string warningMessage)
    {
        warningMessage = string.Empty;
        if (!TryNormalizeWebSocketUrl(gatewayUrl, out var normalizedUrl) ||
            !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            IsLoopbackGateway(uri))
        {
            return false;
        }

        if (IsPrivateGateway(uri) || AllowInsecureGatewayOverrideEnabled())
        {
            warningMessage = InsecureGatewayWarningMessage;
            return true;
        }

        return false;
    }

    public static string NormalizeForWebSocket(string? gatewayUrl) =>
        TryNormalizeWebSocketUrl(gatewayUrl, out var normalizedUrl)
            ? normalizedUrl
            : gatewayUrl?.Trim() ?? string.Empty;

    /// <summary>
    /// Extract credentials from gateway URL user-info (username:password).
    /// The returned value may include URL-encoded characters and should be decoded before
    /// constructing an Authorization header.
    /// </summary>
    public static string? ExtractCredentials(string gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return string.IsNullOrEmpty(uri.UserInfo) ? null : uri.UserInfo;
    }

    /// <summary>
    /// Decode URL-encoded credentials from URL user-info format (username:password).
    /// Username-only input is normalized to username: for HTTP Basic Auth.
    /// Returns the original value if decoding fails.
    /// </summary>
    public static string DecodeCredentials(string credentials)
    {
        if (string.IsNullOrEmpty(credentials))
        {
            return credentials;
        }

        var separatorIndex = credentials.IndexOf(':');
        if (separatorIndex < 0)
        {
            try
            {
                return $"{Uri.UnescapeDataString(credentials)}:";
            }
            catch (UriFormatException)
            {
                return $"{credentials}:";
            }
        }

        var username = credentials[..separatorIndex];
        var password = credentials[(separatorIndex + 1)..];

        try
        {
            return $"{Uri.UnescapeDataString(username)}:{Uri.UnescapeDataString(password)}";
        }
        catch (UriFormatException)
        {
            return credentials;
        }
    }

    /// <summary>
    /// Remove user-info credentials from a URL for safe logging and display.
    /// </summary>
    public static string SanitizeForDisplay(string? gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return gatewayUrl?.Trim() ?? string.Empty;
        }

        return RemoveUserInfo(gatewayUrl.Trim());
    }

    public static bool TryNormalizeWebSocketUrl(string? gatewayUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return false;
        }

        var trimmed = gatewayUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        string candidate;
        if (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed;
        }
        else
        {
            var schemeSeparator = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeSeparator < 0)
            {
                return false;
            }

            var remainder = trimmed[schemeSeparator..];
            if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "ws" + remainder;
            }
            else if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "wss" + remainder;
            }
            else
            {
                return false;
            }
        }

        normalizedUrl = RemoveUserInfo(candidate);
        return true;
    }

    private static string RemoveUserInfo(string url)
    {
        var schemeSeparator = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return url;
        }

        var authorityStart = schemeSeparator + 3;
        var relativeEnd = url.AsSpan(authorityStart).IndexOfAny(s_authorityTerminators);
        var authorityEnd = relativeEnd < 0 ? url.Length : authorityStart + relativeEnd;

        var atIndex = url.IndexOf('@', authorityStart);
        if (atIndex < 0 || atIndex >= authorityEnd)
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, authorityStart), url.AsSpan(atIndex + 1));
    }

    private static bool AllowInsecureGatewayOverrideEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(AllowInsecureGatewayEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    private static bool IsLoopbackGateway(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateGateway(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 || // 10.0.0.0/8
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || // 172.16.0.0/12
                (bytes[0] == 192 && bytes[1] == 168); // 192.168.0.0/16
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xfe) == 0xfc;
    }
}
