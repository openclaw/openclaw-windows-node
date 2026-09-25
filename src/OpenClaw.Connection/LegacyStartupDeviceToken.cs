namespace OpenClaw.Connection;

/// <summary>
/// Startup credential choice after a missing per-gateway identity is checked
/// against the legacy settings identity for the same gateway URL.
/// </summary>
internal readonly record struct LegacyStartupCredentialChoice(
    GatewayCredentialResolution Resolution,
    string IdentityDirectory,
    bool Copied,
    string? CopyError);

/// <summary>
/// Keeps a stored device token ahead of a shared or bootstrap token when the
/// per-gateway identity file was never copied from the legacy settings path.
/// </summary>
internal static class LegacyStartupDeviceToken
{
    public const string IdentityFileName = "device-key-ed25519.json";

    public static bool IsStoredDeviceCredential(GatewayCredential? credential) =>
        credential?.Source is CredentialResolver.SourceDeviceToken
            or CredentialResolver.SourceNodeDeviceToken;

    public static LegacyStartupCredentialChoice Prefer(
        GatewayCredentialResolution primary,
        string recordUrl,
        string? effectiveGatewayUrl,
        string perGatewayIdentityDirectory,
        string legacySettingsDirectory,
        Func<string, GatewayCredentialResolution> resolveFromDirectory)
    {
        ArgumentNullException.ThrowIfNull(resolveFromDirectory);

        if (IsStoredDeviceCredential(primary.Credential)
            || !UrlsMatch(recordUrl, effectiveGatewayUrl)
            || IdentityFileExists(perGatewayIdentityDirectory)
            || !IdentityFileExists(legacySettingsDirectory))
        {
            return new(primary, perGatewayIdentityDirectory, Copied: false, CopyError: null);
        }

        var copyError = TryCopyLegacyIdentity(perGatewayIdentityDirectory, legacySettingsDirectory);
        if (copyError == null)
        {
            return new(
                resolveFromDirectory(perGatewayIdentityDirectory),
                perGatewayIdentityDirectory,
                Copied: true,
                CopyError: null);
        }

        var legacy = resolveFromDirectory(legacySettingsDirectory);
        if (IsStoredDeviceCredential(legacy.Credential) || primary.Credential is null)
            return new(legacy, legacySettingsDirectory, Copied: false, CopyError: copyError);

        return new(primary, perGatewayIdentityDirectory, Copied: false, CopyError: copyError);
    }

    private static bool UrlsMatch(string recordUrl, string? effectiveGatewayUrl) =>
        !string.IsNullOrWhiteSpace(effectiveGatewayUrl)
        && string.Equals(recordUrl, effectiveGatewayUrl, StringComparison.OrdinalIgnoreCase);

    private static bool IdentityFileExists(string directory) =>
        File.Exists(Path.Combine(directory, IdentityFileName));

    private static string? TryCopyLegacyIdentity(
        string perGatewayIdentityDirectory,
        string legacySettingsDirectory)
    {
        try
        {
            var destination = Path.Combine(perGatewayIdentityDirectory, IdentityFileName);
            if (File.Exists(destination))
                return null;

            if (!Directory.Exists(perGatewayIdentityDirectory))
                Directory.CreateDirectory(perGatewayIdentityDirectory);

            File.Copy(
                Path.Combine(legacySettingsDirectory, IdentityFileName),
                destination,
                overwrite: false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
