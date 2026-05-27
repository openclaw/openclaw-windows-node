namespace OpenClaw.SetupEngine;

public static class GatewayLkgVersion
{
    public const string LkgVersion = "2026.5.22";

    public static string ResolveLkgVersion() => LkgVersion;

    public static void ApplyToConfig(SetupConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Gateway.Version))
            return;

        config.Gateway.Version = LkgVersion;
    }
}
