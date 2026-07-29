using System.Reflection;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Resolves the immutable Gateway package identity embedded in the shared
/// assembly at build time.
/// </summary>
public static class GatewayPackageBuildTargetResolver
{
    public const string SourceMetadataKey = "OpenClawGatewayPackageSource";
    public const string ExpectedVersionMetadataKey = "OpenClawGatewayPackageExpectedVersion";
    public const string PackageUriMetadataKey = "OpenClawGatewayPackageUri";
    public const string Sha256MetadataKey = "OpenClawGatewayPackageSha256";

    public static GatewayPackageTarget Resolve() =>
        Resolve(typeof(AppVersionInfo).Assembly);

    internal static GatewayPackageTarget Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Resolve(assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Select(attribute => new KeyValuePair<string, string?>(
                attribute.Key,
                attribute.Value)));
    }

    internal static GatewayPackageTarget Resolve(
        IEnumerable<KeyValuePair<string, string?>> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var values = metadata
            .Where(item =>
                item.Key is SourceMetadataKey or ExpectedVersionMetadataKey or PackageUriMetadataKey or Sha256MetadataKey)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var matches = group.ToArray();
                    if (matches.Length != 1)
                        throw new InvalidOperationException($"Duplicate Gateway package metadata '{group.Key}'.");
                    return matches[0].Value?.Trim();
                },
                StringComparer.Ordinal);

        var source = Require(values, SourceMetadataKey);
        var expectedVersion = Require(values, ExpectedVersionMetadataKey);
        values.TryGetValue(PackageUriMetadataKey, out var packageUri);
        values.TryGetValue(Sha256MetadataKey, out var sha256);

        if (source.Equals(nameof(GatewayPackageSource.Official), StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(packageUri) || !string.IsNullOrWhiteSpace(sha256))
            {
                throw new InvalidOperationException(
                    "Official Gateway package metadata cannot include a package URI or SHA-256.");
            }

            return GatewayPackageTarget.Official(expectedVersion);
        }

        if (source.Equals(nameof(GatewayPackageSource.Composed), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return GatewayPackageTarget.Composed(
                    expectedVersion,
                    new Uri(Require(values, PackageUriMetadataKey), UriKind.Absolute),
                    Require(values, Sha256MetadataKey));
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                throw new InvalidOperationException("Invalid composed Gateway package build metadata.", ex);
            }
        }

        throw new InvalidOperationException(
            $"Gateway package metadata '{SourceMetadataKey}' must be Official or Composed.");
    }

    private static string Require(
        IReadOnlyDictionary<string, string?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing Gateway package metadata '{key}'.");
        return value;
    }
}
