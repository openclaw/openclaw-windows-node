namespace OpenClawTray.Helpers;

internal static class ConfigPathSensitivity
{
    public static bool IsSensitive(string path)
    {
        var normalizedPath = path.ToLowerInvariant();
        if (normalizedPath.Contains("token", StringComparison.Ordinal)
            || normalizedPath.Contains("secret", StringComparison.Ordinal)
            || normalizedPath.Contains("password", StringComparison.Ordinal)
            || normalizedPath.Contains("apikey", StringComparison.Ordinal)
            || normalizedPath.Contains("api_key", StringComparison.Ordinal)
            || normalizedPath.Contains("webhookurl", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var segment in path.Split('.'))
        {
            if (segment.Equals("nsec", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("privateKey", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
