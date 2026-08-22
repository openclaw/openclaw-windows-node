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

public sealed class RunGatewayWizardStep : SetupStep
{
    public override string Id => "run-wizard";
    public override string DisplayName => "Run gateway wizard";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => ctx.Config.SkipWizard || ctx.Config.LocalAi.Enabled;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var runner = new SetupWizardRunner(ctx);
        return runner.RunAsync(ct);
    }
}
