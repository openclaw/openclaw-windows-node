using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

// PATH prefix for all openclaw CLI commands in WSL
internal static class WslConstants
{
    public static string GetPathPrefix(string user) =>
        $"""export PATH="/home/{user}/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:$PATH" """;

    // Default (for backward compat with steps that don't have user context yet)
    public const string PathPrefix = """export PATH="/home/openclaw/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:$PATH" """;
}

// Adapter to bridge SetupLogger → IOpenClawLogger for WebSocket clients
internal sealed class SetupOpenClawLogger(SetupLogger logger) : IOpenClawLogger
{
    public void Info(string message) => logger.Info($"[WS] {message}");
    public void Debug(string message) => logger.Debug($"[WS] {message}");
    public void Warn(string message) => logger.Warn($"[WS] {message}");
    public void Error(string message, Exception? ex = null) => logger.Error($"[WS] {message}{(ex != null ? $": {ex}" : "")}");
}

// ═══════════════════════════════════════════════════════════════════
// CLEANUP STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class CleanupStaleDistroStep : SetupStep
{
    public override string Id => "cleanup-distro";
    public override string DisplayName => "Clean up stale WSL distro";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.CleanBeforeRun;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var list = await ctx.Commands.RunAsync("wsl.exe", ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        if (list.ExitCode != 0)
            return StepResult.Ok("WSL not available or no distros — nothing to clean");

        // wsl.exe outputs UTF-16 with potential BOM/null chars — normalize aggressively
        var distros = list.Stdout
            .Replace("\0", "")
            .Replace("\uFEFF", "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => d.Length > 0)
            .ToList();

        ctx.Logger.Debug($"Found WSL distros: [{string.Join(", ", distros)}]");

        if (!distros.Any(d => d.Equals(distro, StringComparison.OrdinalIgnoreCase)))
        {
            // Distro not registered, but disk directory may still exist from prior crash
            var wslDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "wsl", distro);
            if (Directory.Exists(wslDir))
            {
                ctx.Logger.Info($"Removing orphaned WSL directory: {wslDir}");
                // Shut down WSL VM to release VHD locks
                await ctx.Commands.RunAsync("wsl.exe", ["--shutdown"], TimeSpan.FromSeconds(30), ct: ct);
                await Task.Delay(2000, ct);

                // Retry deletion — VHD may still be locked briefly after WSL shutdown
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        Directory.Delete(wslDir, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        ctx.Logger.Warn($"VHD directory still locked, retrying in {(attempt + 1) * 2}s...");
                        await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2), ct);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 2)
                    {
                        ctx.Logger.Warn($"VHD directory access denied, retrying in {(attempt + 1) * 2}s...");
                        await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2), ct);
                    }
                }
            }
            ctx.Logger.Decision("No stale distro found", "skip cleanup");
            return StepResult.Ok("No stale distro to clean");
        }

        ctx.Logger.Decision($"Found existing distro '{distro}'", "terminating and unregistering");

        // Terminate first (stops gateway service), then shut WSL down to release VHD/port locks.
        await ctx.Commands.RunAsync("wsl.exe", ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await ctx.Commands.RunAsync("wsl.exe", ["--shutdown"], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let port release

        var unregister = await ctx.Commands.RunAsync("wsl.exe", ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        if (unregister.ExitCode != 0)
        {
            ctx.Logger.Warn($"First unregister attempt failed (exit {unregister.ExitCode}); forcing WSL shutdown and retrying");
            await ctx.Commands.RunAsync("wsl.exe", ["--shutdown"], TimeSpan.FromSeconds(30), ct: ct);
            await Task.Delay(3000, ct);
            unregister = await ctx.Commands.RunAsync("wsl.exe", ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        }

        if (unregister.ExitCode == 0)
        {
            // Also remove the on-disk WSL vhdx directory (--import fails if it exists)
            var wslDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "wsl", distro);
            if (Directory.Exists(wslDir))
            {
                ctx.Logger.Info($"Removing leftover WSL directory: {wslDir}");
                Directory.Delete(wslDir, recursive: true);
            }

            // Wait for port to be released
            ctx.Logger.Info("Waiting for port release after distro termination...");
            await Task.Delay(3000, ct);
            return StepResult.Ok($"Unregistered stale distro '{distro}'");
        }

        return StepResult.Fail($"Failed to unregister distro: {unregister.Stderr}");
    }
}

public sealed class CleanupStaleGatewayStep : SetupStep
{
    public override string Id => "cleanup-gateway";
    public override string DisplayName => "Clean up stale gateway state";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.CleanBeforeRun;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        // Remove stale setup-state.json from AppData (legacy location)
        var stateFile = Path.Combine(ctx.DataDir, "setup-state.json");
        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
            ctx.Logger.Info("Deleted stale setup-state.json (AppData)");
        }

        // Also remove from LocalAppData (current write location)
        var localStateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray", "setup-state.json");
        if (File.Exists(localStateFile))
        {
            File.Delete(localStateFile);
            ctx.Logger.Info("Deleted stale setup-state.json (LocalAppData)");
        }

        // Remove stale gateway record for our local URL if it exists
        var registry = new GatewayRegistry(ctx.DataDir);
        registry.Load();
        var existing = registry.FindByUrl(ctx.GatewayUrl!);
        if (existing != null)
        {
            // Clean identity directory
            var identityDir = registry.GetIdentityDirectory(existing.Id);
            if (Directory.Exists(identityDir))
            {
                Directory.Delete(identityDir, recursive: true);
                ctx.Logger.Info($"Deleted stale identity directory: {identityDir}");
            }
            registry.Remove(existing.Id);
            registry.Save();
            ctx.Logger.Info($"Removed stale gateway record for {ctx.GatewayUrl}");
        }

        await Task.CompletedTask;
        return StepResult.Ok("Gateway state cleaned");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        // Delete setup-state.json (written by VerifyEndToEndStep)
        var localDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");

        var stateFile = Path.Combine(localDataPath, "setup-state.json");
        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
            ctx.Logger.Info("[Uninstall] Deleted setup-state.json");
        }
        else
        {
            ctx.Logger.Info("[Uninstall] setup-state.json already absent");
        }

        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// PREFLIGHT STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class PreflightOsStep : SetupStep
{
    public override string Id => "preflight-os";
    public override string DisplayName => "Verify Windows OS";
    public override bool CanRetry => false;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!Environment.Is64BitOperatingSystem)
            return Task.FromResult(StepResult.Terminal("64-bit Windows required"));

        if (!OperatingSystem.IsWindows())
            return Task.FromResult(StepResult.Terminal("Windows OS required"));

        var version = Environment.OSVersion.Version;
        ctx.Logger.Info($"OS: Windows {version} (64-bit)");

        return Task.FromResult(StepResult.Ok($"Windows {version}"));
    }
}

public sealed class PreflightWslStep : SetupStep
{
    public override string Id => "preflight-wsl";
    public override string DisplayName => "Verify WSL available";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var versionResult = await ctx.Commands.RunAsync("wsl.exe", ["--version"], TimeSpan.FromSeconds(5), ct: ct);
        if (versionResult.ExitCode != 0 && LooksUnavailable(versionResult))
        {
            var installResult = await InstallWslPlatformAsync(ctx, ct);
            if (!installResult.IsSuccess)
                return installResult;

            versionResult = await ctx.Commands.RunAsync("wsl.exe", ["--version"], TimeSpan.FromSeconds(5), ct: ct);
        }

        if (versionResult.ExitCode != 0)
            return StepResult.Terminal($"WSL is not available. {FirstUsefulLine(versionResult)}");

        ctx.Logger.Info($"WSL version output: {NormalizeWslOutput(versionResult.Stdout).Trim()}");
        return StepResult.Ok("WSL available");
    }

    private static async Task<StepResult> InstallWslPlatformAsync(SetupContext ctx, CancellationToken ct)
    {
        ctx.Logger.Warn("WSL platform appears to be missing; launching elevated WSL platform install");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--install");
            psi.ArgumentList.Add("--no-distribution");

            using var process = Process.Start(psi);
            if (process == null)
                return StepResult.Fail("Could not start elevated WSL platform installer.");

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 3010)
                return StepResult.Terminal("WSL platform install requires a restart. Reboot Windows, then run setup again.");

            if (process.ExitCode != 0)
                return StepResult.Fail($"WSL platform install failed with exit code {process.ExitCode}.");

            var probe = await ctx.Commands.RunAsync("wsl.exe", ["--version"], TimeSpan.FromSeconds(5), ct: ct);
            if (probe.ExitCode != 0 || LooksUnavailable(probe))
                return StepResult.Terminal("WSL platform install completed, but Windows still reports WSL unavailable. Reboot Windows, then run setup again.");

            return StepResult.Ok("WSL platform installed");
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            return StepResult.Fail("WSL platform install was cancelled at the elevation prompt.");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"WSL platform install failed: {ex.Message}", ex);
        }
    }

    private static bool LooksUnavailable(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("aka.ms/wslinstall", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Windows Subsystem for Linux has no installed distributions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWslOutput(string value)
        => value.Replace("\0", "").Replace("\uFEFF", "");

    private static string FirstUsefulLine(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stderr}\n{result.Stdout}");
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            ?? "Run wsl --install from an elevated terminal and retry setup.";
    }
}

public sealed class PreflightPortStep : SetupStep
{
    public override string Id => "preflight-port";
    public override string DisplayName => "Check gateway port available";
    public override bool CanRetry => false;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var port = ctx.Config.GatewayPort;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return Task.FromResult(StepResult.Ok($"Port {port} is available"));
        }
        catch (SocketException)
        {
            return Task.FromResult(StepResult.Fail($"Port {port} is already in use"));
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// WSL STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class CreateWslInstanceStep : SetupStep
{
    public override string Id => "wsl-create";
    public override string DisplayName => "Create WSL instance";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(5));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var baseDistro = ctx.Config.BaseDistro;

        ctx.Logger.Info($"Creating WSL distro '{distro}' from base '{baseDistro}'");

        // Import as our named distro
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-setup-{ctx.Logger.RunId}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Export base → import as our distro name
            var exportPath = Path.Combine(tempDir, "base.tar");
            var export = await ctx.Commands.RunAsync(
                "wsl.exe", ["--export", baseDistro, exportPath],
                TimeSpan.FromMinutes(5), ct: ct);

            if (export.ExitCode != 0)
            {
                ctx.Logger.Warn($"Base distro export failed (exit {export.ExitCode}); attempting to install {baseDistro}");
                var install = await ctx.Commands.RunAsync(
                    "wsl.exe", ["--install", baseDistro, "--no-launch"],
                    TimeSpan.FromMinutes(5), ct: ct);

                if (install.ExitCode != 0 && !install.Stdout.Contains("already installed", StringComparison.OrdinalIgnoreCase))
                    return StepResult.Fail($"Failed to install base distro '{baseDistro}' (exit {install.ExitCode}): {install.Stderr}");

                export = await ctx.Commands.RunAsync(
                    "wsl.exe", ["--export", baseDistro, exportPath],
                    TimeSpan.FromMinutes(5), ct: ct);
            }

            if (export.ExitCode != 0)
                return StepResult.Fail($"Failed to export base distro: {export.Stderr}");

            var installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray", "wsl", distro);
            Directory.CreateDirectory(installPath);

            var import = await ctx.Commands.RunAsync(
                "wsl.exe", ["--import", distro, installPath, exportPath, "--version", "2"],
                TimeSpan.FromMinutes(5), ct: ct);

            if (import.ExitCode != 0)
                return StepResult.Fail($"Failed to import distro: {import.Stderr}");

            return StepResult.Ok($"Created WSL2 distro '{distro}'");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        await ctx.Commands.RunAsync("wsl.exe", ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await ctx.Commands.RunAsync("wsl.exe", ["--shutdown"], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let port/VHD locks release
        await ctx.Commands.RunAsync("wsl.exe", ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);

        // VHD parent dir cleanup (mirrors old uninstall step 5a)
        var localDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
        var vhdDir = Path.Combine(localDataPath, "wsl", distro);
        if (Directory.Exists(vhdDir))
        {
            Directory.Delete(vhdDir, recursive: true);
            ctx.Logger.Info($"[Uninstall] Deleted VHD parent directory: {vhdDir}");
        }

        // WSL parent dir cleanup — remove empty wsl\ directory (mirrors old step 5b)
        var wslDir = Path.Combine(localDataPath, "wsl");
        if (Directory.Exists(wslDir) && !Directory.EnumerateFileSystemEntries(wslDir).Any())
        {
            Directory.Delete(wslDir);
            ctx.Logger.Info("[Uninstall] Deleted empty wsl\\ parent directory");
        }
    }
}

public sealed class ConfigureWslInstanceStep : SetupStep
{
    public override string Id => "wsl-configure";
    public override string DisplayName => "Configure WSL instance";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var wsl = ctx.Config.Wsl;

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

        var result = await ctx.Commands.RunInWslAsync(distro, script, TimeSpan.FromSeconds(60), ct: ct);

        if (result.ExitCode != 0 || !result.Stdout.Contains("CONFIGURED_OK"))
            return StepResult.Fail($"Configuration failed: {result.Stderr}");

        // Restart WSL to apply wsl.conf (systemd)
        ctx.Logger.Info("Restarting WSL to apply configuration (systemd)");
        await ctx.Commands.RunAsync("wsl.exe", ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let WSL settle

        return StepResult.Ok("WSL instance configured");
    }
}

// ═══════════════════════════════════════════════════════════════════
// GATEWAY INSTALL STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class InstallCliStep : SetupStep
{
    public override string Id => "install-cli";
    public override string DisplayName => "Install OpenClaw CLI";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(5));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var user = ctx.Config.Wsl.User;

        // Download and run install script (URL configurable)
        var installUrl = ctx.Config.Gateway.InstallUrl ?? "https://openclaw.ai/install-cli.sh";
        var installScript = $"curl -fsSL {installUrl} | bash";
        var result = await ctx.Commands.RunInWslAsync(distro, installScript, TimeSpan.FromMinutes(5), ct: ct);

        if (result.ExitCode != 0)
            return StepResult.Fail($"CLI install failed (exit {result.ExitCode}): {result.Stderr}");

        // Verify CLI is accessible — try common install locations
        var verifyPaths = new[]
        {
            "openclaw --version",
            $"/home/{user}/.openclaw/bin/openclaw --version",
            "/opt/openclaw/bin/openclaw --version",
            "/usr/local/bin/openclaw --version"
        };

        foreach (var cmd in verifyPaths)
        {
            var verify = await ctx.Commands.RunInWslAsync(distro, cmd, TimeSpan.FromSeconds(15), ct: ct);
            if (verify.ExitCode == 0 && !string.IsNullOrWhiteSpace(verify.Stdout))
            {
                ctx.Logger.Info($"OpenClaw CLI version: {verify.Stdout.Trim()}");
                return StepResult.Ok($"CLI installed: {verify.Stdout.Trim()}");
            }
        }

        return StepResult.Fail("CLI installed but not found in any known location");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, $"rm -rf /opt/openclaw /home/{user}/.openclaw", TimeSpan.FromSeconds(30), ct: ct);
    }
}

public sealed class ConfigureGatewayStep : SetupStep
{
    public override string Id => "configure-gateway";
    public override string DisplayName => "Configure gateway";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var port = ctx.Config.GatewayPort;
        var gw = ctx.Config.Gateway;

        // Generate a shared gateway token
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        ctx.SharedGatewayToken = token;

        var configCommands = $"""
            openclaw config set gateway.mode local
            openclaw config set gateway.port {port}
            openclaw config set gateway.bind {gw.Bind}
            openclaw config set gateway.auth.mode {gw.AuthMode}
            openclaw config set gateway.auth.token {token}
            openclaw config set gateway.reload.mode {gw.ReloadMode}
            """;

        // Apply any extra config key/value pairs from config (shell-escape values)
        if (gw.ExtraConfig is { Count: > 0 })
        {
            foreach (var (key, value) in gw.ExtraConfig)
            {
                // Shell-escape: wrap value in single quotes, escaping embedded single quotes
                var escapedValue = "'" + value.Replace("'", "'\\''") + "'";
                var escapedKey = key.Replace("'", "").Replace(";", "").Replace("|", "").Replace("&", "");
                configCommands += $"\n            openclaw config set {escapedKey} {escapedValue}";
            }
        }

        var pathPrefix = ctx.WslPathPrefix;
        var script = $"""
            set -e
            {pathPrefix}
            
            {configCommands}
            
            echo "GATEWAY_CONFIGURED"
            """;

        var result = await ctx.Commands.RunInWslAsync(distro, script, TimeSpan.FromSeconds(30), ct: ct);

        if (result.ExitCode != 0 || !result.Stdout.Contains("GATEWAY_CONFIGURED"))
            return StepResult.Fail($"Gateway configuration failed (exit {result.ExitCode}): {result.Stderr}");

        ctx.Logger.StateChange("shared_gateway_token", null, "[SET]");
        return StepResult.Ok("Gateway configured");
    }
}

public sealed class InstallGatewayServiceStep : SetupStep
{
    public override string Id => "install-service";
    public override string DisplayName => "Install gateway service";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        var result = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw gateway install --force", TimeSpan.FromSeconds(60), ct: ct);

        if (result.ExitCode != 0)
            return StepResult.Fail($"Service install failed (exit {result.ExitCode}): {result.Stderr}");

        return StepResult.Ok("Gateway service installed");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, $"{ctx.WslPathPrefix} && openclaw gateway uninstall", TimeSpan.FromSeconds(30), ct: ct);
    }
}

public sealed class StartGatewayStep : SetupStep
{
    public override string Id => "start-gateway";
    public override string DisplayName => "Start gateway";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var pathCmd = ctx.WslPathPrefix;

        // Start the service
        var start = await ctx.Commands.RunInWslAsync(
            distro, $"{pathCmd} && openclaw gateway start", TimeSpan.FromSeconds(30), ct: ct);

        if (start.ExitCode != 0)
        {
            // Check if systemd start-limit-hit
            if (start.Stderr.Contains("start-limit", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.Warn("Start-limit hit, resetting and retrying");
                await ctx.Commands.RunInWslAsync(distro, "systemctl reset-failed openclaw-gateway", TimeSpan.FromSeconds(10), ct: ct);
                await Task.Delay(2000, ct);
                start = await ctx.Commands.RunInWslAsync(distro, $"{pathCmd} && openclaw gateway start", TimeSpan.FromSeconds(30), ct: ct);
                if (start.ExitCode != 0)
                    return StepResult.Fail($"Gateway start failed after reset: {start.Stderr}");
            }
            else
            {
                return StepResult.Fail($"Gateway start failed (exit {start.ExitCode}): {start.Stderr}");
            }
        }

        // Wait for health endpoint
        ctx.Logger.Info("Waiting for gateway health endpoint...");
        var healthDeadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(ctx.Config.Gateway.HealthTimeoutSeconds));

        while (DateTimeOffset.UtcNow < healthDeadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = await ctx.Commands.RunInWslAsync(
                distro, "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:" + ctx.Config.GatewayPort + "/ --max-time 3",
                TimeSpan.FromSeconds(10), ct: ct);

            if (status.ExitCode == 0 && status.Stdout.Trim() is "200" or "401" or "403")
            {
                ctx.Logger.Info($"Gateway is accepting connections (HTTP {status.Stdout.Trim()})");
                return StepResult.Ok("Gateway running");
            }

            ctx.Logger.Debug($"Gateway not yet accepting connections (curl exit={status.ExitCode}, response={status.Stdout.Trim()})");


            await Task.Delay(2000, ct);
        }

        // Capture journal for diagnostics
        var journal = await ctx.Commands.RunInWslAsync(
            distro, "journalctl -u openclaw-gateway --no-pager -n 50", TimeSpan.FromSeconds(10), ct: ct);
        ctx.Logger.Error($"Gateway health timeout. Journal:\n{journal.Stdout}");

        return StepResult.Fail($"Gateway did not become healthy within {ctx.Config.Gateway.HealthTimeoutSeconds}s");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        // Check if distro is running before trying systemctl stop
        var list = await ctx.Commands.RunAsync("wsl.exe", ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        var distros = list.Stdout
            .Replace("\0", "").Replace("\uFEFF", "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim()).Where(d => d.Length > 0).ToList();

        if (!distros.Any(d => d.Equals(distro, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Logger.Info("[Uninstall] Distro not registered — skipping gateway stop");
            return;
        }

        // Check distro state — only stop if Running
        var verbose = await ctx.Commands.RunAsync("wsl.exe", ["--list", "--verbose"], TimeSpan.FromSeconds(15), ct: ct);
        var isRunning = verbose.Stdout
            .Replace("\0", "").Replace("\uFEFF", "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(distro, StringComparison.OrdinalIgnoreCase)
                      && line.Contains("Running", StringComparison.OrdinalIgnoreCase));

        if (!isRunning)
        {
            ctx.Logger.Info("[Uninstall] Distro not running — skipping systemctl stop");
            return;
        }

        // Stop gateway service with 5-second timeout (mirrors old uninstall step 3)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await ctx.Commands.RunInWslAsync(
                distro, "bash -c 'sudo systemctl stop openclaw-gateway 2>&1 || true'",
                TimeSpan.FromSeconds(10), ct: cts.Token);
            ctx.Logger.Info("[Uninstall] Stopped gateway service");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.Warn("[Uninstall] systemctl stop timed out (5s); distro may be wedged — wsl --unregister will force-terminate");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// PAIRING STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class MintBootstrapTokenStep : SetupStep
{
    public override string Id => "mint-token";
    public override string DisplayName => "Mint bootstrap token";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        // Token was already set by ConfigureGatewayStep
        if (string.IsNullOrWhiteSpace(ctx.SharedGatewayToken))
            return StepResult.Fail("No shared gateway token set by previous step");

        // Mint a bootstrap/QR token
        var env = new Dictionary<string, string>
        {
            ["OPENCLAW_GATEWAY_TOKEN"] = ctx.SharedGatewayToken
        };

        var mint = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw qr --json", TimeSpan.FromSeconds(30), env, ct);

        if (mint.ExitCode == 0 && !string.IsNullOrWhiteSpace(mint.Stdout))
        {
            // Parse bootstrap token from JSON output
            try
            {
                using var doc = JsonDocument.Parse(mint.Stdout.Trim());
                if (doc.RootElement.TryGetProperty("bootstrapToken", out var bt))
                {
                    ctx.BootstrapToken = bt.GetString();
                    ctx.Logger.StateChange("bootstrap_token", null, "[SET]");
                    return StepResult.Ok("Bootstrap token minted");
                }
            }
            catch (JsonException ex)
            {
                ctx.Logger.Warn($"Failed to parse QR JSON: {ex.Message}");
            }
        }

        // Fallback: use the shared gateway token directly as bootstrap
        ctx.BootstrapToken = ctx.SharedGatewayToken;
        ctx.Logger.Decision("QR mint failed or unavailable", "using shared gateway token as bootstrap");
        return StepResult.Ok("Using shared gateway token as bootstrap");
    }
}

public sealed class PairOperatorStep : SetupStep
{
    public override string Id => "pair-operator";
    public override string DisplayName => "Pair operator connection";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var gatewayUrl = ctx.GatewayUrl!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;

        if (string.IsNullOrEmpty(token))
            return StepResult.Terminal("No credential available for operator pairing");

        // Register gateway in registry (only once — reuse across retries)
        var registry = new GatewayRegistry(ctx.DataDir);
        registry.Load();

        string identityPath;
        if (!string.IsNullOrEmpty(ctx.GatewayRecordId))
        {
            var existing = registry.GetById(ctx.GatewayRecordId);
            if (existing == null)
                return StepResult.Fail($"Gateway record {ctx.GatewayRecordId} not found");
            identityPath = registry.GetIdentityDirectory(existing.Id);
            ctx.Logger.Info($"Reusing existing gateway record: id={existing.Id}");
        }
        else
        {
            var record = new GatewayRecord
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                Url = gatewayUrl,
                FriendlyName = $"Local ({ctx.DistroName})",
                SharedGatewayToken = ctx.SharedGatewayToken,
                BootstrapToken = ctx.BootstrapToken,
                IsLocal = true,
                LastConnected = DateTime.UtcNow
            };

            record = registry.AddOrUpdate(record);
            registry.SetActive(record.Id);
            registry.Save();
            ctx.GatewayRecordId = record.Id;
            identityPath = registry.GetIdentityDirectory(record.Id);
            ctx.Logger.Info($"Gateway record created: id={record.Id}");
        }

        // Initialize device identity
        Directory.CreateDirectory(identityPath);
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        ctx.Logger.Info($"Device identity initialized: {identity.DeviceId[..16]}...");
        ctx.OperatorDeviceId = identity.DeviceId;

        // Connect operator WebSocket — handle pairing-required flow
        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        OpenClawGatewayClient? client = null;

        try
        {
            // Phase 1: Initial connect (may get PAIRING_REQUIRED)
            client = new OpenClawGatewayClient(gatewayUrl, token, logger: wsLogger, identityPath: identityPath);
            client.UseV2Signature = true; // Local gateway uses v2 signature format
            var phase1Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (phase1Result == ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Operator connected directly (no pairing needed)");
                return StepResult.Ok("Operator connected and paired");
            }

            if (phase1Result == ConnectionOutcome.PairingRequired)
            {
                if (!ctx.Config.AutoApprovePairing)
                    return StepResult.Fail("Pairing required but auto-approve is disabled");

                ctx.Logger.Info("Pairing required — auto-approving via CLI");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                // Auto-approve the pending pairing request
                var approveResult = await AutoApprovePairing(ctx, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                // Wait for gateway to process the approval
                await Task.Delay(2000, ct);

                // Phase 2: Reconnect — the device should now be approved
                client = new OpenClawGatewayClient(gatewayUrl, token, logger: wsLogger, identityPath: identityPath);
                client.UseV2Signature = true;
                var phase2Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(20), ct);

                if (phase2Result == ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator paired successfully after approval");
                    // Disconnect before finalization
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;

                    // Phase 3: Skip operator finalization here — it must happen AFTER node pairing.
                    // The node pairing changes the device's "current metadata" to node/node-host,
                    // so operator finalization (as cli/cli) must come last to match what the tray sends.
                    ctx.Logger.Info("Operator paired — finalization deferred to after node pairing");
                    return StepResult.Ok("Operator paired (finalization deferred)");
                }

                return StepResult.Fail($"Reconnection after approval failed: {phase2Result}");
            }

            return StepResult.Fail($"Operator connection failed: {phase1Result}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Operator pairing failed: {ex.Message}", ex);
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// After initial pairing, the gateway knows us via auth.token (shared gateway token).
    /// The tray will connect using auth.deviceToken (the token we just received).
    /// This "finalizes" the transition so the gateway doesn't flag it as metadata-upgrade.
    /// </summary>
    private static async Task<StepResult> FinalizeWithDeviceToken(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        ctx.Logger.Info("Finalizing: reconnect with device token (like tray will)");

        // Read the device token we just stored
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        var deviceToken = identity.DeviceToken;

        if (string.IsNullOrEmpty(deviceToken))
        {
            ctx.Logger.Warn("No device token stored after pairing — skipping finalization");
            return StepResult.Ok("Operator paired (no finalization needed)");
        }

        // Wait for the gateway's internal session grace period to expire.
        // Without this delay, the gateway accepts the deviceToken connect within grace
        // but would later reject the tray's identical connect as "metadata-upgrade".
        ctx.Logger.Info("Waiting for gateway grace period to expire before finalization...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        // Connect exactly as the tray would: pass deviceToken as the credential
        var finalClient = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
        finalClient.UseV2Signature = true;

        try
        {
            var result = await WaitForConnectionOrPairing(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Finalization connected — tray will connect seamlessly");
                return StepResult.Ok("Operator paired and finalized for tray");
            }

            if (result == ConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Metadata-upgrade detected during finalization — auto-approving");
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
                finalClient = null;

                // Approve the metadata-upgrade
                var approveResult = await AutoApprovePairing(ctx, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                // One more connect to confirm
                finalClient = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
                finalClient.UseV2Signature = true;
                var finalResult = await WaitForConnectionOrPairing(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

                if (finalResult == ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Finalization approved — tray will connect seamlessly");
                    return StepResult.Ok("Operator paired and finalized for tray");
                }

                return StepResult.Fail($"Finalization failed after approval: {finalResult}");
            }

            return StepResult.Fail($"Finalization connect failed: {result}");
        }
        finally
        {
            if (finalClient != null)
            {
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
            }
        }
    }

    internal static async Task<StepResult> AutoApprovePairing(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken ?? throw new InvalidOperationException("No gateway token available for auto-approve");

        // Step 1: Get the latest pending request (--latest shows it, exits 1)
        var preview = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && openclaw devices approve --latest --json --token "{token}" """,
            TimeSpan.FromSeconds(30), ct: ct);

        ctx.Logger.Info($"Approve preview: exit={preview.ExitCode}");
        ctx.Logger.Debug($"Approve stdout: {preview.Stdout.Trim()}");

        // Parse requestId from the JSON output
        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(preview.Stdout.Trim());
            if (doc.RootElement.TryGetProperty("selected", out var selected) &&
                selected.TryGetProperty("requestId", out var rid))
            {
                requestId = rid.GetString();
            }
        }
        catch (JsonException ex)
        {
            ctx.Logger.Warn($"Failed to parse approve preview JSON: {ex.Message}");
        }

        if (string.IsNullOrEmpty(requestId))
        {
            ctx.Logger.Warn($"No requestId found in approve output");
            return StepResult.Fail("Could not find pending pairing request to approve");
        }

        ctx.Logger.Info($"Approving pairing request: {requestId}");

        // Step 2: Actually approve the request by passing the requestId directly
        var approve = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && openclaw devices approve "{requestId}" --json --token "{token}" """,
            TimeSpan.FromSeconds(30), ct: ct);

        ctx.Logger.Info($"Approve result: exit={approve.ExitCode}, stdout={approve.Stdout.Trim()}");

        if (approve.ExitCode != 0)
            return StepResult.Fail($"Device approval failed (exit {approve.ExitCode}): {approve.Stdout.Trim()}");

        return StepResult.Ok($"Approved request {requestId}");
    }

    internal enum ConnectionOutcome { Connected, PairingRequired, Error, Timeout }

    internal static async Task<ConnectionOutcome> WaitForConnectionOrPairing(
        OpenClawGatewayClient client, SetupContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ConnectionOutcome>();

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            ctx.Logger.Debug($"Operator connection status: {status}");
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(ConnectionOutcome.Connected);
            else if (status == ConnectionStatus.Error)
                tcs.TrySetResult(ConnectionOutcome.Error);
            else if (status == ConnectionStatus.Disconnected)
            {
                // Check if pairing was required — client sets IsPairingRequired before disconnect
                if (client.IsPairingRequired)
                    tcs.TrySetResult(ConnectionOutcome.PairingRequired);
                else
                    tcs.TrySetResult(ConnectionOutcome.Error);
            }
        }

        client.StatusChanged += OnStatusChanged;
        EventHandler<DeviceTokenReceivedEventArgs> onDeviceToken = (_, _) => ctx.Logger.Info("Device token received from gateway");
        client.DeviceTokenReceived += onDeviceToken;

        try
        {
            await client.ConnectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return ConnectionOutcome.Timeout;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
            client.DeviceTokenReceived -= onDeviceToken;
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var registry = new GatewayRegistry(ctx.DataDir);
        registry.Load();

        // Find all local gateway records to remove (mirrors old uninstall step 6a)
        var localRecords = registry.GetAll()
            .Where(r => r.IsLocal || (r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)))
            .ToList();

        if (localRecords.Count > 0)
        {
            foreach (var record in localRecords)
            {
                // Remove identity directory
                var identityDir = registry.GetIdentityDirectory(record.Id);
                if (Directory.Exists(identityDir))
                {
                    Directory.Delete(identityDir, recursive: true);
                    ctx.Logger.Info($"[Uninstall] Deleted identity directory: {identityDir}");
                }
                registry.Remove(record.Id);
            }
            registry.Save();
            ctx.Logger.Info($"[Uninstall] Removed {localRecords.Count} local gateway record(s)");
        }
        else
        {
            ctx.Logger.Info("[Uninstall] No local gateway records found");
        }

        // Null operator device token (mirrors old uninstall step 7)
        // Check if external gateways remain — if so, preserve root device tokens
        var hasExternalGateways = registry.GetAll().Any(r =>
            !r.IsLocal && !(r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)));

        if (hasExternalGateways)
        {
            ctx.Logger.Info("[Uninstall] Preserving root device tokens — external gateway records remain");
        }
        else
        {
            var operatorCleared = DeviceIdentity.TryClearDeviceTokenForRole(ctx.DataDir, "operator");
            ctx.Logger.Info(operatorCleared
                ? "[Uninstall] Cleared operator device token"
                : "[Uninstall] Operator device token already absent");
        }

        // Best-effort revoke operator token via gateway HTTP endpoint (mirrors old step 4)
        await TryRevokeOperatorTokenAsync(ctx, ct);
    }

    private static async Task TryRevokeOperatorTokenAsync(SetupContext ctx, CancellationToken ct)
    {
        try
        {
            // Read settings.json for legacy token if available
            var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
            if (!File.Exists(settingsPath)) return;

            var settingsJson = await File.ReadAllTextAsync(settingsPath, ct);
            using var doc = JsonDocument.Parse(settingsJson);

            string? token = null;
            if (doc.RootElement.TryGetProperty("Token", out var tokenProp))
                token = tokenProp.GetString();

            if (string.IsNullOrWhiteSpace(token)) return;

            var gatewayUrl = ctx.GatewayUrl ?? "ws://localhost:18789";
            var httpBase = gatewayUrl
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await http.PostAsync($"{httpBase}/api/v1/operator/disconnect", content: null, cts.Token);
            ctx.Logger.Info($"[Uninstall] Revoke operator token: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            ctx.Logger.Info($"[Uninstall] Best-effort token revoke failed ({ex.GetType().Name}); gateway may be down");
        }
    }
}

public sealed class PairNodeStep : SetupStep
{
    public override string Id => "pair-node";
    public override string DisplayName => "Pair node connection";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var gatewayUrl = ctx.GatewayUrl!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;

        if (string.IsNullOrEmpty(token))
            return StepResult.Terminal("No credential available for node pairing");

        var registry = new GatewayRegistry(ctx.DataDir);
        registry.Load();
        var record = registry.GetById(ctx.GatewayRecordId!);
        if (record == null)
            return StepResult.Fail("Gateway record not found in registry");

        var identityPath = registry.GetIdentityDirectory(record.Id);

        // Verify gateway is reachable before connecting
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync($"http://localhost:{ctx.Config.GatewayPort}/", ct);
            ctx.Logger.Debug($"Gateway health check: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Gateway not reachable before node pairing: {ex.Message}");
        }

        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        WindowsNodeClient? client = null;

        try
        {
            // Phase 1: Connect (may get PAIRING_REQUIRED)
            client = new WindowsNodeClient(gatewayUrl, token, identityPath, logger: wsLogger);
            client.UseV2Signature = true;

            // Register capabilities BEFORE connect — gateway stores them from hello message
            RegisterCapabilitiesFromConfig(client, ctx);

            var outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (outcome == NodeConnectionOutcome.Connected)
            {
                ctx.NodeDeviceId = client.ShortDeviceId;
                ctx.Logger.Info($"Node connected directly: {ctx.NodeDeviceId}");
                return StepResult.Ok("Node connected and paired");
            }

            if (outcome == NodeConnectionOutcome.PairingRequired)
            {
                if (!ctx.Config.AutoApprovePairing)
                    return StepResult.Fail("Node pairing required but auto-approve is disabled");

                ctx.Logger.Info("Node pairing required — auto-approving via CLI");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await AutoApproveNodePairing(ctx, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                await Task.Delay(2000, ct);

                // Phase 2: Reconnect after approval
                client = new WindowsNodeClient(gatewayUrl, token, identityPath, logger: wsLogger);
                client.UseV2Signature = true;
                RegisterCapabilitiesFromConfig(client, ctx);

                outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(20), ct);
                if (outcome == NodeConnectionOutcome.Connected)
                {
                    ctx.NodeDeviceId = client.ShortDeviceId;
                    ctx.Logger.Info($"Node paired after approval: {ctx.NodeDeviceId}");
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;

                    // Skip node finalization — the operator finalization in VerifyEndToEndStep
                    // will be the last connect, ensuring operator metadata is "current".
                    // Node finalization would rotate tokens and potentially invalidate the operator token.
                    ctx.Logger.Info("Node paired — skipping node finalization (operator finalization is last)");
                    return StepResult.Ok("Node paired successfully");
                }

                return StepResult.Fail($"Node reconnection after approval failed: {outcome}");
            }

            return StepResult.Fail($"Node connection failed: {outcome}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Node pairing failed: {ex.Message}", ex);
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// After node pairing, finalize by connecting with the node device token to avoid
    /// metadata-upgrade when the tray reconnects.
    /// </summary>
    private static async Task<StepResult> FinalizeNodeWithDeviceToken(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        ctx.Logger.Info("Finalizing node: reconnect with node device token");

        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        var nodeToken = identity.NodeDeviceToken;

        if (string.IsNullOrEmpty(nodeToken))
        {
            ctx.Logger.Warn("No node device token stored after pairing — skipping node finalization");
            return StepResult.Ok("Node paired (no finalization needed)");
        }

        // Wait for grace period (same as operator finalization)
        ctx.Logger.Info("Waiting for gateway grace period before node finalization...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var finalClient = new WindowsNodeClient(gatewayUrl, nodeToken, identityPath, logger: wsLogger);
        finalClient.UseV2Signature = true;

        try
        {
            var result = await WaitForNodeConnection(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == NodeConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Node finalization connected — tray will connect seamlessly");
                return StepResult.Ok("Node paired and finalized for tray");
            }

            if (result == NodeConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Node metadata-upgrade detected — auto-approving");
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
                finalClient = null;

                var approveResult = await AutoApproveNodePairing(ctx, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Node finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                finalClient = new WindowsNodeClient(gatewayUrl, nodeToken, identityPath, logger: wsLogger);
                finalClient.UseV2Signature = true;
                var finalResult = await WaitForNodeConnection(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

                if (finalResult == NodeConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Node finalization approved — tray will connect seamlessly");
                    return StepResult.Ok("Node paired and finalized for tray");
                }

                return StepResult.Fail($"Node finalization failed after approval: {finalResult}");
            }

            return StepResult.Fail($"Node finalization failed: {result}");
        }
        finally
        {
            if (finalClient != null)
            {
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
            }
        }
    }

    private enum NodeConnectionOutcome { Connected, PairingRequired, Error, Timeout }

    private static async Task<NodeConnectionOutcome> WaitForNodeConnection(
        WindowsNodeClient client, SetupContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<NodeConnectionOutcome>();

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            ctx.Logger.Debug($"Node connection status: {status}");
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(NodeConnectionOutcome.Connected);
            else if (status == ConnectionStatus.Error)
                tcs.TrySetResult(NodeConnectionOutcome.Error);
            else if (status == ConnectionStatus.Disconnected)
            {
                if (client.IsPendingApproval)
                    tcs.TrySetResult(NodeConnectionOutcome.PairingRequired);
                else
                    tcs.TrySetResult(NodeConnectionOutcome.Error);
            }
        }

        client.StatusChanged += OnStatusChanged;

        try
        {
            await client.ConnectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return NodeConnectionOutcome.Timeout;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
        }
    }

    private static async Task<StepResult> AutoApproveNodePairing(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken ?? throw new InvalidOperationException("No gateway token available for auto-approve");

        // Step 1: Get latest pending request
        var preview = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && openclaw devices approve --latest --json --token "{token}" """,
            TimeSpan.FromSeconds(30), ct: ct);

        ctx.Logger.Info($"Node approve preview: exit={preview.ExitCode}");
        ctx.Logger.Debug($"Node approve stdout: {preview.Stdout.Trim()}");

        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(preview.Stdout.Trim());
            if (doc.RootElement.TryGetProperty("selected", out var selected) &&
                selected.TryGetProperty("requestId", out var rid))
            {
                requestId = rid.GetString();
            }
        }
        catch (JsonException ex)
        {
            ctx.Logger.Warn($"Failed to parse node approve preview: {ex.Message}");
        }

        if (string.IsNullOrEmpty(requestId))
            return StepResult.Fail("Could not find pending node pairing request");

        ctx.Logger.Info($"Approving node pairing request: {requestId}");

        // Step 2: Approve
        var approve = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && openclaw devices approve "{requestId}" --json --token "{token}" """,
            TimeSpan.FromSeconds(30), ct: ct);

        ctx.Logger.Info($"Node approve result: exit={approve.ExitCode}");

        return approve.ExitCode == 0
            ? StepResult.Ok($"Node approved: {requestId}")
            : StepResult.Fail($"Node approval failed (exit {approve.ExitCode}): {approve.Stdout.Trim()}");
    }

    private static void RegisterCapabilitiesFromConfig(WindowsNodeClient client, SetupContext ctx)
    {
        var capabilities = ctx.Config.Capabilities.GetEnabledCapabilities();
        foreach (var (category, commands) in capabilities)
        {
            client.RegisterCapability(new StubNodeCapability(category, commands));
        }
        ctx.Logger.Info($"Registered {capabilities.Count} capability categories with {capabilities.Sum(c => c.Commands.Length)} total commands");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        // Null node device token (mirrors old uninstall step 7 for node role)
        // Only clear if no external gateways remain (same logic as PairOperatorStep)
        var registry = new GatewayRegistry(ctx.DataDir);
        registry.Load();
        var hasExternalGateways = registry.GetAll().Any(r =>
            !r.IsLocal && !(r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)));

        if (hasExternalGateways)
        {
            ctx.Logger.Info("[Uninstall] Preserving node device token — external gateway records remain");
        }
        else
        {
            var nodeCleared = DeviceIdentity.TryClearDeviceTokenForRole(ctx.DataDir, "node");
            ctx.Logger.Info(nodeCleared
                ? "[Uninstall] Cleared node device token"
                : "[Uninstall] Node device token already absent");
        }

        return Task.CompletedTask;
    }
}

public sealed class VerifyEndToEndStep : SetupStep
{
    public override string Id => "verify-e2e";
    public override string DisplayName => "Verify end-to-end connectivity";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        // Verify gateway is still healthy
        var distro = ctx.DistroName!;
        var status = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw gateway status --json", TimeSpan.FromSeconds(15), ct: ct);

        if (status.ExitCode != 0 || !status.Stdout.Contains("running", StringComparison.OrdinalIgnoreCase))
            return StepResult.Fail("Gateway is not running");

        // Verify registry state
        var registry = new GatewayRegistry(ctx.DataDir);
        registry.Load();
        var record = registry.GetById(ctx.GatewayRecordId!);
        if (record == null)
            return StepResult.Fail("Gateway record missing from registry");

        var identityPath = registry.GetIdentityDirectory(record.Id);
        if (!DeviceIdentity.HasStoredDeviceToken(identityPath))
        {
            ctx.Logger.Warn("No stored device token found — tray app may need to re-pair");
        }
        else
        {
            ctx.Logger.Info("Device token present — performing final operator handshake");

            // CRITICAL: The operator finalization must happen AFTER node pairing.
            // Node pairing changes the device's "current metadata" to node-host/node.
            // The tray connects as operator (cli/cli), so we must re-establish operator
            // as the device's last-seen metadata. This prevents "metadata-upgrade" errors.
            var wsLogger = new SetupOpenClawLogger(ctx.Logger);
            var finalResult = await FinalizeOperatorForTray(ctx, ctx.GatewayUrl!, identityPath, wsLogger, ct);
            if (!finalResult.IsSuccess)
                return finalResult;
        }

        // Write setup-state.json so tray knows the distro name for WSL keepalive
        await WriteSetupStateAsync(ctx, ct);

        // Write settings.json with EnableNodeMode + capability toggles from config
        WriteSettingsJson(ctx);

        // Drain any remaining pending approvals (device or node) so tray starts clean
        await DrainPendingApprovalsAsync(ctx, ct);

        return StepResult.Ok("Gateway running; operator finalized; settings written for tray.");
    }

    private static async Task DrainPendingApprovalsAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken!;
        var pathPrefix = ctx.WslPathPrefix;

        // Approve pending device requests (up to 5 iterations to drain all)
        for (int i = 0; i < 5; i++)
        {
            var preview = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{pathPrefix} && openclaw devices approve --latest --json --token "{token}" """,
                TimeSpan.FromSeconds(15), ct: ct);

            if (preview.ExitCode != 0 || preview.Stdout.Contains("No pending", StringComparison.OrdinalIgnoreCase))
                break;

            // Parse and approve
            string? requestId = null;
            try
            {
                using var doc = JsonDocument.Parse(preview.Stdout.Trim());
                if (doc.RootElement.TryGetProperty("selected", out var selected) &&
                    selected.TryGetProperty("requestId", out var rid))
                    requestId = rid.GetString();
            }
            catch { break; }

            if (string.IsNullOrEmpty(requestId)) break;

            ctx.Logger.Info($"Draining pending device approval: {requestId}");
            await ctx.Commands.RunInWslAsync(
                distro,
                $"""{pathPrefix} && openclaw devices approve "{requestId}" --json --token "{token}" """,
                TimeSpan.FromSeconds(15), ct: ct);
        }

        // Approve pending node requests
        for (int i = 0; i < 5; i++)
        {
            var nodeList = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{pathPrefix} && openclaw nodes list --json --token "{token}" """,
                TimeSpan.FromSeconds(15), ct: ct);

            if (nodeList.ExitCode != 0) break;

            try
            {
                using var doc = JsonDocument.Parse(nodeList.Stdout.Trim());
                if (!doc.RootElement.TryGetProperty("pending", out var pending) || pending.GetArrayLength() == 0)
                    break;

                var requestId = pending[0].GetProperty("requestId").GetString();
                if (string.IsNullOrEmpty(requestId)) break;

                ctx.Logger.Info($"Draining pending node approval: {requestId}");
                await ctx.Commands.RunInWslAsync(
                    distro,
                    $"""{pathPrefix} && openclaw nodes approve "{requestId}" --json --token "{token}" """,
                    TimeSpan.FromSeconds(15), ct: ct);
            }
            catch { break; }
        }
    }

    private static void WriteSettingsJson(SetupContext ctx)
    {
        var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
        ctx.Config.Settings.MergeIntoSettingsFile(settingsPath);
        ctx.Logger.Info($"Wrote settings.json: EnableNodeMode={ctx.Config.Settings.EnableNodeMode}");
    }

    /// <summary>
    /// Final operator connect using device token — establishes operator/cli/cli as the
    /// device's "current metadata" so the tray can connect without metadata-upgrade.
    /// </summary>
    private static async Task<StepResult> FinalizeOperatorForTray(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        var deviceToken = identity.DeviceToken;

        if (string.IsNullOrEmpty(deviceToken))
            return StepResult.Fail("No device token available for operator finalization");

        // Wait for grace period to expire so this connect is treated as a real metadata change
        ctx.Logger.Info("Waiting for grace period before final operator handshake...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var client = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
        client.UseV2Signature = true;

        try
        {
            var result = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == PairOperatorStep.ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Final operator handshake succeeded — tray will connect seamlessly");
                return StepResult.Ok("Operator finalized");
            }

            if (result == PairOperatorStep.ConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Metadata-upgrade detected — auto-approving for tray");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await PairOperatorStep.AutoApprovePairing(ctx, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Operator finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                // After approval, the gateway rotates the device token. The old one is invalid.
                // Clear the stale DeviceToken from the identity file so the client doesn't
                // try to use it (OpenClawGatewayClient prefers stored DeviceToken over constructor token).
                ctx.Logger.Info("Clearing stale operator device token from identity file");
                DeviceIdentity.TryClearDeviceToken(identityPath);

                // Reconnect with the SHARED GATEWAY TOKEN to get a fresh device token.
                ctx.Logger.Info("Reconnecting with shared token to get fresh device token after approval");
                client = new OpenClawGatewayClient(gatewayUrl, ctx.SharedGatewayToken!, logger: wsLogger, identityPath: identityPath);
                client.UseV2Signature = true;
                var confirmResult = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

                if (confirmResult == PairOperatorStep.ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator finalization approved — fresh device token stored, tray will connect seamlessly");
                    return StepResult.Ok("Operator finalized after approval");
                }

                return StepResult.Fail($"Operator finalization failed after approval: {confirmResult}");
            }

            return StepResult.Fail($"Operator finalization failed: {result}");
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    private static async Task WriteSetupStateAsync(SetupContext ctx, CancellationToken ct)
    {
        var stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
        Directory.CreateDirectory(stateDir);

        var statePath = Path.Combine(stateDir, "setup-state.json");
        // Phase and Status must be integers matching the tray's LocalGatewaySetupPhase/Status enums.
        // Phase.Complete = 13, Status.Complete = 7
        var state = new
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid().ToString("N"),
            InstallId = Guid.NewGuid().ToString("N"),
            Phase = 13,
            Status = 7,
            DistroName = ctx.DistroName,
            GatewayUrl = ctx.GatewayUrl,
            IsLocalOnly = true,
            FailureCode = (string?)null,
            UserMessage = (string?)null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Issues = Array.Empty<object>(),
            History = Array.Empty<object>()
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(statePath, json, ct);
        ctx.Logger.Info($"Wrote setup-state.json: DistroName={ctx.DistroName}");
    }
}

// ─── Step 16: Start WSL Keepalive ───

public sealed class StartKeepaliveStep : SetupStep
{
    public override string Id => "start-keepalive";
    public override string DisplayName => "Start WSL keepalive";

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        ctx.Logger.Info($"Launching persistent keepalive for distro: {distro}");

        // Launch detached keepalive process — keeps the distro alive so port forwarding
        // remains stable until the tray starts its own keepalive.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = $"-d {distro} -- sleep infinity",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
        {
            ctx.Logger.Warn("Failed to start keepalive process — tray will start its own");
            return Task.FromResult(StepResult.Ok());
        }

        ctx.Logger.Info($"Keepalive process started (PID {proc.Id}), distro will stay alive for tray launch");

        // Write keepalive marker so tray doesn't spawn a duplicate
        WriteKeepaliveMarker(ctx, proc.Id);

        return Task.FromResult(StepResult.Ok());
    }

    private static void WriteKeepaliveMarker(SetupContext ctx, int pid)
    {
        var markerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray", "wsl-keepalive");
        Directory.CreateDirectory(markerDir);

        var markerPath = Path.Combine(markerDir, $"{ctx.DistroName}.json");
        var marker = new
        {
            DistroName = ctx.DistroName,
            Pid = pid,
            StartTimeUtc = DateTimeOffset.UtcNow,
            ProcessName = "wsl"
        };
        var json = System.Text.Json.JsonSerializer.Serialize(marker, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(markerPath, json);
        ctx.Logger.Info($"Wrote keepalive marker: {markerPath}");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName;
        if (string.IsNullOrEmpty(distro))
        {
            ctx.Logger.Info("[Uninstall] No distro name — skipping keepalive cleanup");
            return;
        }

        // Kill keepalive wsl.exe processes for this distro
        // Pattern: wsl.exe -d <distro> -- sleep <N>
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("wsl")
                .Concat(System.Diagnostics.Process.GetProcessesByName("wsl.exe"));

            foreach (var proc in procs)
            {
                try
                {
                    // Read command line via WMI/CIM
                    var cmdLine = GetProcessCommandLine(proc.Id);
                    if (cmdLine != null
                        && cmdLine.Contains(distro, StringComparison.OrdinalIgnoreCase)
                        && cmdLine.Contains("sleep", StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill();
                        ctx.Logger.Info($"[Uninstall] Killed keepalive process PID {proc.Id}");
                    }
                }
                catch { /* process may have exited */ }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"[Uninstall] Error enumerating keepalive processes: {ex.Message}");
        }

        // Delete keepalive marker file
        var markerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray", "wsl-keepalive");
        var markerPath = Path.Combine(markerDir, $"{distro}.json");

        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
            ctx.Logger.Info($"[Uninstall] Deleted keepalive marker: {markerPath}");
        }

        // Clean up empty marker directory
        if (Directory.Exists(markerDir) && !Directory.EnumerateFileSystemEntries(markerDir).Any())
        {
            Directory.Delete(markerDir);
            ctx.Logger.Info("[Uninstall] Deleted empty wsl-keepalive directory");
        }

        await Task.CompletedTask;
    }

    private static string? GetProcessCommandLine(int pid)
    {
        try
        {
            // Use WMI to get the command line
            var result = new System.Diagnostics.Process();
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch { return null; }
    }
}

public sealed class RunGatewayWizardStep : SetupStep
{
    public override string Id => "run-wizard";
    public override string DisplayName => "Run gateway wizard";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => ctx.Config.SkipWizard;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var runner = new SetupWizardRunner(ctx);
        return runner.RunAsync(ct);
    }
}
