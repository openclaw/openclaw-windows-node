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

        config.Gateway.Version = target.Source == OpenClaw.Connection.GatewayPackageSource.Official
            ? target.ExpectedVersion
            : target.PackageUri!.AbsoluteUri;
        config.Gateway.ExpectedPackageSha256 = target.Sha256;
    }

    internal static string? ResolveExpectedInstalledVersion(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
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
               string.Equals(
                   config.Gateway.ExpectedPackageSha256?.Trim(),
                   target.Sha256,
                   StringComparison.OrdinalIgnoreCase)
            ? target.ExpectedVersion
            : null;
    }
}
