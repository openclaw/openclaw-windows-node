namespace OpenClaw.SetupEngine;

public static class GatewayLkgVersion
{
    public const string DefaultInstallUrl = "https://openclaw.ai/install-cli.sh";
    public const string LkgVersion = "2026.6.5";

    public static string ResolveLkgVersion() => LkgVersion;

    public static void ApplyToConfig(SetupConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Gateway.Version))
            return;

        if (!string.IsNullOrWhiteSpace(config.Gateway.InstallUrl) &&
            !string.Equals(config.Gateway.InstallUrl, DefaultInstallUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        config.Gateway.Version = LkgVersion;
    }
}
