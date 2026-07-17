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
            var hasExpiredAuthorizationPath = root.TryGetProperty("Health", out var health) &&
                                              health.ValueKind == JsonValueKind.Array &&
                                              health.EnumerateArray().Any(message =>
                                                  message.ValueKind == JsonValueKind.String &&
                                                  message.GetString()?.Contains("auth path not found", StringComparison.OrdinalIgnoreCase) == true);
            status = new TailscaleStatus(backendState, dnsName?.Trim().TrimEnd('.'), hasExpiredAuthorizationPath);
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

public sealed record TailscaleStatus(string BackendState, string? DnsName, bool HasExpiredAuthorizationPath = false)
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
        var script = "chmod 0755 /usr/bin/tailscale 2>/dev/null || true; rm -f /etc/sudoers.d/openclaw-tailscale-whois /usr/local/libexec/openclaw-tailscale-whois /opt/openclaw/bin/tailscale /etc/systemd/user/openclaw-gateway.service.d/20-openclaw-tailscale-whois.conf; rmdir /opt/openclaw/bin /opt/openclaw /etc/systemd/user/openclaw-gateway.service.d 2>/dev/null || true; /usr/bin/tailscale funnel reset 2>/dev/null || true; /usr/bin/tailscale serve reset 2>/dev/null || true; /usr/bin/tailscale logout 2>/dev/null || true; systemctl disable --now tailscaled 2>/dev/null || true";
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

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.Tailscale.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var config = ctx.Config.Tailscale;
        if (TailscaleSetupPolicy.ValidateConfig(ctx.Config) is { } configError)
            return StepResult.Terminal(configError);

        var hostname = config.EffectiveHostname;
        // Root owns tailscaled and Serve. Do not delegate the Tailscale operator
        // socket to the gateway account; optional identity trust receives a
        // narrowly constrained whois helper later in the pipeline.
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

                status = (await WaitForRunningAsync(ctx, deadline, ct)).Status;
            }
            else
            {
                status = null;
                for (var attempt = 0; attempt < 2 && _clock.UtcNow < deadline; attempt++)
                {
                    if (!await PresentBrowserAuthorizationAsync(ctx, upCommand, forceReauthentication: attempt > 0, ct: ct))
                        return StepResult.Fail("Tailscale did not provide a browser authorization URL.");

                    var wait = await WaitForRunningAsync(ctx, deadline, ct);
                    if (wait.Status is not null)
                    {
                        status = wait.Status;
                        break;
                    }

                    if (!wait.ExpiredAuthorizationPath)
                        break;

                    if (attempt == 0)
                        ctx.Logger.Warn("Tailscale browser authorization became unavailable; requesting one fresh authorization link.");
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

    private async Task<bool> PresentBrowserAuthorizationAsync(
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
            return false;

        var presenter = ctx.ExternalAuthorizationPresenter ?? new ConsoleExternalAuthorizationPresenter();
        await presenter.PresentAsync(
            new ExternalAuthorizationRequest("Tailscale", authorizationUrl, "Authorize the generated OpenClaw gateway in your Tailscale tailnet:"),
            ct);
        return true;
    }

    private async Task<AuthorizationWaitResult> WaitForRunningAsync(SetupContext ctx, DateTimeOffset deadline, CancellationToken ct)
    {
        while (_clock.UtcNow < deadline)
        {
            var result = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!, "/usr/bin/tailscale status --json", TimeSpan.FromSeconds(15), ct: ct, user: "root");
            if (result.ExitCode == 0 && TailscaleSetupPolicy.TryParseStatus(result.Stdout, out var status))
            {
                if (status.IsRunning)
                    return new AuthorizationWaitResult(status, false);
                if (status.HasExpiredAuthorizationPath)
                    return new AuthorizationWaitResult(null, true);
            }

            await _clock.DelayAsync(TimeSpan.FromSeconds(2), ct);
        }

        return new AuthorizationWaitResult(null, false);
    }

    private sealed record AuthorizationWaitResult(TailscaleStatus? Status, bool ExpiredAuthorizationPath);

    private static string ShellEscape(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

/// <summary>
/// Grants the gateway service only the capability OpenClaw needs when tailnet
/// identity authentication is explicitly enabled. The service cannot use the
/// tailscaled operator socket or administer Serve, Funnel, routes, or logout.
/// </summary>
public sealed class ConfigureTailscaleWhoisAccessStep : SetupStep
{
    private const string ShimPath = "/opt/openclaw/bin/tailscale";
    private const string HelperPath = "/usr/local/libexec/openclaw-tailscale-whois";
    private const string SudoersPath = "/etc/sudoers.d/openclaw-tailscale-whois";
    private const string SystemdDropInDirectory = "/etc/systemd/user/openclaw-gateway.service.d";
    private const string SystemdDropInPath = SystemdDropInDirectory + "/20-openclaw-tailscale-whois.conf";

    public override string Id => "configure-tailscale-whois-access";
    public override string DisplayName => "Restrict Tailscale identity lookup";

    public override bool CanSkip(SetupContext ctx) =>
        !ctx.Config.Tailscale.Enabled || !ctx.Config.Tailscale.TrustTailscaleAuth;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;
        if (!IsSafeLinuxUser(user))
            return StepResult.Terminal("The generated gateway user is not a safe Linux account name for Tailscale identity trust.");

        // The generated gateway unit owns its normal runtime PATH. Add a
        // root-owned system-wide drop-in that prepends the constrained shim
        // before /usr/bin; do not assume a particular OpenClaw/node version.
        var serviceEnvironment = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            "systemctl --user show -p Environment --value openclaw-gateway.service",
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user);
        if (serviceEnvironment.ExitCode != 0 || !TryReadServicePath(serviceEnvironment.Stdout, out var servicePath))
        {
            return StepResult.Terminal(
                "Could not read the generated OpenClaw service PATH needed for the restricted Tailscale identity helper. " +
                "Turn off Tailscale identity trust or update OpenClaw before continuing.");
        }
        if (servicePath.StartsWith("/opt/openclaw/bin:", StringComparison.Ordinal))
            servicePath = servicePath["/opt/openclaw/bin:".Length..];
        if (string.IsNullOrWhiteSpace(servicePath))
            return StepResult.Terminal("The generated OpenClaw service PATH cannot be safely extended for Tailscale identity trust.");

        var install = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            BuildInstallScript(user, servicePath),
            TimeSpan.FromSeconds(30),
            ct: ct,
            user: "root",
            inputViaStdin: true);
        if (install.ExitCode != 0)
            return StepResult.Fail("Could not install the restricted Tailscale identity helper.");

        var reload = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            "systemctl --user daemon-reload",
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user);
        if (reload.ExitCode != 0)
            return StepResult.Fail("Could not reload the generated OpenClaw service after installing Tailscale identity trust.");

        var updatedEnvironment = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            "systemctl --user show -p Environment --value openclaw-gateway.service",
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user);
        if (updatedEnvironment.ExitCode != 0 ||
            !TryReadServicePath(updatedEnvironment.Stdout, out var updatedPath) ||
            !updatedPath.StartsWith("/opt/openclaw/bin:", StringComparison.Ordinal))
        {
            return StepResult.Fail("The generated OpenClaw service did not load the restricted Tailscale identity helper path.");
        }

        return StepResult.Ok("Tailscale identity lookup restricted to whois");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        const string remove =
            "chmod 0755 /usr/bin/tailscale 2>/dev/null || true; rm -f /etc/sudoers.d/openclaw-tailscale-whois /usr/local/libexec/openclaw-tailscale-whois /opt/openclaw/bin/tailscale " +
            SystemdDropInPath + "; rmdir /opt/openclaw/bin /opt/openclaw " + SystemdDropInDirectory + " 2>/dev/null || true";
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, remove, TimeSpan.FromSeconds(15), ct: ct, user: "root");
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, "systemctl --user daemon-reload", TimeSpan.FromSeconds(15), ct: ct, user: ctx.Config.Wsl.User);
    }

    internal static string BuildInstallScript(string user, string servicePath) => $"""
        set -eu
        test -x /usr/bin/python3
        chown root:root /usr/bin/tailscale
        chmod 0750 /usr/bin/tailscale
        install -d -m 0755 /usr/local/libexec /opt/openclaw/bin {SystemdDropInDirectory}
        cat > {SystemdDropInPath} <<'SYSTEMD'
        [Service]
        Environment="PATH=/opt/openclaw/bin:{servicePath}"
        SYSTEMD
        chmod 0644 {SystemdDropInPath}
        chown root:root {SystemdDropInPath}
        cat > {HelperPath} <<'PY'
        #!/usr/bin/python3 -I
        import ipaddress
        import os
        import sys

        if len(sys.argv) != 2:
            sys.exit(64)
        try:
            address = str(ipaddress.ip_address(sys.argv[1]))
        except ValueError:
            sys.exit(64)
        os.execv("/usr/bin/tailscale", ["/usr/bin/tailscale", "whois", "--json", address])
        PY
        chmod 0755 {HelperPath}
        chown root:root {HelperPath}
        cat > {ShimPath} <<'SH'
        #!/bin/sh
        set -eu
        if [ "$#" -eq 3 ] && [ "$1" = "whois" ] && [ "$2" = "--json" ]; then
            exec /usr/bin/sudo -n {HelperPath} "$3"
        fi
        echo "OpenClaw may use Tailscale only for whois --json <IP>." >&2
        exit 126
        SH
        chmod 0755 {ShimPath}
        chown root:root {ShimPath}
        cat > {SudoersPath} <<'SUDOERS'
        {user} ALL=(root) NOPASSWD: {HelperPath} *
        SUDOERS
        chmod 0440 {SudoersPath}
        chown root:root {SudoersPath}
        if ! visudo -cf {SudoersPath}; then
            rm -f {SudoersPath} {HelperPath} {ShimPath} {SystemdDropInPath}
            exit 1
        fi
        """;

    private static bool TryReadServicePath(string environment, out string servicePath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(environment, @"(?:^|\s)PATH=([^\s]+)");
        if (match.Success &&
            System.Text.RegularExpressions.Regex.IsMatch(match.Groups[1].Value, @"^[A-Za-z0-9_./:+-]+$"))
        {
            servicePath = match.Groups[1].Value;
            return true;
        }

        servicePath = string.Empty;
        return false;
    }

    private static bool IsSafeLinuxUser(string user) =>
        System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z_][a-z0-9_-]*$");
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
