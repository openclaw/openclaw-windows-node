using System.Numerics;
using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public enum GatewayPackageSource
{
    Official,
    Composed
}

public enum GatewayPackageUpdateRoute
{
    CompanionInstaller,
    CoreTransaction
}

public enum GatewayUpdateDispatchState
{
    Prepared,
    Ambiguous,
    Accepted,
    Cancelled
}

public enum GatewayUpdateCompletionState
{
    Prepared,
    Ambiguous,
    Accepted
}

/// <summary>
/// Immutable identity for the exact Gateway package the Companion is allowed to install.
/// </summary>
public sealed partial record GatewayPackageTarget
{
    private GatewayPackageTarget(
        GatewayPackageSource source,
        string expectedVersion,
        Uri? packageUri,
        string? sha256)
    {
        Source = source;
        ExpectedVersion = expectedVersion;
        PackageUri = packageUri;
        Sha256 = sha256;
    }

    public GatewayPackageSource Source { get; }
    public string ExpectedVersion { get; }
    public Uri? PackageUri { get; }
    public string? Sha256 { get; }

    public static GatewayPackageTarget Official(string expectedVersion)
    {
        return new(
            GatewayPackageSource.Official,
            NormalizeExactVersion(expectedVersion),
            packageUri: null,
            sha256: null);
    }

    public static GatewayPackageTarget Composed(
        string expectedVersion,
        Uri packageUri,
        string sha256)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        if (!packageUri.IsAbsoluteUri ||
            !packageUri.IsWellFormedOriginalString() ||
            (packageUri.Scheme != Uri.UriSchemeHttp && packageUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(packageUri.Host) ||
            !packageUri.AbsolutePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(packageUri.UserInfo) ||
            !string.IsNullOrEmpty(packageUri.Query) ||
            !string.IsNullOrEmpty(packageUri.Fragment))
        {
            throw new ArgumentException(
                "Composed Gateway package URI must be an absolute, credential-free HTTP(S) .tgz URI without query or fragment.",
                nameof(packageUri));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var normalizedSha256 = sha256.Trim().ToLowerInvariant();
        if (!Sha256Regex().IsMatch(normalizedSha256))
            throw new ArgumentException("Gateway package SHA-256 must contain exactly 64 hexadecimal characters.", nameof(sha256));

        return new(
            GatewayPackageSource.Composed,
            NormalizeExactVersion(expectedVersion),
            packageUri,
            normalizedSha256);
    }

    internal static bool TryRestore(
        GatewayPackageSource source,
        string expectedVersion,
        string? packageUri,
        string? sha256,
        out GatewayPackageTarget? target)
    {
        target = null;
        try
        {
            target = source switch
            {
                GatewayPackageSource.Official when packageUri is null && sha256 is null =>
                    Official(expectedVersion),
                GatewayPackageSource.Composed when packageUri is not null && sha256 is not null =>
                    Composed(expectedVersion, new Uri(packageUri, UriKind.Absolute), sha256),
                _ => null
            };
            return target is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    private static string NormalizeExactVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (!ExactVersionRegex().IsMatch(normalized))
            throw new ArgumentException("Gateway package target requires an exact semantic version.", nameof(value));
        return normalized;
    }

    internal static int ComparePrecedence(string left, string right)
    {
        var normalizedLeft = NormalizeExactVersion(left);
        var normalizedRight = NormalizeExactVersion(right);
        var leftParts = ParsePrecedence(normalizedLeft);
        var rightParts = ParsePrecedence(normalizedRight);

        for (var i = 0; i < leftParts.Core.Count; i++)
        {
            var coreComparison = leftParts.Core[i].CompareTo(rightParts.Core[i]);
            if (coreComparison != 0)
                return coreComparison;
        }

        if (leftParts.PreRelease.Count == 0)
            return rightParts.PreRelease.Count == 0 ? 0 : 1;
        if (rightParts.PreRelease.Count == 0)
            return -1;

        for (var i = 0; i < Math.Min(leftParts.PreRelease.Count, rightParts.PreRelease.Count); i++)
        {
            var leftIdentifier = leftParts.PreRelease[i];
            var rightIdentifier = rightParts.PreRelease[i];
            var leftNumeric = leftIdentifier.All(char.IsAsciiDigit);
            var rightNumeric = rightIdentifier.All(char.IsAsciiDigit);
            var comparison = leftNumeric && rightNumeric
                ? BigInteger.Parse(leftIdentifier).CompareTo(BigInteger.Parse(rightIdentifier))
                : leftNumeric ? -1
                : rightNumeric ? 1
                : string.Compare(leftIdentifier, rightIdentifier, StringComparison.Ordinal);
            if (comparison != 0)
                return comparison;
        }

        return leftParts.PreRelease.Count.CompareTo(rightParts.PreRelease.Count);
    }

    private static SemanticVersionPrecedence ParsePrecedence(string version)
    {
        var withoutBuildMetadata = version.Split('+', 2)[0];
        var coreAndPreRelease = withoutBuildMetadata.Split('-', 2);
        return new(
            coreAndPreRelease[0].Split('.').Select(BigInteger.Parse).ToArray(),
            coreAndPreRelease.Length == 2 ? coreAndPreRelease[1].Split('.').ToArray() : []);
    }

    private sealed record SemanticVersionPrecedence(
        IReadOnlyList<BigInteger> Core,
        IReadOnlyList<string> PreRelease);

    private const string ExactVersionPattern =
        @"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)" +
        @"(?:-(?:(?:0|[1-9]\d*)|(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))" +
        @"(?:\.(?:(?:0|[1-9]\d*)|(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)))*)?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?";

    [GeneratedRegex(@"\A" + ExactVersionPattern + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersionRegex();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}

/// <summary>
/// Pure first-adoption route policy. Unknown and legacy source versions stay on
/// the Companion installer lane. Core is available only for explicitly audited
/// official-package source versions.
/// </summary>
public sealed class GatewayPackageUpdateRoutePolicy
{
    private const string LastLegacySourceVersion = "2026.7.2";

    private readonly HashSet<string> _auditedCoreSourceVersions;

    public GatewayPackageUpdateRoutePolicy(IEnumerable<string>? auditedCoreSourceVersions = null)
    {
        _auditedCoreSourceVersions = new(StringComparer.Ordinal);
        foreach (var version in auditedCoreSourceVersions ?? [])
            _auditedCoreSourceVersions.Add(GatewayPackageTarget.Official(version).ExpectedVersion);
    }

    public GatewayPackageUpdateRoute Select(string installedVersion, GatewayPackageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalizedInstalledVersion = GatewayPackageTarget.Official(installedVersion).ExpectedVersion;
        if (target.Source == GatewayPackageSource.Composed ||
            GatewayPackageTarget.ComparePrecedence(normalizedInstalledVersion, LastLegacySourceVersion) <= 0 ||
            !_auditedCoreSourceVersions.Contains(normalizedInstalledVersion))
        {
            return GatewayPackageUpdateRoute.CompanionInstaller;
        }

        return GatewayPackageUpdateRoute.CoreTransaction;
    }
}

/// <summary>
/// Canonical Gateway installer command construction shared by first install and
/// in-place Companion updates.
/// </summary>
public static class GatewayPackageInstallCommandBuilder
{
    public const string DefaultInstallUrl = "https://openclaw.ai/install-cli.sh";

    public static string Build(
        string installUrl,
        string? requestedVersion,
        string? expectedPackageSha256 = null)
    {
        var escapedUrl = WslShellQuoting.EscapePosixSingleQuoteInner(installUrl);
        if (!string.IsNullOrWhiteSpace(expectedPackageSha256))
            return BuildVerifiedPackage(escapedUrl, requestedVersion, expectedPackageSha256);

        if (string.IsNullOrWhiteSpace(requestedVersion))
            return $"curl -fsSL --proto '=https' --tlsv1.2 '{escapedUrl}' | bash";

        var trimmedVersion = requestedVersion.Trim();
        if (trimmedVersion.Contains('\n') || trimmedVersion.Contains('\r'))
            throw new ArgumentException("Gateway version cannot contain newlines.");

        var escapedVersion = WslShellQuoting.EscapePosixSingleQuoteInner(trimmedVersion);
        return $"curl -fsSL --proto '=https' --tlsv1.2 '{escapedUrl}' | bash -s -- --version '{escapedVersion}'";
    }

    public static string Build(string installUrl, GatewayPackageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Source switch
        {
            GatewayPackageSource.Official =>
                Build(installUrl, target.ExpectedVersion),
            GatewayPackageSource.Composed =>
                Build(installUrl, target.PackageUri!.AbsoluteUri, target.Sha256),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }

    private static string BuildVerifiedPackage(
        string escapedInstallUrl,
        string? requestedVersion,
        string expectedPackageSha256)
    {
        var normalizedSha256 = expectedPackageSha256.Trim().ToLowerInvariant();
        if (normalizedSha256.Length != 64 || !normalizedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected gateway package SHA-256 must contain exactly 64 hexadecimal characters.");

        var packageSpec = requestedVersion?.Trim();
        if (string.IsNullOrWhiteSpace(packageSpec) ||
            packageSpec.Contains('\n') ||
            packageSpec.Contains('\r') ||
            !Uri.TryCreate(packageSpec, UriKind.Absolute, out var packageUri) ||
            !packageUri.IsWellFormedOriginalString() ||
            (packageUri.Scheme != Uri.UriSchemeHttp && packageUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(packageUri.Host) ||
            !packageUri.AbsolutePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(packageUri.UserInfo) ||
            !string.IsNullOrEmpty(packageUri.Query) ||
            !string.IsNullOrEmpty(packageUri.Fragment))
        {
            throw new ArgumentException(
                "Expected gateway package SHA-256 requires Version to be a credential-free HTTP(S) .tgz URL without query or fragment.");
        }

        var escapedPackageSpec = WslShellQuoting.EscapePosixSingleQuoteInner(packageSpec);
        var packageCurlOptions = packageUri.Scheme == Uri.UriSchemeHttps
            ? "--proto '=https' --tlsv1.2"
            : "--proto '=http'";

        return
            "download_dir=\"$(mktemp -d /tmp/openclaw-install.XXXXXX)\"" +
            " && trap 'rm -rf -- \"$download_dir\"' EXIT" +
            " && package_path=\"$download_dir/openclaw.tgz\"" +
            " && installer_path=\"$download_dir/install-cli.sh\"" +
            $" && curl -fsSL {packageCurlOptions} '{escapedPackageSpec}' -o \"$package_path\"" +
            $" && printf '%s  %s\\n' '{normalizedSha256}' \"$package_path\" | sha256sum --check --strict -" +
            $" && curl -fsSL --proto '=https' --tlsv1.2 '{escapedInstallUrl}' -o \"$installer_path\"" +
            " && bash \"$installer_path\" --version \"$package_path\"";
    }
}
