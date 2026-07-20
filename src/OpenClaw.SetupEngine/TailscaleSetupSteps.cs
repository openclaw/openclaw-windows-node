using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

/// <summary>Pure policy and parsing helpers for the optional Tailscale setup path.</summary>
public static partial class TailscaleSetupPolicy
{
    private const string DefaultHostnamePrefix = "openclaw-";
    private const string SupportedBaseDistro = "Ubuntu-24.04";

    public static string? ValidateConfig(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var tailscale = config.Tailscale;
        if (!tailscale.Enabled)
            return null;

        if (!string.Equals(config.BaseDistro?.Trim(), SupportedBaseDistro, StringComparison.OrdinalIgnoreCase))
            return $"Tailscale setup currently requires BaseDistro '{SupportedBaseDistro}'. Choose that distro before replacing the generated gateway.";
        if (!string.IsNullOrWhiteSpace(config.GatewayUrl))
            return "Tailscale setup derives the gateway URL automatically; GatewayUrl must be null.";
        if (!string.Equals(config.Gateway.Bind, "loopback", StringComparison.OrdinalIgnoreCase))
            return "Tailscale Serve requires Gateway.Bind to be 'loopback'.";
        if (tailscale.AuthTimeoutSeconds is < 30 or > 1800)
            return "Tailscale.AuthTimeoutSeconds must be between 30 and 1800 seconds.";
        if (tailscale.ServeApprovalTimeoutSeconds is < 30 or > 1800)
            return "Tailscale.ServeApprovalTimeoutSeconds must be between 30 and 1800 seconds.";
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
            var authorizationUri = root.TryGetProperty("AuthURL", out var authUrl) &&
                                   authUrl.ValueKind == JsonValueKind.String
                ? TryReadAuthorizationUrl(authUrl.GetString() ?? string.Empty)
                : null;
            var hasExpiredAuthorizationPath = root.TryGetProperty("Health", out var health) &&
                                              health.ValueKind == JsonValueKind.Array &&
                                              health.EnumerateArray().Any(message =>
                                                  message.ValueKind == JsonValueKind.String &&
                                                  message.GetString()?.Contains("auth path not found", StringComparison.OrdinalIgnoreCase) == true);
            status = new TailscaleStatus(backendState, dnsName?.Trim().TrimEnd('.'), hasExpiredAuthorizationPath, authorizationUri);
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
    /// Parses Serve status as structured data. A matching TCP listener or an
    /// unrelated Web handler is not enough: only a Web proxy to the gateway's
    /// loopback HTTP port proves that Companion will reach OpenClaw.
    /// </summary>
    public static bool TryParseServeStatus(string status, int port, out TailscaleServeStatus parsed)
    {
        parsed = new TailscaleServeStatus(false, false);
        if (port is <= 0 or > 65535)
            return false;

        try
        {
            using var document = JsonDocument.Parse(status);
            var root = document.RootElement;
            parsed = new TailscaleServeStatus(
                RoutesToGateway: HasGatewayWebProxy(root, port),
                FunnelEnabled: HasEnabledFunnel(root));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool ServeStatusRoutesToPort(string status, int port) =>
        TryParseServeStatus(status, port, out var parsed) && parsed.RoutesToGateway;

    public static bool ServeStatusEnablesFunnel(string status, int port) =>
        TryParseServeStatus(status, port, out var parsed) && parsed.FunnelEnabled;

    private static bool HasGatewayWebProxy(JsonElement root, int port)
    {
        if (!root.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
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

        return false;
    }

    // Serve status represents Funnel as AllowFunnel on current Tailscale
    // versions. Accept the legacy Funnel spelling too so a version change
    // cannot silently turn a public endpoint into an accepted setup state.
    private static bool HasEnabledFunnel(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("AllowFunnel") || property.NameEquals("Funnel"))
                return ContainsEnabledFunnelValue(property.Value);
        }

        return false;
    }

    private static bool ContainsEnabledFunnelValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsEnabledFunnelValue),
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsEnabledFunnelValue(property.Value)),
        // A non-empty string is a configured public endpoint in older status
        // documents. Be conservative: setup must never accept it as private.
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => false
    };

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

public sealed record TailscaleStatus(
    string BackendState,
    string? DnsName,
    bool HasExpiredAuthorizationPath = false,
    Uri? AuthorizationUri = null)
{
    public bool IsRunning => BackendState.Equals("Running", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(DnsName);
}

public sealed record TailscaleServeStatus(bool RoutesToGateway, bool FunnelEnabled);

/// <summary>Injectable time source for authorization and HTTPS approval polling.</summary>
public interface ITailscalePollingClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemTailscalePollingClock : ITailscalePollingClock
{
    public static readonly SystemTailscalePollingClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record TailscaleEndpointProbeResult(bool IsReachable, int? StatusCode, string? Error)
{
    public static TailscaleEndpointProbeResult Reachable(int statusCode) => new(true, statusCode, null);
    public static TailscaleEndpointProbeResult Unreachable(string error) => new(false, null, error);
}

/// <summary>Windows-side HTTPS probe used before pairing is allowed to begin.</summary>
public interface ITailscaleEndpointProbe
{
    Task<TailscaleEndpointProbeResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken);
}

public sealed class TailscaleEndpointProbe : ITailscaleEndpointProbe
{
    public async Task<TailscaleEndpointProbeResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var response = await http.GetAsync(endpoint, cancellationToken);
            var status = (int)response.StatusCode;
            return status is 200 or 401 or 403
                ? TailscaleEndpointProbeResult.Reachable(status)
                : TailscaleEndpointProbeResult.Unreachable($"HTTP {status}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return TailscaleEndpointProbeResult.Unreachable(ex.Message);
        }
    }
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
        /usr/bin/tailscale version
        """;

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var script = "/usr/bin/tailscale funnel reset 2>/dev/null || true; /usr/bin/tailscale serve reset 2>/dev/null || true; /usr/bin/tailscale logout 2>/dev/null || true; systemctl disable --now tailscaled 2>/dev/null || true";
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, script, TimeSpan.FromSeconds(30), ct: ct, user: "root");
    }
}

public sealed class AuthorizeTailscaleStep : SetupStep
{
    private readonly ITailscalePollingClock _clock;

    public AuthorizeTailscaleStep(ITailscalePollingClock? clock = null)
    {
        _clock = clock ?? SystemTailscalePollingClock.Instance;
    }

    public override string Id => "authorize-tailscale";
    public override string DisplayName => "Authorize Tailscale gateway";
    // Browser approval and auth keys are deliberately one-shot. This step owns
    // the bounded reauthorization flow so the pipeline cannot create extra
    // approval prompts or retry after the auth key has been cleared.
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.Tailscale.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var config = ctx.Config.Tailscale;
        if (TailscaleSetupPolicy.ValidateConfig(ctx.Config) is { } configError)
            return StepResult.Terminal(configError);

        var hostname = config.EffectiveHostname;
        // Root owns tailscaled and Serve. Do not make the gateway account a
        // Tailscale operator; tailscaled's LocalAPI applies its own read/write
        // authorization to local callers.
        var upCommand = $"/usr/bin/tailscale up --hostname={ShellEscape(hostname)}";
        var deadline = _clock.UtcNow.AddSeconds(config.AuthTimeoutSeconds);
        try
        {
            TailscaleStatus? status;
            if (config.AuthMode == TailscaleAuthMode.AuthKey)
            {
                var up = await ctx.Commands.RunInWslAsync(
                    ctx.DistroName!,
                    upCommand + " --auth-key=\"$TS_AUTHKEY\"",
                    TimeSpan.FromSeconds(60),
                    new Dictionary<string, string> { ["TS_AUTHKEY"] = config.AuthKey! },
                    ct,
                    user: "root",
                    inputViaStdin: true);
                if (up.ExitCode != 0)
                    return StepResult.Fail($"Tailscale auth-key authorization failed (exit {up.ExitCode}).");

                status = (await WaitForRunningAsync(ctx, deadline, activeAuthorizationUri: null, ct: ct)).Status;
            }
            else
            {
                status = null;
                var authorizationUri = await RequestBrowserAuthorizationAsync(ctx, upCommand, forceReauthentication: false, ct);
                if (authorizationUri is null)
                    return StepResult.Fail("Tailscale did not provide a browser authorization URL.");

                await PresentBrowserAuthorizationAsync(ctx, authorizationUri, ct);
                var refreshedAuthorization = false;
                while (_clock.UtcNow < deadline)
                {
                    var wait = await WaitForRunningAsync(ctx, deadline, authorizationUri, ct);
                    if (wait.Status is not null)
                    {
                        status = wait.Status;
                        break;
                    }

                    if (refreshedAuthorization)
                        break;

                    if (wait.ReplacementAuthorizationUri is { } replacementUri)
                    {
                        ctx.Logger.Warn("Tailscale provided a replacement browser authorization link; presenting it once.");
                        authorizationUri = replacementUri;
                        await PresentBrowserAuthorizationAsync(ctx, authorizationUri, ct);
                        refreshedAuthorization = true;
                        continue;
                    }

                    if (!wait.ExpiredAuthorizationPath)
                        break;

                    ctx.Logger.Warn("Tailscale browser authorization became unavailable; requesting one fresh authorization link.");
                    authorizationUri = await RequestBrowserAuthorizationAsync(ctx, upCommand, forceReauthentication: true, ct);
                    if (authorizationUri is null)
                        return StepResult.Fail("Tailscale did not provide a fresh browser authorization URL.");
                    await PresentBrowserAuthorizationAsync(ctx, authorizationUri, ct);
                    refreshedAuthorization = true;
                }
            }

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

    private async Task<Uri?> RequestBrowserAuthorizationAsync(
        SetupContext ctx,
        string upCommand,
        bool forceReauthentication,
        CancellationToken ct)
    {
        var reauthentication = forceReauthentication ? " --force-reauth" : string.Empty;
        var up = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!, upCommand + reauthentication + " --timeout=5s", TimeSpan.FromSeconds(15), ct: ct, user: "root", inputViaStdin: true);
        var authorizationUrl = TailscaleSetupPolicy.TryReadAuthorizationUrl(up.Stdout + "\n" + up.Stderr);
        if (authorizationUrl is null)
        {
            // Some tailscaled versions retain a valid browser URL only in
            // status JSON after a short timed `up` command. Read that same
            // URL without sending it through setup logging or failure text.
            var status = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!, "/usr/bin/tailscale status --json", TimeSpan.FromSeconds(15), ct: ct, user: "root");
            authorizationUrl = status.ExitCode == 0
                ? TailscaleSetupPolicy.TryReadAuthorizationUrl(status.Stdout)
                : null;
        }
        if (authorizationUrl is null)
            return null;

        return authorizationUrl;
    }

    private static async Task PresentBrowserAuthorizationAsync(
        SetupContext ctx,
        Uri authorizationUrl,
        CancellationToken ct)
    {
        var presenter = ctx.ExternalAuthorizationPresenter ?? new ConsoleExternalAuthorizationPresenter();
        await presenter.PresentAsync(
            new ExternalAuthorizationRequest("Tailscale", authorizationUrl, "Authorize the generated OpenClaw gateway in your Tailscale tailnet:"),
            ct);
    }

    private async Task<AuthorizationWaitResult> WaitForRunningAsync(
        SetupContext ctx,
        DateTimeOffset deadline,
        Uri? activeAuthorizationUri,
        CancellationToken ct)
    {
        while (_clock.UtcNow < deadline)
        {
            var result = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!, "/usr/bin/tailscale status --json", TimeSpan.FromSeconds(15), ct: ct, user: "root");
            if (result.ExitCode == 0 && TailscaleSetupPolicy.TryParseStatus(result.Stdout, out var status))
            {
                if (status.IsRunning)
                    return new AuthorizationWaitResult(status, false, null);
                if (activeAuthorizationUri is not null &&
                    status.AuthorizationUri is { } replacementAuthorizationUri &&
                    Uri.Compare(activeAuthorizationUri, replacementAuthorizationUri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    return new AuthorizationWaitResult(null, false, replacementAuthorizationUri);
                }
                if (status.HasExpiredAuthorizationPath)
                    return new AuthorizationWaitResult(null, true, null);
            }

            await _clock.DelayAsync(TimeSpan.FromSeconds(2), ct);
        }

        return new AuthorizationWaitResult(null, false, null);
    }

    private sealed record AuthorizationWaitResult(
        TailscaleStatus? Status,
        bool ExpiredAuthorizationPath,
        Uri? ReplacementAuthorizationUri);

    private static string ShellEscape(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

public sealed class FinalizeTailscaleServeStep : SetupStep
{
    private readonly ITailscalePollingClock _clock;
    private readonly ITailscaleEndpointProbe _endpointProbe;

    public FinalizeTailscaleServeStep(
        ITailscalePollingClock? clock = null,
        ITailscaleEndpointProbe? endpointProbe = null)
    {
        _clock = clock ?? SystemTailscalePollingClock.Instance;
        _endpointProbe = endpointProbe ?? new TailscaleEndpointProbe();
    }

    public override string Id => "finalize-tailscale-serve";
    public override string DisplayName => "Publish gateway on Tailscale";

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.Tailscale.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.TailscaleDnsName))
            return StepResult.Fail("Tailscale authorization did not produce a MagicDNS hostname.");

        var serveResult = await WaitForServeRouteAsync(ctx, ct);
        if (serveResult is not null)
            return serveResult;

        var endpoint = new UriBuilder(Uri.UriSchemeWss, ctx.TailscaleDnsName).Uri.AbsoluteUri.TrimEnd('/');
        var devicePairPublicUrl = new UriBuilder(Uri.UriSchemeHttps, ctx.TailscaleDnsName).Uri.AbsoluteUri.TrimEnd('/');
        var configurePairUrl = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            $"{ctx.WslPathPrefix} && openclaw config set {ConfigureGatewayStep.DevicePairPublicUrlKey} {ShellEscape(devicePairPublicUrl)} && openclaw config set {ConfigureGatewayStep.DevicePairEnabledKey} true",
            TimeSpan.FromSeconds(45),
            ct: ct);
        if (configurePairUrl.ExitCode != 0)
            return StepResult.Fail($"Could not configure the Tailscale device-pair URL: {configurePairUrl.Stderr}");

        var probe = await _endpointProbe.ProbeAsync(new Uri(devicePairPublicUrl), ct);
        if (!probe.IsReachable)
            return StepResult.Fail($"Windows could not reach the Tailscale gateway endpoint: {probe.Error ?? "unknown error"}");
        ctx.Logger.Info($"Tailscale Serve health check: HTTP {probe.StatusCode}");

        ctx.GatewayUrl = endpoint;
        ctx.Logger.Info($"Companion will pair through {endpoint}");
        return StepResult.Ok("Tailscale Serve endpoint verified");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        const string reset = "/usr/bin/tailscale funnel reset 2>/dev/null || true; /usr/bin/tailscale serve reset 2>/dev/null || true";
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, reset, TimeSpan.FromSeconds(20), ct: ct, user: "root");
    }

    private async Task<StepResult?> WaitForServeRouteAsync(SetupContext ctx, CancellationToken ct)
    {
        var deadline = _clock.UtcNow.AddSeconds(ctx.Config.Tailscale.ServeApprovalTimeoutSeconds);
        var approvalPresented = false;
        while (_clock.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var status = await GetServeStatusAsync(ctx, ct);
            if (status.ExitCode == 0 &&
                TailscaleSetupPolicy.TryParseServeStatus(status.Stdout, ctx.Config.GatewayPort, out var parsed))
            {
                if (parsed.FunnelEnabled)
                    return StepResult.Fail("Tailscale Funnel is configured for this generated gateway. Funnel is not supported; remove it and retry.");
                if (parsed.RoutesToGateway)
                    return null;
            }

            // Root is the durable owner of the Serve configuration. The OpenClaw
            // account never receives the tailscaled operator capability.
            var enable = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!,
                $"/usr/bin/tailscale serve --bg --yes {ctx.Config.GatewayPort}",
                TimeSpan.FromSeconds(30),
                ct: ct,
                user: "root");
            var authorizationUrl = TailscaleSetupPolicy.TryReadAuthorizationUrl(enable.Stdout + "\n" + enable.Stderr);
            if (authorizationUrl is not null && !approvalPresented)
            {
                approvalPresented = true;
                var presenter = ctx.ExternalAuthorizationPresenter ?? new ConsoleExternalAuthorizationPresenter();
                await presenter.PresentAsync(
                    new ExternalAuthorizationRequest("Tailscale", authorizationUrl, "Enable HTTPS for your Tailscale Serve endpoint:"),
                    ct);
            }
            else if (enable.ExitCode != 0 && authorizationUrl is null)
            {
                // Do not include command output: it can contain a short-lived
                // login URL and must not reach journals, diagnostics, or UI.
                return StepResult.Fail("Tailscale Serve could not publish the gateway.");
            }

            await _clock.DelayAsync(TimeSpan.FromSeconds(2), ct);
        }

        return StepResult.Fail(
            $"Tailscale Serve did not route to the generated gateway within {ctx.Config.Tailscale.ServeApprovalTimeoutSeconds} seconds.");
    }

    private static Task<CommandResult> GetServeStatusAsync(SetupContext ctx, CancellationToken ct) =>
        ctx.Commands.RunInWslAsync(
            ctx.DistroName!, "/usr/bin/tailscale serve status --json", TimeSpan.FromSeconds(20), ct: ct, user: "root");

    private static string ShellEscape(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
