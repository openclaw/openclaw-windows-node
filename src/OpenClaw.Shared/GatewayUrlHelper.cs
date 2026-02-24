using System;

namespace OpenClaw.Shared;

public static class GatewayUrlHelper
{
    public const string ValidationMessage = "Gateway URL must be a valid URL (ws://, wss://, http://, or https://).";

    public static bool IsValidGatewayUrl(string? gatewayUrl) =>
        TryNormalizeWebSocketUrl(gatewayUrl, out _);

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

        var username = credentials.Substring(0, separatorIndex);
        var password = credentials.Substring(separatorIndex + 1);

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

            var remainder = trimmed.Substring(schemeSeparator);
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
        var authorityEnd = url.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        var atIndex = url.IndexOf('@', authorityStart);
        if (atIndex < 0 || atIndex >= authorityEnd)
        {
            return url;
        }

        return url.Substring(0, authorityStart) + url.Substring(atIndex + 1);
    }
}

