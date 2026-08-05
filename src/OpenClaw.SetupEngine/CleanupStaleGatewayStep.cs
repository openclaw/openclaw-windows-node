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
        var localStateFile = Path.Combine(ctx.LocalDataDir, "setup-state.json");
        if (File.Exists(localStateFile))
        {
            File.Delete(localStateFile);
            ctx.Logger.Info("Deleted stale setup-state.json (LocalAppData)");
        }

        // Remove stale gateway record for our local URL if it exists
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var existing = registry.FindByUrl(ctx.GatewayUrl!);
        if (existing != null)
        {
            // Preserve non-local records and SSH-tunneled gateways — they may be
            // remote gateways that happen to use localhost as a forwarded port.
            if (!PairOperatorStep.IsSetupManagedLocalRecord(existing, ctx))
            {
                ctx.Logger.Warn($"Skipping cleanup of gateway record {existing.Id}: " +
                    "not a SetupEngine-managed local gateway");
            }
            else
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
        }

        await Task.CompletedTask;
        return StepResult.Ok("Gateway state cleaned");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        // Delete setup-state.json (written by VerifyEndToEndStep)
        var localDataPath = ctx.LocalDataDir;

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
