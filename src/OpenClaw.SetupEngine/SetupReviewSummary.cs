namespace OpenClaw.SetupEngine;

using OpenClaw.Shared.Inference.Catalog;

public sealed record SetupReviewSummary(
    string DistroTitle,
    string DistroDescription,
    string InstallerDescription,
    string InstallerBadge,
    string GatewayDescription,
    string GatewayEndpoint,
    string ExactCommands,
    string CompletionGatewaySummary)
{
    public bool LocalAiEnabled { get; init; }
    public string? LocalAiTitle { get; init; }
    public string? LocalAiDescription { get; init; }
}

public static class SetupReviewSummaryBuilder
{
    public static SetupReviewSummary Build(SetupConfig config, string? dataDir = null, string? localDataDir = null)
    {
        var distroName = Display(config.DistroName, "OpenClawGateway");
        var baseDistro = Display(config.BaseDistro, "Ubuntu-24.04");
        var gatewayBind = Display(config.Gateway.Bind, "loopback");
        var gatewayPort = config.GatewayPort;
        var installPath = Path.Combine(localDataDir ?? SetupContext.ResolveLocalDataDir(), "wsl", distroName);
        var gatewayDataPath = Path.Combine(dataDir ?? SetupContext.ResolveDataDir(), "gateways.json");
        var release = config.Gateway.ResolvedRelease ?? GatewayReleasePolicy.ResolveAndApply(config);
        var installUrl = config.Gateway.InstallUrl ?? GatewayReleasePolicy.DefaultInstallUrl;
        var installerHost = TryGetHttpsHost(installUrl);
        var installerDescription = installerHost is null
            ? "Installer URL is not HTTPS; setup will stop before downloading anything."
            : release.IsCustomInstaller
                ? $"Unverified custom installer from {installerHost}; exact Gateway {release.Version}, protocol v{release.ProtocolGeneration} is checked after install."
                : $"Official Gateway {release.Version}; validated for protocol v{release.ProtocolGeneration} and fetched over HTTPS from {installerHost}.";
        var installerBadge = installerHost is null
            ? "Invalid URL"
            : release.IsCustomInstaller ? "Custom" : $"v{release.ProtocolGeneration} validated";
        var isLanBind = gatewayBind.Equals("lan", StringComparison.OrdinalIgnoreCase);
        var tailscaleEnabled = config.Tailscale.Enabled;
        var tailnetDnsSuffix = config.Tailscale.TailnetDnsSuffix?.Trim().Trim('.');
        var tailscaleEndpoint = string.IsNullOrWhiteSpace(tailnetDnsSuffix)
            ? $"wss://{config.Tailscale.EffectiveHostname}.<tailnet>.ts.net"
            : $"wss://{config.Tailscale.EffectiveHostname}.{tailnetDnsSuffix}";
        var gatewayDescription = tailscaleEnabled
            ? config.Tailscale.TrustTailscaleAuth
                ? "Tailscale Serve enabled: the gateway stays loopback-only, trusts tailnet identity authentication, and Companion connects over private HTTPS/WSS."
                : "Tailscale Serve enabled: the gateway stays loopback-only, requires existing Companion token or device authentication, and connects over private HTTPS/WSS."
            : isLanBind
            ? "LAN bind enabled: reachable from this PC and your local network according to Windows firewall/routing."
            : "Loopback only. It is not reachable from your network or the internet.";
        var gatewayEndpoint = tailscaleEnabled
            ? tailscaleEndpoint
            : isLanBind ? $"LAN:{gatewayPort}" : $"127.0.0.1:{gatewayPort}";
        var wslCommand = "wsl " + string.Join(' ', WslInstallSupport.BuildDirectInstallArgs(baseDistro, distroName, installPath));
        var runtimeArgument = release.IsCustomInstaller
            ? ""
            : $" --node-version {GatewayReleasePolicy.NodeVersion}";
        var installCommand =
            $"curl -fsSL --proto '=https' --tlsv1.2 <install-url> | bash -s -- --version {release.Version}{runtimeArgument}";
        LocalModelInfo localAiModel =
            LocalModelCatalog.Find(config.LocalAi.SelectedModelId) ?? LocalModelCatalog.Default;
        string[] localAiCommands = config.LocalAi.Enabled
            ?
            [
                "download verified llama-server + CUDA runtime for Windows",
                $"download {localAiModel.Weights.RelativePath} from Hugging Face revision " +
                    ((HuggingFaceRevisionSource)localAiModel.Weights.Source).RevisionSha,
                $"llama-server router on dynamic 127.0.0.1 port; model loads on first request",
                $"openclaw provider llamacpp -> /v1; primary llamacpp/{localAiModel.Id}",
            ]
            : [];

        var summary = new SetupReviewSummary(
            DistroTitle: $"Install an isolated {baseDistro} instance",
            DistroDescription: $"WSL distro \"{distroName}\" at {installPath}. Separate from any Linux distributions you already have. Disk use grows dynamically and is typically several GB.",
            InstallerDescription: installerDescription,
            InstallerBadge: installerBadge,
            GatewayDescription: gatewayDescription,
            GatewayEndpoint: gatewayEndpoint,
            ExactCommands: string.Join(
                Environment.NewLine,
                new[]
                {
                    wslCommand,
                    installCommand,
                    $"openclaw config set gateway.bind {gatewayBind} · port {gatewayPort}",
                    tailscaleEnabled
                        ? config.Tailscale.TrustTailscaleAuth
                            ? "install signed Tailscale package · root owns tailscale up/serve · identity auth enabled"
                            : "install signed Tailscale package · root owns tailscale up/serve"
                        : null,
                    "openclaw gateway install --force   (systemd --user service)",
                }.Concat(localAiCommands).Concat(new[]
                {
                    $"writes -> {installPath}",
                    $"writes -> {gatewayDataPath} + identity"
                }).Where(line => line is not null)),
            CompletionGatewaySummary: $"{distroName} · {gatewayEndpoint}");
        return summary with
        {
            LocalAiEnabled = config.LocalAi.Enabled,
            LocalAiTitle = config.LocalAi.Enabled
                ? $"Local AI verified with {localAiModel.DisplayName}"
                : null,
            LocalAiDescription = config.LocalAi.Enabled
                ? "llama-server · " +
                    $"{localAiModel.Recipe.ContextTokens / 1024}K context · FP16 KV · full CUDA offload · loads on first request"
                : null,
        };
    }

    private static string Display(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? TryGetHttpsHost(string installUrl)
        => Uri.TryCreate(installUrl, UriKind.Absolute, out var uri)
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.Host
            : null;
}
