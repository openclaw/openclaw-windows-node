namespace OpenClaw.SetupEngine;

public static class GatewayLkgVersion
{
    public const string DefaultInstallUrl = "https://openclaw.ai/install-cli.sh";
    public const string DefaultWindowsInstallUrl = "https://openclaw.ai/install.ps1";
    public const string LkgVersion = "2026.6.11";
    public const string ManagedNodeVersion = "24.15.0";
    public const string ManagedNodeX64Sha256 = "cc5149eabd53779ce1e7bdc5401643622d0c7e6800ade18928a767e940bb0e62";
    public const string ManagedNodeArm64Sha256 = "c9eb7402eda26e2ba7e44b6727fc85a8de56c5095b1f71ebd3062892211aa116";

    public static string ResolveLkgVersion() => LkgVersion;

    internal static bool ShouldUseManagedWindowsInstaller(string installUrl) =>
        string.Equals(installUrl, DefaultWindowsInstallUrl, StringComparison.OrdinalIgnoreCase);

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
