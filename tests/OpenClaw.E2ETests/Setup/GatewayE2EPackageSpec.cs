namespace OpenClaw.E2ETests.Setup;

internal static class GatewayE2EPackageSpec
{
    internal const string EnvVar = "OPENCLAW_E2E_GATEWAY_PACKAGE_SPEC";

    internal static string Resolve() => Resolve(
        Environment.GetEnvironmentVariable(EnvVar),
        OpenClaw.SetupEngine.GatewayLkgVersion.ResolveLkgVersion());

    internal static string Resolve(string? raw, string defaultSpec)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultSpec;

        var value = raw.Trim();
        if (value.Contains('\r') || value.Contains('\n'))
            throw new InvalidOperationException($"{EnvVar} cannot contain newlines.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.AbsolutePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{EnvVar} must be an absolute HTTP(S) URL for a built .tgz package.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException($"{EnvVar} cannot contain credentials.");

        return value;
    }
}
