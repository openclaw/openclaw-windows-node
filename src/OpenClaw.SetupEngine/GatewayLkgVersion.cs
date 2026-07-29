namespace OpenClaw.SetupEngine;

public static class GatewayLkgVersion
{
    public const string DefaultInstallUrl = OpenClaw.Connection.GatewayPackageInstallCommandBuilder.DefaultInstallUrl;
    public const string LkgVersion = "2026.7.1";

    public static string ResolveLkgVersion() => LkgVersion;

    public static void ApplyToConfig(SetupConfig config)
        => ApplyToConfig(config, OpenClaw.Connection.GatewayPackageBuildTargetResolver.Resolve());

    internal static void ApplyToConfig(
        SetupConfig config,
        OpenClaw.Connection.GatewayPackageTarget target)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(target);
        if (!string.IsNullOrWhiteSpace(config.Gateway.Version))
            return;

        if (!string.IsNullOrWhiteSpace(config.Gateway.InstallUrl) &&
            !string.Equals(config.Gateway.InstallUrl, DefaultInstallUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var configuredSha256 = config.Gateway.ExpectedPackageSha256?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredSha256) &&
            !string.Equals(configuredSha256, target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Configured Gateway package digest does not match the resolved immutable package target.");
        }

        config.Gateway.Version = target.Source == OpenClaw.Connection.GatewayPackageSource.Official
            ? target.ExpectedVersion
            : target.PackageUri!.AbsoluteUri;
        config.Gateway.ExpectedInstalledVersion = target.ExpectedVersion;
        config.Gateway.ExpectedPackageSha256 = target.Sha256;
    }

    internal static string? ResolveExpectedInstalledVersion(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var configuredExpectedVersion = config.Gateway.ExpectedInstalledVersion?.Trim();
        var configuredPackageSpec = config.Gateway.Version?.Trim();
        var configuredSha256 = config.Gateway.ExpectedPackageSha256?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredExpectedVersion))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(configuredSha256))
                {
                    var composedTarget = OpenClaw.Connection.GatewayPackageTarget.Composed(
                        configuredExpectedVersion,
                        new Uri(configuredPackageSpec ?? string.Empty, UriKind.Absolute),
                        configuredSha256);
                    return composedTarget.ExpectedVersion;
                }

                var officialTarget = OpenClaw.Connection.GatewayPackageTarget.Official(
                    configuredExpectedVersion);
                if (!string.Equals(
                        configuredPackageSpec,
                        officialTarget.ExpectedVersion,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Official Gateway version does not match ExpectedInstalledVersion.");
                }

                return officialTarget.ExpectedVersion;
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                throw new InvalidOperationException("Configured Gateway package identity is invalid.", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Gateway.InstallUrl) &&
            !string.Equals(config.Gateway.InstallUrl, DefaultInstallUrl, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = OpenClaw.Connection.GatewayPackageBuildTargetResolver.Resolve();
        var expectedVersionSpec = target.Source == OpenClaw.Connection.GatewayPackageSource.Official
            ? target.ExpectedVersion
            : target.PackageUri!.AbsoluteUri;
        return string.Equals(config.Gateway.Version?.Trim(), expectedVersionSpec, StringComparison.Ordinal) &&
               (string.IsNullOrWhiteSpace(config.Gateway.ExpectedInstalledVersion) ||
                string.Equals(
                    config.Gateway.ExpectedInstalledVersion.Trim(),
                    target.ExpectedVersion,
                    StringComparison.Ordinal)) &&
               string.Equals(
                   config.Gateway.ExpectedPackageSha256?.Trim(),
                   target.Sha256,
                   StringComparison.OrdinalIgnoreCase)
            ? target.ExpectedVersion
            : null;
    }

    internal static string? ResolveSchemaVersion(GatewayConfig config)
        => ResolveSchemaVersion(
            config,
            OpenClaw.Connection.GatewayPackageBuildTargetResolver.Resolve());

    internal static string? ResolveSchemaVersion(
        GatewayConfig config,
        OpenClaw.Connection.GatewayPackageTarget target)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(target);
        if (!string.IsNullOrWhiteSpace(config.ExpectedInstalledVersion))
        {
            return config.ExpectedInstalledVersion.Trim();
        }

        if (target.Source == OpenClaw.Connection.GatewayPackageSource.Composed &&
            string.Equals(config.Version?.Trim(), target.PackageUri?.AbsoluteUri, StringComparison.Ordinal) &&
            string.Equals(config.ExpectedPackageSha256?.Trim(), target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return target.ExpectedVersion;
        }

        return config.Version;
    }
}
