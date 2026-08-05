using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class ConfigureWslInstanceStep : SetupStep
{
    public override string Id => "wsl-configure";
    public override string DisplayName => "Configure WSL instance";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var wsl = ctx.Config.Wsl;

        if (!WslConfig.IsValidLinuxUserName(wsl.User))
            return StepResult.Terminal($"Invalid WSL user '{wsl.User}'. Use a Linux username matching [a-z_][a-z0-9_-]{{0,31}}.");

        // Build wsl.conf from config
        var wslConf = $"""
[boot]
systemd={wsl.Systemd.ToString().ToLower()}

[automount]
enabled={wsl.Automount.ToString().ToLower()}
mountFsTab={wsl.MountFsTab.ToString().ToLower()}

[interop]
enabled={wsl.Interop.ToString().ToLower()}
appendWindowsPath={wsl.AppendWindowsPath.ToString().ToLower()}

[user]
default={wsl.User}

[time]
useWindowsTimezone={wsl.UseWindowsTimezone.ToString().ToLower()}
""";

        // Create user and directories
        var script = $"""
            set -e

            # Create user if not exists
            if ! id -u {wsl.User} &>/dev/null; then
                useradd -m -s /bin/bash {wsl.User}
            fi

            # Create required directories
            mkdir -p /home/{wsl.User}/.openclaw
            mkdir -p /var/lib/openclaw
            mkdir -p /var/log/openclaw
            mkdir -p /opt/openclaw

            chown -R {wsl.User}:{wsl.User} /home/{wsl.User}/.openclaw
            chown -R {wsl.User}:{wsl.User} /var/lib/openclaw
            chown -R {wsl.User}:{wsl.User} /var/log/openclaw
            chown -R {wsl.User}:{wsl.User} /opt/openclaw

            # Write wsl.conf
            cat > /etc/wsl.conf << 'WSLCONF'
            {wslConf}
            WSLCONF

            echo "CONFIGURED_OK"
            """;

        var result = await ctx.Commands.RunInWslAsync(distro, script, TimeSpan.FromSeconds(60), ct: ct, user: "root");

        if (result.ExitCode != 0 || !result.Stdout.Contains("CONFIGURED_OK"))
            return StepResult.Fail($"Configuration failed: {result.Stderr}");

        // Restart WSL to apply wsl.conf (systemd)
        ctx.Logger.Info("Restarting WSL to apply configuration (systemd)");
        await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let WSL settle

        return StepResult.Ok("WSL instance configured");
    }
}
