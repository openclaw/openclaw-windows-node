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
