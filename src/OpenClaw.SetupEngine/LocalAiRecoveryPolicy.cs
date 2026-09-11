using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;

namespace OpenClaw.SetupEngine;

public static class LocalAiRecoveryPolicy
{
    public static bool CanRecoverExistingGateway(
        ExistingConfigDetector.ExistingConfig existing,
        string expectedGatewayId,
        string targetDistroName,
        string expectedGatewayUrl) =>
        existing.HasLocalGateway &&
        string.Equals(
            existing.LocalGatewayId,
            expectedGatewayId,
            StringComparison.Ordinal) &&
        existing.HasDistro &&
        existing.DistroIsAppOwned &&
        string.Equals(
            existing.DistroName,
            targetDistroName,
            StringComparison.OrdinalIgnoreCase) &&
        GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
            existing.LocalGatewayUrl,
            expectedGatewayUrl);
}

internal sealed record LocalAiRecoveryConfigurationBaseline(
    bool LocalAiEnabled,
    string? LocalAiSelectedModelId,
    int LocalAiPort,
    bool RollbackOnFailure,
    bool SkipWizard,
    string DistroName,
    int GatewayPort,
    string? GatewayUrl)
{
    public static LocalAiRecoveryConfigurationBaseline Capture(SetupConfig config) =>
        new(
            config.LocalAi.Enabled,
            config.LocalAi.SelectedModelId,
            config.LocalAi.Port,
            config.RollbackOnFailure,
            config.SkipWizard,
            config.DistroName,
            config.GatewayPort,
            config.GatewayUrl);

    public void Restore(SetupConfig config)
    {
        config.LocalAi.Enabled = LocalAiEnabled;
        config.LocalAi.SelectedModelId = LocalAiSelectedModelId;
        config.LocalAi.Port = LocalAiPort;
        config.RollbackOnFailure = RollbackOnFailure;
        config.SkipWizard = SkipWizard;
        config.DistroName = DistroName;
        config.GatewayPort = GatewayPort;
        config.GatewayUrl = GatewayUrl;
    }
}

public sealed class ValidateLocalAiRecoveryGatewayStep : SetupStep
{
    private readonly Func<string, string, string?, string?, ExistingConfigDetector.ExistingConfig> _detect;
    private readonly Func<string, IReadOnlyList<GatewayRecord>> _loadGatewayRecords;
    private readonly bool _finalCheck;

    public ValidateLocalAiRecoveryGatewayStep(bool finalCheck = false)
        : this(ExistingConfigDetector.Detect, LoadGatewayRecords, finalCheck)
    {
    }

    internal ValidateLocalAiRecoveryGatewayStep(
        Func<string, string, string?, string?, ExistingConfigDetector.ExistingConfig> detect,
        Func<string, IReadOnlyList<GatewayRecord>> loadGatewayRecords,
        bool finalCheck = false)
    {
        _detect = detect ?? throw new ArgumentNullException(nameof(detect));
        _loadGatewayRecords =
            loadGatewayRecords ?? throw new ArgumentNullException(nameof(loadGatewayRecords));
        _finalCheck = finalCheck;
    }

    public override string Id => _finalCheck
        ? "revalidate-local-ai-recovery-gateway"
        : "validate-local-ai-recovery-gateway";
    public override string DisplayName => _finalCheck
        ? "Recheck gateway before Local AI recovery changes"
        : "Verify existing gateway for Local AI recovery";
    public override bool CanRetry => false;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var expectedGatewayId = ctx.Config.LocalAiRecoveryGatewayId;
        if (string.IsNullOrWhiteSpace(expectedGatewayId))
        {
            return Task.FromResult(StepResult.Terminal(
                "Local AI recovery is missing its managed gateway identity. Close recovery and retry."));
        }

        var owners = _loadGatewayRecords(ctx.DataDir)
            .Where(record =>
                GatewayRecordEditing.IsSetupManagedLocalRecord(record) &&
                !string.IsNullOrWhiteSpace(record.Id) &&
                !string.IsNullOrWhiteSpace(
                    GatewayRecordEditing.ResolveManagedDistroName(record)))
            .ToArray();
        if (owners.Length != 1 ||
            !string.Equals(owners[0].Id, expectedGatewayId, StringComparison.Ordinal) ||
            !string.Equals(
                GatewayRecordEditing.ResolveManagedDistroName(owners[0])?.Trim(),
                ctx.Config.DistroName.Trim(),
                StringComparison.OrdinalIgnoreCase) ||
            !GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
                owners[0].Url,
                ctx.Config.EffectiveGatewayUrl))
        {
            return Task.FromResult(StepResult.Terminal(
                "The managed gateway owner changed before Local AI recovery. Close recovery and review Connection settings."));
        }

        ExistingConfigDetector.ExistingConfig existing;
        try
        {
            existing = _detect(
                ctx.DataDir,
                ctx.Config.DistroName,
                ctx.LocalDataDir,
                expectedGatewayId);
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"Local AI recovery gateway inspection failed: {ex.Message}");
            return Task.FromResult(StepResult.Terminal(
                "OpenClaw could not safely verify the existing managed gateway. Close recovery and run full setup."));
        }

        return Task.FromResult(
            LocalAiRecoveryPolicy.CanRecoverExistingGateway(
                existing,
                expectedGatewayId,
                ctx.Config.DistroName,
                ctx.Config.EffectiveGatewayUrl)
                ? StepResult.Ok("Existing app-managed gateway verified for Local AI recovery.")
                : StepResult.Terminal(
                    "The existing app-managed gateway is unavailable. Close recovery and run full setup."));
    }

    private static IReadOnlyList<GatewayRecord> LoadGatewayRecords(string dataDir)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        return registry.GetAll();
    }
}

public sealed class PreserveLocalAiRecoveryGatewayStep : SetupStep
{
    private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _restart;
    private readonly Func<LocalAiResolvedInstall, CancellationToken, Task<bool>> _probeOriginalEndpoint;

    public PreserveLocalAiRecoveryGatewayStep()
        : this(StartGatewayStep.RestartAndWaitForHealthAsync, ProbeOriginalEndpointAsync)
    {
    }

    internal PreserveLocalAiRecoveryGatewayStep(
        Func<SetupContext, CancellationToken, Task<StepResult>> restart,
        Func<LocalAiResolvedInstall, CancellationToken, Task<bool>>? probeOriginalEndpoint = null)
    {
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _probeOriginalEndpoint = probeOriginalEndpoint ?? ProbeOriginalEndpointAsync;
    }

    public override string Id => "preserve-local-ai-recovery-gateway";
    public override string DisplayName => "Preserve gateway during Local AI recovery";
    public override bool CanRetry => false;
    // A cold WSL restart can consume two CLI attempts plus the full health window.
    public override TimeSpan GetRollbackTimeout(SetupContext ctx) =>
        TimeSpan.FromSeconds(Math.Max(
            Math.Max(1, ctx.Config.RollbackTimeoutSeconds),
            Math.Max(1, ctx.Config.Gateway.HealthTimeoutSeconds) + 75));

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) =>
        Task.FromResult(StepResult.Ok("Gateway recovery guard armed."));

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        Exception? receiptError = null;
        if (ctx.LocalAiRecoveryOriginalInstall is { } originalInstall)
        {
            if (!ctx.LocalAiRecoveryReceiptRollbackAllowed)
            {
                ctx.Logger.Warn(
                    "The previous Local AI endpoint receipt was not restored because gateway provider rollback did not complete.");
            }
            else if (!await _probeOriginalEndpoint(originalInstall, ct).ConfigureAwait(false))
            {
                ctx.Logger.Warn(
                    "The previous Local AI endpoint could not be verified as healthy; preserving the replacement " +
                    "receipt instead of restoring a receipt for an endpoint that is not confirmed reachable.");
            }
            else
            {
                try
                {
                    var store = new LocalAiManifestStore(new LocalAiPaths(ctx.LocalDataDir));
                    await store.SaveAsync(originalInstall.Manifest, ct).ConfigureAwait(false);
                    ctx.LocalAiResolvedInstall = store.ResolveAndValidate(originalInstall.Manifest);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    receiptError = ex;
                    ctx.Logger.Warn(
                        $"Restoring the previous Local AI endpoint receipt failed ({ex.GetType().Name}).");
                }
            }
            ctx.LocalAiRecoveryProviderTransition = false;
            ctx.LocalAiRecoveryReceiptRollbackAllowed = false;
            ctx.LocalAiGatewayPriorState = null;
        }

        if (ctx.LocalAiRecoveryStoppedWsl)
        {
            StepResult restart = await _restart(ctx, ct);
            if (!restart.IsSuccess)
                throw new InvalidOperationException(restart.Message);

            ctx.LocalAiRecoveryStoppedWsl = false;
        }

        if (receiptError is not null)
        {
            throw new InvalidOperationException(
                "The previous Local AI endpoint receipt could not be restored.",
                receiptError);
        }
    }

    /// <summary>
    /// Confirms the original (pre-recovery) llama-server endpoint is actually alive before the
    /// Gateway is pointed back at it. A stale manifest receipt alone cannot tell us whether the
    /// original process is still running.
    /// </summary>
    private static async Task<bool> ProbeOriginalEndpointAsync(
        LocalAiResolvedInstall original,
        CancellationToken ct)
    {
        if (original.Endpoint is null)
            return true;

        using var client = new LlamaServerClient();
        LlamaServerRouterProbeResult probe = await client.ProbeManagedModelAsync(
            original.Endpoint,
            original.Manifest.ModelAlias,
            original.ModelPath,
            ct).ConfigureAwait(false);
        return probe.IsHealthy;
    }
}
