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
