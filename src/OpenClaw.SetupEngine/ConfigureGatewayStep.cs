using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;


public sealed class ConfigureGatewayStep : SetupStep
{
    internal const string DevicePairPublicUrlKey = "plugins.entries.device-pair.config.publicUrl";
    internal const string DevicePairEnabledKey = "plugins.entries.device-pair.enabled";
    // Each `openclaw config set` emitted below spawns the Node CLI fresh inside WSL; on a
    // newly created distro with a cold cache that is ~4-5s apiece. Budget the step by how
    // many config commands we actually emit -- BuildConfigCommands grows with the
    // device-pair keys and every Gateway.ExtraConfig entry -- with a floor so the minimal
    // path keeps generous headroom. A fixed cap silently regresses as the list grows.
    internal static readonly TimeSpan ConfigBaseBudget = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan PerConfigCommandBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MinConfigurationTimeout = TimeSpan.FromSeconds(180);

    public override string Id => "configure-gateway";
    public override string DisplayName => "Configure gateway";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var port = ctx.Config.GatewayPort;
        var gw = ctx.Config.Gateway;

        // Validate bind value — Tailscale Serve deliberately keeps the gateway loopback-bound.
        if (gw.Bind is not ("loopback" or "lan"))
            return StepResult.Terminal($"Invalid Gateway.Bind value '{gw.Bind}'. Must be 'loopback' or 'lan'.");
        if (TailscaleSetupPolicy.ValidateConfig(ctx.Config) is { } tailscaleConfigError)
            return StepResult.Terminal(tailscaleConfigError);

        // Generate a shared gateway token
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        ctx.SharedGatewayToken = token;
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };

        var allowedCommandsJson = JsonSerializer.Serialize(ctx.Config.Capabilities.GetEnabledCommandIds());
        var escapedAllowedCommands = WslShellQuoting.QuotePosixSingleQuote(allowedCommandsJson);
        var extraConfigOverridesAllowCommands = gw.ExtraConfig?.ContainsKey("gateway.nodes.allowCommands") == true;
        if (gw.ExtraConfig is { Count: > 0 })
        {
            foreach (var key in gw.ExtraConfig.Keys)
            {
                if (!IsSafeExtraConfigKey(key))
                    return StepResult.Fail($"Invalid Gateway.ExtraConfig key '{key}'. Keys may contain only letters, digits, '.', '_', and '-'.");
            }
        }

        var configCommands = BuildConfigCommands(gw, port, escapedAllowedCommands, ctx.Config.Tailscale);

        ctx.Logger.Info($"Gateway node allowCommands derived from setup capabilities: {allowedCommandsJson}");
        if (extraConfigOverridesAllowCommands)
            ctx.Logger.Warn("Gateway.ExtraConfig overrides derived gateway.nodes.allowCommands");
        if (GetDefaultDevicePairPublicUrl(gw, port, ctx.Config.Tailscale.Enabled) is { } defaultPublicUrl &&
            gw.ExtraConfig?.ContainsKey(DevicePairPublicUrlKey) != true)
        {
            ctx.Logger.Info($"Configured device-pair public URL for loopback gateway: {defaultPublicUrl}");
        }

        var pathPrefix = ctx.WslPathPrefix;
        var script = $"""
            set -e
            {pathPrefix}

            {configCommands}

            echo "GATEWAY_CONFIGURED"
            """;

        var timeout = ComputeConfigurationTimeout(configCommands);
        var result = await ctx.Commands.RunInWslAsync(distro, script, timeout, env, ct);

        if (result.ExitCode != 0 || !result.Stdout.Contains("GATEWAY_CONFIGURED"))
        {
            if (result.TimedOut)
                return StepResult.Fail(
                    $"Gateway configuration timed out after {timeout.TotalSeconds:0}s while running openclaw config inside WSL.");

            return StepResult.Fail($"Gateway configuration failed (exit {result.ExitCode}): {result.Stderr}");
        }

        ctx.Logger.StateChange("shared_gateway_token", null, "[SET]");
        return StepResult.Ok("Gateway configured");
    }

    internal static string BuildConfigCommands(
        GatewayConfig gw,
        int port,
        string escapedAllowedCommands,
        TailscaleConfig? tailscale = null)
    {
        var configCommands = $"""
            openclaw config set gateway.mode local
            openclaw config set gateway.port {port}
            openclaw config set gateway.bind {gw.Bind}
            openclaw config set gateway.auth.mode {gw.AuthMode}
            openclaw config set gateway.auth.token "$OPENCLAW_GATEWAY_TOKEN"
            openclaw config set gateway.reload.mode {gw.ReloadMode}
            openclaw config set gateway.nodes.allowCommands {escapedAllowedCommands}
            """;

        if (tailscale?.Enabled == true)
        {
            var trustTailscaleAuth = tailscale.TrustTailscaleAuth ? "true" : "false";
            configCommands += $"""

                openclaw config set gateway.tailscale.mode off
                openclaw config set gateway.auth.allowTailscale {trustTailscaleAuth}
                """;
        }

        if (GetDefaultDevicePairPublicUrl(gw, port, tailscale?.Enabled == true) is { } defaultPublicUrl &&
            gw.ExtraConfig?.ContainsKey(DevicePairPublicUrlKey) != true)
        {
            configCommands += $"\n            openclaw config set {DevicePairPublicUrlKey} {WslShellQuoting.QuotePosixSingleQuote(defaultPublicUrl)}";
        }

        // The gateway ships the `device-pair` plugin bundled but DISABLED by default.
        // Without it, every scope-upgrade / role-upgrade WS connect (how OAuth providers like
        // Codex request the broader scopes needed to start their auth flow) hangs in
        // "pending approval" forever. The provider CLI errors out before ever printing its
        // verification URL, leaving the wizard stuck. Enable the plugin whenever we know how
        // to reach it (i.e. we either wrote the default loopback URL above, or the user
        // supplied their own publicUrl via ExtraConfig).
        var hasDevicePairPublicUrl =
            GetDefaultDevicePairPublicUrl(gw, port, tailscale?.Enabled == true) is not null ||
            gw.ExtraConfig?.ContainsKey(DevicePairPublicUrlKey) == true;
        var devicePairExplicitlyConfigured =
            gw.ExtraConfig?.ContainsKey(DevicePairEnabledKey) == true;
        if (hasDevicePairPublicUrl && !devicePairExplicitlyConfigured)
        {
            configCommands += $"\n            openclaw config set {DevicePairEnabledKey} true";
        }

        // Apply any extra config key/value pairs from config (shell-escape values)
        if (gw.ExtraConfig is { Count: > 0 })
        {
            foreach (var (key, value) in gw.ExtraConfig)
            {
                if (!IsSafeExtraConfigKey(key))
                    throw new ArgumentException($"Invalid Gateway.ExtraConfig key '{key}'. Keys may contain only letters, digits, '.', '_', and '-'.", nameof(gw));

                var escapedValue = WslShellQuoting.QuotePosixSingleQuote(value);
                configCommands += $"\n            openclaw config set {key} {escapedValue}";
            }
        }

        return configCommands;
    }

    // Budget = base + per-command, floored. Scales the WSL timeout with the number of
    // `openclaw config set` invocations the step emits so it cannot silently regress as
    // BuildConfigCommands grows.
    internal static TimeSpan ComputeConfigurationTimeout(string configCommands)
    {
        var budget = ConfigBaseBudget + PerConfigCommandBudget * CountConfigSetCommands(configCommands);
        return budget > MinConfigurationTimeout ? budget : MinConfigurationTimeout;
    }

    private static int CountConfigSetCommands(string configCommands)
    {
        var count = 0;
        foreach (var line in configCommands.Split('\n'))
        {
            if (line.Contains("openclaw config set", StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    internal static string? GetDefaultDevicePairPublicUrl(GatewayConfig gw, int port, bool tailscaleEnabled = false) =>
        gw.Bind == "loopback" && !tailscaleEnabled ? $"http://127.0.0.1:{port}" : null;

    internal static bool IsSafeExtraConfigKey(string value)
        => System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9._-]+$");
}
