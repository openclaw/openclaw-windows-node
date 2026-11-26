using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Connection;

/// <summary>
/// Protects a reverse-proxy Bearer credential for the current Windows user and binds it
/// to one TLS WebSocket origin. It is never interchangeable with a Gateway token.
/// </summary>
public static class GatewayProxyBearerCredential
{
    private const string EntropyPrefix = "OpenClaw.Gateway.ProxyBearer.v1:";

    public static string RequireOrigin(string gatewayUrl)
    {
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.Origin,
                "A proxy Bearer token requires a wss:// gateway URL without URL credentials.");

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static bool MatchesOrigin(string gatewayUrl, string? expectedOrigin)
    {
        if (string.IsNullOrWhiteSpace(expectedOrigin)) return false;
        try
        {
            return string.Equals(RequireOrigin(gatewayUrl), expectedOrigin, StringComparison.OrdinalIgnoreCase);
        }
        catch (GatewayProxyCredentialException)
        {
            return false;
        }
    }

    public static string Protect(string token, string origin)
    {
        ValidateToken(token);
        if (!OperatingSystem.IsWindows())
            throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.SavedCredential,
                "Proxy Bearer credentials require Windows user protection.");

        var plain = Encoding.ASCII.GetBytes(token);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                plain, Encoding.UTF8.GetBytes(EntropyPrefix + origin), DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string? Reveal(GatewayRecord record)
    {
        if (string.IsNullOrEmpty(record.ProtectedProxyBearerToken)) return null;
        if (!MatchesOrigin(record.Url, record.ProxyBearerOrigin))
            throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.SavedCredential,
                "The saved proxy credential does not match this gateway origin.");
        if (record.ProtectedProxyBearerToken.Length > 32768)
            throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.SavedCredential,
                "The saved proxy credential is invalid.");
        if (!OperatingSystem.IsWindows())
            throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.SavedCredential,
                "Proxy Bearer credentials require Windows user protection.");

        byte[] protectedBytes;
        try { protectedBytes = Convert.FromBase64String(record.ProtectedProxyBearerToken); }
        catch (FormatException ex) { throw new GatewayProxyCredentialException(
            GatewayProxyCredentialFailure.SavedCredential, "The saved proxy credential is invalid.", ex); }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(
                protectedBytes,
                Encoding.UTF8.GetBytes(EntropyPrefix + record.ProxyBearerOrigin),
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.SavedCredential,
                "The saved proxy credential cannot be read by this Windows user.", ex);
        }

        try
        {
            var token = Encoding.ASCII.GetString(plain);
            ValidateToken(token);
            return token;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 8192 || token[0] == '=')
            throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.InvalidToken,
                "The proxy Bearer token is not a valid RFC 6750 token.");

        var padding = false;
        foreach (var c in token)
        {
            if (c == '=')
            {
                padding = true;
                continue;
            }
            if (padding || c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~' or '+' or '/'))
                throw new GatewayProxyCredentialException(GatewayProxyCredentialFailure.InvalidToken,
                    "The proxy Bearer token is not a valid RFC 6750 token.");
        }
    }
}
