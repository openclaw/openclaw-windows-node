namespace OpenClaw.E2ETests.Setup;

internal static class GatewayE2EPackageSpec
{
    internal const string EnvVar = "OPENCLAW_E2E_GATEWAY_PACKAGE_SPEC";
    internal const string SourceEnvVar = "OPENCLAW_E2E_GATEWAY_SOURCE";

    internal static string Resolve() => Resolve(
        Environment.GetEnvironmentVariable(SourceEnvVar),
        Environment.GetEnvironmentVariable(EnvVar),
        OpenClaw.SetupEngine.GatewayLkgVersion.ResolveLkgVersion());

    internal static string Resolve(string? sourceRaw, string? packageRaw, string lkgSpec)
    {
        var source = sourceRaw?.Trim();
        if (string.Equals(source, "lkg", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(packageRaw))
                throw new InvalidOperationException($"{EnvVar} must be empty when {SourceEnvVar}=lkg.");
            return lkgSpec;
        }

        if (!string.Equals(source, "candidate", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SourceEnvVar} must be either 'lkg' or 'candidate'.");
        if (string.IsNullOrWhiteSpace(packageRaw))
            throw new InvalidOperationException($"{EnvVar} is required when {SourceEnvVar}=candidate.");

        var value = packageRaw.Trim();
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
