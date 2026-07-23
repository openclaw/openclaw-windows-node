namespace OpenClaw.E2ETests.Setup;

internal static class GatewayE2EPackageSpec
{
    internal const string EnvVar = "OPENCLAW_E2E_GATEWAY_PACKAGE_SPEC";
    internal const string Sha256EnvVar = "OPENCLAW_E2E_GATEWAY_PACKAGE_SHA256";
    internal const string HostDistroEnvVar = "OPENCLAW_E2E_GATEWAY_PACKAGE_HOST_DISTRO";
    internal const string SourceEnvVar = "OPENCLAW_E2E_GATEWAY_SOURCE";
    internal const string VersionEnvVar = "OPENCLAW_E2E_GATEWAY_VERSION";

    internal static string Resolve() => Resolve(
        Environment.GetEnvironmentVariable(SourceEnvVar),
        Environment.GetEnvironmentVariable(EnvVar),
        Environment.GetEnvironmentVariable(Sha256EnvVar),
        Environment.GetEnvironmentVariable(HostDistroEnvVar),
        Environment.GetEnvironmentVariable(VersionEnvVar),
        OpenClaw.SetupEngine.GatewayLkgVersion.ResolveLkgVersion());

    internal static string? ResolveExpectedSha256() => ResolveExpectedSha256(
        Environment.GetEnvironmentVariable(SourceEnvVar),
        Environment.GetEnvironmentVariable(Sha256EnvVar));

    internal static string? ResolveExpectedSha256(string? sourceRaw, string? sha256Raw)
    {
        var source = sourceRaw?.Trim();
        if (!string.Equals(source, "composed", StringComparison.OrdinalIgnoreCase))
            return null;

        var sha256 = sha256Raw?.Trim();
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"{Sha256EnvVar} must be the reviewed composed SHA-256 when {SourceEnvVar}=composed.");
        }

        return sha256.ToLowerInvariant();
    }

    internal static string Resolve(
        string? sourceRaw,
        string? packageRaw,
        string? sha256Raw,
        string? hostDistroRaw,
        string? versionRaw,
        string lkgSpec)
    {
        var source = sourceRaw?.Trim();
        if (string.Equals(source, "lkg", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(packageRaw))
                throw new InvalidOperationException($"{EnvVar} must be empty when {SourceEnvVar}=lkg.");
            if (!string.IsNullOrWhiteSpace(sha256Raw))
                throw new InvalidOperationException($"{Sha256EnvVar} must be empty when {SourceEnvVar}=lkg.");
            if (!string.IsNullOrWhiteSpace(hostDistroRaw))
                throw new InvalidOperationException($"{HostDistroEnvVar} must be empty when {SourceEnvVar}=lkg.");
            if (!string.IsNullOrWhiteSpace(versionRaw))
                throw new InvalidOperationException($"{VersionEnvVar} must be empty when {SourceEnvVar}=lkg.");
            return lkgSpec;
        }

        if (string.Equals(source, "official", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(packageRaw))
                throw new InvalidOperationException($"{EnvVar} must be empty when {SourceEnvVar}=official.");
            if (!string.IsNullOrWhiteSpace(sha256Raw))
                throw new InvalidOperationException($"{Sha256EnvVar} must be empty when {SourceEnvVar}=official.");
            if (!string.IsNullOrWhiteSpace(hostDistroRaw))
                throw new InvalidOperationException($"{HostDistroEnvVar} must be empty when {SourceEnvVar}=official.");

            var version = versionRaw?.Trim();
            if (string.IsNullOrWhiteSpace(version) ||
                !System.Text.RegularExpressions.Regex.IsMatch(
                    version,
                    "^\\d{4}\\.\\d+\\.\\d+-[0-9A-Za-z]+(?:[.-][0-9A-Za-z-]+)*$"))
            {
                throw new InvalidOperationException(
                    $"{VersionEnvVar} must be an exact prerelease version when {SourceEnvVar}=official.");
            }

            return version;
        }

        if (!string.Equals(source, "composed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{SourceEnvVar} must be 'lkg', 'official', or 'composed'.");
        if (!string.IsNullOrWhiteSpace(versionRaw))
            throw new InvalidOperationException($"{VersionEnvVar} must be empty when {SourceEnvVar}=composed.");
        if (string.IsNullOrWhiteSpace(packageRaw))
            throw new InvalidOperationException($"{EnvVar} is required when {SourceEnvVar}=composed.");
        if (string.IsNullOrWhiteSpace(sha256Raw) ||
            sha256Raw.Trim().Length != 64 ||
            !sha256Raw.Trim().All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"{Sha256EnvVar} must be the reviewed composed SHA-256 when {SourceEnvVar}=composed.");
        }
        if (string.IsNullOrWhiteSpace(hostDistroRaw) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                hostDistroRaw.Trim(),
                "^OpenClawE2EPackageHost-[A-Za-z0-9-]+$"))
        {
            throw new InvalidOperationException(
                $"{HostDistroEnvVar} must identify the disposable package host when {SourceEnvVar}=composed.");
        }

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

        var normalizedSha256 = sha256Raw.Trim().ToLowerInvariant();
        var expectedFileName = $"openclaw-composed-{normalizedSha256}.tgz";
        if (!string.Equals(
                Path.GetFileName(uri.AbsolutePath),
                expectedFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{EnvVar} must name the content-addressed package for {Sha256EnvVar}.");
        }

        return value;
    }
}
