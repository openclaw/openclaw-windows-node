using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;


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
        var installUrl = ctx.Config.Gateway.InstallUrl ?? GatewayLkgVersion.DefaultInstallUrl;

        // Validate URL is HTTPS to prevent downgrade attacks
        if (!Uri.TryCreate(installUrl, UriKind.Absolute, out var parsedUrl) ||
            !string.Equals(parsedUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return StepResult.Fail($"Installer URL must be HTTPS: {installUrl}");
        }

        string installScript;
        try
        {
            installScript = BuildInstallCommand(installUrl, ctx.Config.Gateway.Version);
        }
        catch (ArgumentException ex)
        {
            return StepResult.Fail(ex.Message);
        }

        var result = await ctx.Commands.RunInWslAsync(distro, installScript, TimeSpan.FromMinutes(5), ct: ct);

        if (result.ExitCode != 0)
            return StepResult.Fail($"CLI install failed (exit {result.ExitCode}): {result.Stderr}");

        var verifyCommands = new (string Command, string? ExecutablePath)[]
        {
            ("openclaw --version", null),
            ($"/home/{user}/.openclaw/bin/openclaw --version", $"/home/{user}/.openclaw/bin/openclaw"),
            ("/opt/openclaw/bin/openclaw --version", "/opt/openclaw/bin/openclaw"),
            ("/usr/local/bin/openclaw --version", "/usr/local/bin/openclaw")
        };

        foreach (var (cmd, executablePath) in verifyCommands)
        {
            var verify = await ctx.Commands.RunInWslAsync(distro, cmd, TimeSpan.FromSeconds(15), ct: ct);
            if (verify.ExitCode == 0 && !string.IsNullOrWhiteSpace(verify.Stdout))
            {
                if (executablePath != null)
                {
                    var pathResult = await EnsureCliOnDefaultPathAsync(ctx, distro, executablePath, ct);
                    if (!pathResult.IsSuccess)
                        return pathResult;
                }

                ctx.Logger.Info($"OpenClaw CLI version: {verify.Stdout.Trim()}");
                return StepResult.Ok($"CLI installed: {verify.Stdout.Trim()}");
            }
        }

        return StepResult.Fail("CLI installed but not found in any known location");
    }

    internal static string BuildInstallCommand(string installUrl, string? requestedVersion)
    {
        var escapedUrl = WslShellQuoting.EscapePosixSingleQuoteInner(installUrl);
        if (string.IsNullOrWhiteSpace(requestedVersion))
            return $"curl -fsSL --proto '=https' --tlsv1.2 '{escapedUrl}' | bash";

        var trimmedVersion = requestedVersion.Trim();
        if (trimmedVersion.Contains('\n') || trimmedVersion.Contains('\r'))
            throw new ArgumentException("Gateway version cannot contain newlines.");

        var escapedVersion = WslShellQuoting.EscapePosixSingleQuoteInner(trimmedVersion);
        return $"curl -fsSL --proto '=https' --tlsv1.2 '{escapedUrl}' | bash -s -- --version '{escapedVersion}'";
    }

    private static async Task<StepResult> EnsureCliOnDefaultPathAsync(
        SetupContext ctx,
        string distro,
        string executablePath,
        CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;

        if (!executablePath.StartsWith("/", StringComparison.Ordinal) ||
            executablePath.Contains('\'') ||
            executablePath.Contains('\n'))
        {
            return StepResult.Fail($"Refusing to create openclaw PATH symlink for unexpected install path: {executablePath}");
        }

        if (!string.Equals(executablePath, "/usr/local/bin/openclaw", StringComparison.Ordinal))
        {
            var linkCommand = $"""
                set -e
                ln -sfn {executablePath} /usr/local/bin/openclaw
                echo OPENCLAW_PATH_READY
                """;

            var link = await ctx.Commands.RunInWslAsync(
                distro,
                linkCommand,
                TimeSpan.FromSeconds(15),
                ct: ct,
                user: "root");

            if (link.ExitCode != 0 || !link.Stdout.Contains("OPENCLAW_PATH_READY", StringComparison.Ordinal))
                return StepResult.Fail($"Failed to make openclaw available on default PATH: {link.Stderr}");
        }

        var bareVerify = await ctx.Commands.RunInWslAsync(
            distro,
            $"env -i HOME=/home/{user} USER={user} PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin openclaw --version",
            TimeSpan.FromSeconds(15),
            ct: ct);

        if (bareVerify.ExitCode != 0 || string.IsNullOrWhiteSpace(bareVerify.Stdout))
            return StepResult.Fail($"openclaw PATH symlink verification failed: {bareVerify.Stderr}");

        ctx.Logger.Info($"OpenClaw CLI available on default PATH: {bareVerify.Stdout.Trim()}");
        return StepResult.Ok();
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, $"rm -rf /opt/openclaw /home/{user}/.openclaw /usr/local/bin/openclaw", TimeSpan.FromSeconds(30), ct: ct, user: "root");
    }
}
