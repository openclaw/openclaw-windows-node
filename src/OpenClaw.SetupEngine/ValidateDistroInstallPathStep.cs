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
// CLEANUP STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class ValidateDistroInstallPathStep : SetupStep
{
    public const string StepId = "validate-distro-path";

    public override string Id => StepId;
    public override string DisplayName => "Validate WSL distro install path";
    public override bool CanRetry => false;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (DistroInstallPathPolicy.TryGetNewInstallPath(
                ctx.LocalDataDir,
                ctx.DistroName,
                out _,
                out var error))
        {
            return Task.FromResult(StepResult.Ok());
        }

        return Task.FromResult(StepResult.Terminal(
            DistroInstallPathPolicy.WithLegacyReplacementGuidance(ctx.DistroName, error)));
    }
}
