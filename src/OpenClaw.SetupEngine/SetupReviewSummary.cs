namespace OpenClaw.SetupEngine;

public sealed record SetupReviewSummary(
    string DistroTitle,
    string DistroDescription,
    string InstallerDescription,
    string InstallerBadge,
    string GatewayDescription,
    string GatewayEndpoint,
    string ExactCommands,
    string CompletionGatewaySummary);

public static class SetupReviewSummaryBuilder
{
    public static SetupReviewSummary Build(SetupConfig config)
    {
        var distroName = Display(config.DistroName, "OpenClawGateway");
        var baseDistro = Display(config.BaseDistro, "Ubuntu-24.04");
        var gatewayBind = Display(config.Gateway.Bind, "loopback");
        var gatewayPort = config.GatewayPort;
        var installPath = Path.Combine(SetupContext.ResolveLocalDataDir(), "wsl", distroName);
        var gatewayDataPath = Path.Combine(SetupContext.ResolveDataDir(), "gateways.json");
        var installUrl = config.Gateway.InstallUrl ?? GatewayLkgVersion.DefaultInstallUrl;
        var installerHost = TryGetHttpsHost(installUrl);
        var installerDescription = installerHost is null
            ? "Installer URL is not HTTPS; setup will stop before downloading anything."
            : $"Fetched over HTTPS from {installerHost}; runs as a non-root {Display(config.Wsl.User, "openclaw")} user inside the instance.";
        var installerBadge = installerHost is null ? "Invalid URL" : "HTTPS";
        var isLanBind = gatewayBind.Equals("lan", StringComparison.OrdinalIgnoreCase);
        var gatewayDescription = isLanBind
            ? "LAN bind enabled — reachable from this PC and your local network according to Windows firewall/routing."
            : "Loopback only — not reachable from your network or the internet.";
        var gatewayEndpoint = isLanBind ? $"LAN:{gatewayPort}" : $"127.0.0.1:{gatewayPort}";
        var wslCommand = "wsl " + string.Join(' ', WslInstallSupport.BuildDirectInstallArgs(baseDistro, distroName, installPath));
        var installCommand = string.IsNullOrWhiteSpace(config.Gateway.Version)
            ? "curl -fsSL --proto '=https' --tlsv1.2 <install-url> | bash"
            : $"curl -fsSL --proto '=https' --tlsv1.2 <install-url> | bash -s -- --version {config.Gateway.Version.Trim()}";

        return new SetupReviewSummary(
            DistroTitle: $"Install an isolated {baseDistro} instance",
            DistroDescription: $"WSL distro \"{distroName}\" at {installPath}. Separate from any Linux distributions you already have.",
            InstallerDescription: installerDescription,
            InstallerBadge: installerBadge,
            GatewayDescription: gatewayDescription,
            GatewayEndpoint: gatewayEndpoint,
            ExactCommands: string.Join(
                Environment.NewLine,
                wslCommand,
                installCommand,
                $"openclaw config set gateway.bind {gatewayBind} · port {gatewayPort}",
                "openclaw gateway install --force   (systemd --user service)",
                $"writes -> {installPath}",
                $"writes -> {gatewayDataPath} + identity"),
            CompletionGatewaySummary: $"{distroName} · {gatewayEndpoint}");
    }

    private static string Display(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? TryGetHttpsHost(string installUrl)
        => Uri.TryCreate(installUrl, UriKind.Absolute, out var uri)
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.Host
            : null;
}
