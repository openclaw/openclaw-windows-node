namespace OpenClaw.SetupEngine;

public static class GatewayLkgVersion
{
    public const string DefaultInstallUrl = "https://openclaw.ai/install-cli.sh";
    public const string DefaultWindowsInstallUrl = "https://openclaw.ai/install.ps1";
    public const string LkgVersion = "2026.6.11";

    public static string ResolveLkgVersion() => LkgVersion;

    public static void ApplyToConfig(SetupConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Gateway.Version) && !config.GatewayVersionWasDefaulted)
            return;

        var previousVersionWasDefaulted = config.GatewayVersionWasDefaulted;
        config.GatewayVersionWasDefaulted = false;

        var installUrl = config.InstallMode == GatewayInstallMode.NativeWindows
            ? config.Gateway.WindowsInstallUrl
            : config.Gateway.InstallUrl;
        var defaultInstallUrl = config.InstallMode == GatewayInstallMode.NativeWindows
            ? DefaultWindowsInstallUrl
            : DefaultInstallUrl;
        if (!string.IsNullOrWhiteSpace(installUrl) &&
            !string.Equals(installUrl, defaultInstallUrl, StringComparison.OrdinalIgnoreCase))
        {
            if (previousVersionWasDefaulted)
                config.Gateway.Version = null;
            return;
        }

        config.Gateway.Version = LkgVersion;
        config.GatewayVersionWasDefaulted = true;
    }
}
