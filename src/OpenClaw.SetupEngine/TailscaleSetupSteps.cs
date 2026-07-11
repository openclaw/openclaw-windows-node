using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

/// <summary>Pure policy and parsing helpers for the optional Tailscale setup path.</summary>
public static partial class TailscaleSetupPolicy
{
    private const string DefaultHostnamePrefix = "openclaw-";

    public static string? ValidateConfig(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var tailscale = config.Tailscale;
        if (!tailscale.Enabled)
            return null;

        if (!string.IsNullOrWhiteSpace(config.GatewayUrl))
            return "Tailscale setup derives the gateway URL automatically; GatewayUrl must be null.";
        if (!string.Equals(config.Gateway.Bind, "loopback", StringComparison.OrdinalIgnoreCase))
            return "Tailscale Serve requires Gateway.Bind to be 'loopback'.";
        if (tailscale.AuthTimeoutSeconds is < 30 or > 1800)
            return "Tailscale.AuthTimeoutSeconds must be between 30 and 1800 seconds.";
        if (tailscale.AuthMode == TailscaleAuthMode.AuthKey && string.IsNullOrWhiteSpace(tailscale.AuthKey))
            return "Tailscale auth-key mode requires OPENCLAW_SETUP_TAILSCALE_AUTH_KEY.";
        if (string.IsNullOrWhiteSpace(NormalizeHostname(tailscale.Hostname, Environment.MachineName)))
            return "Tailscale hostname could not be derived from the configured value.";

        return null;
    }

    public static string NormalizeHostname(string? requestedHostname, string machineName)
    {
        var source = string.IsNullOrWhiteSpace(requestedHostname)
            ? DefaultHostnamePrefix + machineName
            : requestedHostname;
        var normalized = InvalidHostnameCharacters().Replace(source.Trim().ToLowerInvariant(), "-");
        normalized = MultipleHyphens().Replace(normalized, "-").Trim('-');
        if (normalized.Length > 63)
            normalized = normalized[..63].TrimEnd('-');
        return string.IsNullOrWhiteSpace(normalized) ? "openclaw-gateway" : normalized;
    }

    public static bool TryParseStatus(string json, out TailscaleStatus status)
    {
        status = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var backendState = root.TryGetProperty("BackendState", out var state)
                ? state.GetString() ?? string.Empty
                : string.Empty;
            var dnsName = root.TryGetProperty("Self", out var self) &&
                          self.TryGetProperty("DNSName", out var dns)
                ? dns.GetString()
                : null;
            status = new TailscaleStatus(backendState, dnsName?.Trim().TrimEnd('.'));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? GetTailnetDnsSuffix(string? dnsName)
    {
        var normalized = dnsName?.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var separator = normalized.IndexOf('.');
        return separator > 0 && separator < normalized.Length - 1
            ? normalized[(separator + 1)..]
            : null;
    }

    public static Uri? TryReadAuthorizationUrl(string output)
    {
        var match = TailscaleAuthorizationUrl().Match(output);
        return match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    /// <summary>
    /// Verifies that a Tailscale Serve status document has an actual Web handler
    /// proxying to the generated gateway's loopback HTTP port. Do not accept a
    /// matching port elsewhere in the document: TCP listeners and unrelated
    /// handlers do not prove that this gateway endpoint routes to OpenClaw.
    /// </summary>
    public static bool ServeStatusRoutesToPort(string status, int port)
    {
        if (port is <= 0 or > 65535)
            return false;

        try
        {
            using var document = JsonDocument.Parse(status);
            if (!document.RootElement.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var webEndpoint in web.EnumerateObject())
            {
                if (!webEndpoint.Value.TryGetProperty("Handlers", out var handlers) || handlers.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var handler in handlers.EnumerateObject())
                {
                    if (handler.Value.ValueKind != JsonValueKind.Object ||
                        !handler.Value.TryGetProperty("Proxy", out var proxy) ||
                        proxy.ValueKind != JsonValueKind.String)
                        continue;

                    if (IsLoopbackGatewayProxy(proxy.GetString(), port))
                        return true;
                }
            }
        }
        catch (JsonException)
        {
            // Serve has not produced a usable JSON status document.
        }

        return false;
    }

    private static bool IsLoopbackGatewayProxy(string? proxy, int port) =>
        Uri.TryCreate(proxy, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == port &&
        (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidHostnameCharacters();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleHyphens();

    [GeneratedRegex(@"https://login\.tailscale\.com/a/[A-Za-z0-9_-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TailscaleAuthorizationUrl();
}

public sealed record TailscaleStatus(string BackendState, string? DnsName)
{
    public bool IsRunning => BackendState.Equals("Running", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(DnsName);
}

public sealed class PreflightWindowsTailscaleStep : SetupStep
{
    public override string Id => "preflight-windows-tailscale";
    public override string DisplayName => "Check Windows Tailscale";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.Tailscale.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (TailscaleSetupPolicy.ValidateConfig(ctx.Config) is { } configError)
            return StepResult.Terminal(configError);

        var result = await ctx.Commands.RunAsync(
            ResolveWindowsTailscaleCliPath(),
            ["status", "--json"],
            TimeSpan.FromSeconds(15),
            ct: ct);
        if (result.ExitCode != 0)
            return StepResult.Terminal("Windows Tailscale must be installed and signed in before creating a Tailscale gateway.");
        if (!TailscaleSetupPolicy.TryParseStatus(result.Stdout, out var status) || !status.IsRunning)
            return StepResult.Terminal("Windows Tailscale is not connected. Sign in to Tailscale and retry setup.");
        if (TailscaleSetupPolicy.GetTailnetDnsSuffix(status.DnsName) is not { } suffix)
            return StepResult.Terminal("Windows Tailscale did not report a MagicDNS name. Enable MagicDNS for this tailnet before using Tailscale Serve.");

        ctx.WindowsTailnetDnsSuffix = suffix;
        ctx.Config.Tailscale.TailnetDnsSuffix = suffix;
        ctx.Logger.Info($"Windows Tailscale connected to tailnet suffix .{suffix}");
        return StepResult.Ok("Windows Tailscale connected");
    }

    public static string ResolveWindowsTailscaleCliPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installedPath = Path.Combine(programFiles, "Tailscale", "tailscale.exe");
        return File.Exists(installedPath) ? installedPath : "tailscale.exe";
    }
}

public sealed class InstallTailscaleStep : SetupStep
{
    private const string NobleKeyringUrl = "https://pkgs.tailscale.com/stable/ubuntu/noble.noarmor.gpg";
    private const string NobleRepository = "deb [signed-by=/usr/share/keyrings/tailscale-archive-keyring.gpg] https://pkgs.tailscale.com/stable/ubuntu noble main";

    public override string Id => "install-tailscale";
    public override string DisplayName => "Install Tailscale in WSL";

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.Tailscale.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var script = BuildInstallScript();
        var result = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!, script, TimeSpan.FromMinutes(3), ct: ct, user: "root", inputViaStdin: true);
        if (result.ExitCode != 0)
            return StepResult.Fail($"Tailscale installation failed (exit {result.ExitCode}): {result.Stderr}");

        return StepResult.Ok("Tailscale daemon installed");
    }

    internal static string BuildInstallScript() => $"""
        set -eu
        . /etc/os-release
        if [ "$ID" != "ubuntu" ] || [ "$VERSION_ID" != "24.04" ] || [ "$VERSION_CODENAME" != "noble" ]; then
            echo "OpenClaw's generated Tailscale gateway requires Ubuntu 24.04 (noble); found $ID $VERSION_ID $VERSION_CODENAME" >&2
            exit 1
        fi
        install -d -m 0755 /usr/share/keyrings
        curl -fsSL --proto '=https' --tlsv1.2 {NobleKeyringUrl} -o /tmp/openclaw-tailscale-keyring.gpg
        install -m 0644 /tmp/openclaw-tailscale-keyring.gpg /usr/share/keyrings/tailscale-archive-keyring.gpg
        rm -f /tmp/openclaw-tailscale-keyring.gpg
        cat > /etc/apt/sources.list.d/tailscale.list <<'EOF'
        {NobleRepository}
        EOF
        apt-get update
        DEBIAN_FRONTEND=noninteractive apt-get install -y tailscale
        systemctl enable --now tailscaled
        tailscale version
        """;

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var script = "tailscale serve reset 2>/dev/null || true; tailscale logout 2>/dev/null || true; systemctl disable --now tailscaled 2>/dev/null || true";
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, script, TimeSpan.FromSeconds(30), ct: ct, user: "root");
    }
}

public sealed class AuthorizeTailscaleStep : SetupStep
{
    public override string Id => "authorize-tailscale";
    public override string DisplayName => "Authorize Tailscale gateway";

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.Tailscale.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var config = ctx.Config.Tailscale;
        if (TailscaleSetupPolicy.ValidateConfig(ctx.Config) is { } configError)
            return StepResult.Terminal(configError);

        var hostname = config.EffectiveHostname;
        var upCommand = $"tailscale up --operator={ShellEscape(ctx.Config.Wsl.User)} --hostname={ShellEscape(hostname)}";
        CommandResult up;
        try
        {
            if (config.AuthMode == TailscaleAuthMode.AuthKey)
            {
                up = await ctx.Commands.RunInWslAsync(
                    ctx.DistroName!,
                    upCommand + " --auth-key=\"$TS_AUTHKEY\"",
                    TimeSpan.FromSeconds(60),
                    new Dictionary<string, string> { ["TS_AUTHKEY"] = config.AuthKey! },
                    ct,
                    user: "root",
                    inputViaStdin: true);
                if (up.ExitCode != 0)
                    return StepResult.Fail($"Tailscale auth-key authorization failed (exit {up.ExitCode}): {up.Stderr}");
            }
            else
            {
                up = await ctx.Commands.RunInWslAsync(
                    ctx.DistroName!, upCommand + " --timeout=5s", TimeSpan.FromSeconds(15), ct: ct, user: "root", inputViaStdin: true);
                if (TailscaleSetupPolicy.TryReadAuthorizationUrl(up.Stdout + "\n" + up.Stderr) is not { } authorizationUrl)
                    return StepResult.Fail("Tailscale did not provide a browser authorization URL.");

                var presenter = ctx.ExternalAuthorizationPresenter ?? new ConsoleExternalAuthorizationPresenter();
                await presenter.PresentAsync(
                    new ExternalAuthorizationRequest("Tailscale", authorizationUrl, "Authorize the generated OpenClaw gateway in your Tailscale tailnet:"),
                    ct);
            }

            var status = await WaitForRunningAsync(ctx, ct);
            if (status is null)
                return StepResult.Fail($"Tailscale authorization did not complete within {config.AuthTimeoutSeconds} seconds.");

            var wslSuffix = TailscaleSetupPolicy.GetTailnetDnsSuffix(status.DnsName);
            if (string.IsNullOrWhiteSpace(wslSuffix) || string.IsNullOrWhiteSpace(ctx.WindowsTailnetDnsSuffix))
                return StepResult.Fail("Could not compare the Windows and WSL Tailscale networks.");
            if (!string.Equals(wslSuffix, ctx.WindowsTailnetDnsSuffix, StringComparison.OrdinalIgnoreCase))
                return StepResult.Terminal($"The WSL gateway joined .{wslSuffix}, but Windows is connected to .{ctx.WindowsTailnetDnsSuffix}. Authorize both on the same tailnet and retry.");

            ctx.TailscaleDnsName = status.DnsName;
            ctx.Logger.Info($"WSL Tailscale gateway authorized as {status.DnsName}");
            return StepResult.Ok("Tailscale gateway authorized");
        }
        finally
        {
            // The key is intentionally ephemeral even in a long-lived SetupConfig instance.
            config.AuthKey = null;
        }
    }

    private static async Task<TailscaleStatus?> WaitForRunningAsync(SetupContext ctx, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(ctx.Config.Tailscale.AuthTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!, "tailscale status --json", TimeSpan.FromSeconds(15), ct: ct, user: "root");
            if (result.ExitCode == 0 && TailscaleSetupPolicy.TryParseStatus(result.Stdout, out var status) && status.IsRunning)
                return status;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return null;
    }

    private static string ShellEscape(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

public sealed class FinalizeTailscaleServeStep : SetupStep
{
    public override string Id => "finalize-tailscale-serve";
    public override string DisplayName => "Publish gateway on Tailscale";

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.Tailscale.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.TailscaleDnsName))
            return StepResult.Fail("Tailscale authorization did not produce a MagicDNS hostname.");

        var status = await GetServeStatusAsync(ctx, ct);
        if (!TailscaleSetupPolicy.ServeStatusRoutesToPort(status.Stdout, ctx.Config.GatewayPort))
        {
            var enable = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!,
                $"tailscale serve --bg --yes {ctx.Config.GatewayPort}",
                TimeSpan.FromSeconds(30),
                ct: ct,
                user: ctx.Config.Wsl.User);
            if (TailscaleSetupPolicy.TryReadAuthorizationUrl(enable.Stdout + "\n" + enable.Stderr) is { } authorizationUrl)
            {
                var presenter = ctx.ExternalAuthorizationPresenter ?? new ConsoleExternalAuthorizationPresenter();
                await presenter.PresentAsync(
                    new ExternalAuthorizationRequest("Tailscale", authorizationUrl, "Enable HTTPS for your Tailscale Serve endpoint:"),
                    ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                enable = await ctx.Commands.RunInWslAsync(
                    ctx.DistroName!,
                    $"tailscale serve --bg --yes {ctx.Config.GatewayPort}",
                    TimeSpan.FromSeconds(30),
                    ct: ct,
                    user: ctx.Config.Wsl.User);
            }

            if (enable.ExitCode != 0)
                return StepResult.Fail($"Tailscale Serve could not publish the gateway: {enable.Stderr}");
            status = await GetServeStatusAsync(ctx, ct);
        }

        if (!TailscaleSetupPolicy.ServeStatusRoutesToPort(status.Stdout, ctx.Config.GatewayPort))
            return StepResult.Fail("Tailscale Serve is not routing to the generated gateway port.");

        var endpoint = new UriBuilder(Uri.UriSchemeWss, ctx.TailscaleDnsName).Uri.AbsoluteUri.TrimEnd('/');
        var devicePairPublicUrl = new UriBuilder(Uri.UriSchemeHttps, ctx.TailscaleDnsName).Uri.AbsoluteUri.TrimEnd('/');
        var configurePairUrl = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            $"{ctx.WslPathPrefix} && openclaw config set {ConfigureGatewayStep.DevicePairPublicUrlKey} {ShellEscape(devicePairPublicUrl)} && openclaw config set {ConfigureGatewayStep.DevicePairEnabledKey} true",
            TimeSpan.FromSeconds(45),
            ct: ct);
        if (configurePairUrl.ExitCode != 0)
            return StepResult.Fail($"Could not configure the Tailscale device-pair URL: {configurePairUrl.Stderr}");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(devicePairPublicUrl, ct);
            ctx.Logger.Info($"Tailscale Serve health check: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StepResult.Fail($"Windows could not reach the Tailscale gateway endpoint: {ex.Message}");
        }

        ctx.GatewayUrl = endpoint;
        ctx.Logger.Info($"Companion will pair through {endpoint}");
        return StepResult.Ok("Tailscale Serve endpoint verified");
    }

    private static Task<CommandResult> GetServeStatusAsync(SetupContext ctx, CancellationToken ct) =>
        ctx.Commands.RunInWslAsync(
            ctx.DistroName!, "tailscale serve status --json", TimeSpan.FromSeconds(20), ct: ct, user: ctx.Config.Wsl.User);

    private static string ShellEscape(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
