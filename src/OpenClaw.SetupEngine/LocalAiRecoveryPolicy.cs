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
    string? GatewayUrl,
    string? SelectedProfileId,
    bool NetworkingConsent)
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
            config.GatewayUrl,
            config.LocalAi.SelectedProfileId,
            config.LocalAi.WslMirroredNetworkingConsent);

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
        config.LocalAi.SelectedProfileId = SelectedProfileId;
        config.LocalAi.WslMirroredNetworkingConsent = NetworkingConsent;
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

public sealed class ValidateLocalAiRecoveryGatewayCompatibilityStep : SetupStep
{
    internal const string SupportedMarker = "LOCAL_AI_CONDITIONAL_SET_SUPPORTED";
    internal const string UnsupportedMarker = "LOCAL_AI_CONDITIONAL_SET_UNSUPPORTED";

    private readonly Func<SetupContext, CancellationToken, Task<CommandResult>> _probe;

    public ValidateLocalAiRecoveryGatewayCompatibilityStep()
        : this(ProbeConditionalSetSupportAsync)
    {
    }

    internal ValidateLocalAiRecoveryGatewayCompatibilityStep(
        Func<SetupContext, CancellationToken, Task<CommandResult>> probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public override string Id => "validate-local-ai-recovery-gateway-compatibility";
    public override string DisplayName => "Check gateway recovery compatibility";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        LocalAiResolvedInstall?[] recoveryInstalls =
        [
            ctx.LocalAiRecoveryPendingInstall,
            ctx.LocalAiRecoveryOriginalInstall,
            ctx.LocalAiResolvedInstall,
        ];
        if (recoveryInstalls.All(install => install is null))
        {
            return StepResult.Skip(
                "No prior Local AI route requires conditional recovery support.");
        }
        if (recoveryInstalls.All(install => install is null || install.Manifest.RequestedPort != 0))
            return StepResult.Skip("Fixed-port Local AI recovery does not require conditional route updates.");

        CommandResult result = await _probe(ctx, ct).ConfigureAwait(false);
        if (result.ExitCode == 0 &&
            result.Stdout.Contains(SupportedMarker, StringComparison.Ordinal))
        {
            return StepResult.Ok("Gateway supports safe automatic-port recovery.");
        }
        if (result.ExitCode == 42 &&
            result.Stdout.Contains(UnsupportedMarker, StringComparison.Ordinal))
        {
            return StepResult.Terminal(
                "This Gateway version cannot safely recover Local AI with an automatic port. Update the Gateway, then retry recovery.");
        }
        return StepResult.Fail("OpenClaw could not verify Gateway support for safe automatic-port recovery.");
    }

    private static Task<CommandResult> ProbeConditionalSetSupportAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        string script = $$"""
            set -eu
            {{ctx.WslPathPrefix}}
            if openclaw config set --help | grep -Fq -- '--expect-current-json'; then
              echo {{SupportedMarker}}
              exit 0
            fi
            echo {{UnsupportedMarker}}
            exit 42
            """;
        return ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            script,
            TimeSpan.FromMinutes(1),
            ct: ct,
            user: ctx.Config.Wsl.User,
            inputViaStdin: true);
    }
}

public sealed class PreserveLocalAiRecoveryGatewayStep : SetupStep
{
    private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _restart;
    private readonly Func<LocalAiResolvedInstall, CancellationToken, Task<bool>> _probeOriginalEndpoint;
    private readonly Func<
        SetupContext,
        LocalAiGatewayPriorState,
        LocalAiResolvedInstall,
        LocalAiResolvedInstall,
        CancellationToken,
        Task<bool>> _restoreRecoveryRoute;

    public PreserveLocalAiRecoveryGatewayStep()
        : this(StartGatewayStep.RestartAndWaitForHealthAsync, ProbeOriginalEndpointAsync)
    {
    }

    internal PreserveLocalAiRecoveryGatewayStep(
        Func<SetupContext, CancellationToken, Task<StepResult>> restart,
        Func<LocalAiResolvedInstall, CancellationToken, Task<bool>>? probeOriginalEndpoint = null,
        Func<
            SetupContext,
            LocalAiGatewayPriorState,
            LocalAiResolvedInstall,
            LocalAiResolvedInstall,
            CancellationToken,
            Task<bool>>? restoreRecoveryRoute = null)
    {
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _probeOriginalEndpoint = probeOriginalEndpoint ?? ProbeOriginalEndpointAsync;
        _restoreRecoveryRoute = restoreRecoveryRoute ?? ConfigureLocalAiGatewayStep.RestoreRecoveryRouteAsync;
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
            else if (ctx.LocalAiRuntimeBorrowed && ctx.LocalAiRuntime is { } borrowedRuntime)
            {
                try
                {
                    var store = new LocalAiManifestStore(new LocalAiPaths(ctx.LocalDataDir));
                    if (ctx.LocalAiResolvedInstall!.Manifest.ReplacedManifest is not null)
                    {
                        ctx.LocalAiResolvedInstall = await store
                            .RestoreRecoveryManifestAsync(
                                ctx.LocalAiResolvedInstall.Manifest,
                                originalInstall.Manifest,
                                ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        ctx.LocalAiResolvedInstall = await store
                            .RestoreManifestIfUnchangedAsync(
                                ctx.LocalAiResolvedInstall.Manifest,
                                originalInstall.Manifest,
                                ct)
                            .ConfigureAwait(false);
                    }
                    bool runtimeOwnsGatewayRoute = ctx.LocalAiGatewayPriorState is null;
                    LocalAiRuntimeSnapshot restored = runtimeOwnsGatewayRoute
                        ? await borrowedRuntime.RestartForSetupRollbackAsync(ct).ConfigureAwait(false)
                        : await borrowedRuntime.RestartForSetupAsync(ct).ConfigureAwait(false);
                    LocalAiResolvedInstall restoredInstall = await store.LoadAsync(ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException(
                            "The previous Local AI receipt was unavailable after restarting its runtime.");
                    if (restored.State != LocalAiRuntimeState.Healthy ||
                        restored.Ownership != LocalAiOwnership.CompanionManaged ||
                        restored.ModelId != restoredInstall.Manifest.ModelCatalogId ||
                        restored.Endpoint != restoredInstall.Endpoint ||
                        restored.ModelEvidence.State is not
                            (LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded))
                    {
                        throw new InvalidDataException(
                            restored.Detail ?? "The previous Local AI runtime could not be restored.");
                    }
                    ctx.LocalAiResolvedInstall = restoredInstall;
                    AcquireLocalAiRuntimeStep.TransferCleanupOwnershipToRestoredRuntime(
                        ctx,
                        restoredInstall);
                    if (ctx.LocalAiGatewayPriorState is { } prior &&
                        !await _restoreRecoveryRoute(
                                ctx,
                                prior,
                                originalInstall,
                                restoredInstall,
                                ct)
                            .ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            "The previous Local AI gateway route could not be updated to its restored endpoint.");
                    }
                    if (!await _probeOriginalEndpoint(restoredInstall, ct).ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            "The previous Local AI endpoint was not healthy after its runtime was restored.");
                    }
                    if (!runtimeOwnsGatewayRoute &&
                        !await ConfigureLocalAiGatewayStep
                            .AcknowledgeBorrowedRuntimeRouteAsync(ctx, ct)
                            .ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            "The restored Local AI route could not be acknowledged by its runtime owner.");
                    }
                    ctx.LocalAiBorrowedRuntimeRestored = true;
                    CompleteReceiptRollback(ctx);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    receiptError = ex;
                    ctx.LocalAiRecoveryRollbackUncertain = true;
                    ctx.LocalAiRecoveryReceiptRollbackAllowed = false;
                    ctx.Logger.Warn(
                        $"Restoring the previous Local AI runtime failed ({ex.GetType().Name}).");
                }
            }
            else if (!await _probeOriginalEndpoint(originalInstall, ct).ConfigureAwait(false))
            {
                ctx.Logger.Warn(
                    "The previous Local AI endpoint could not be verified as healthy; preserving the replacement " +
                    "receipt instead of restoring a receipt for an endpoint that is not confirmed reachable.");
                ctx.LocalAiRecoveryRollbackUncertain = true;
                ctx.LocalAiRecoveryReceiptRollbackAllowed = false;
            }
            else
            {
                try
                {
                    var store = new LocalAiManifestStore(new LocalAiPaths(ctx.LocalDataDir));
                    ctx.LocalAiResolvedInstall = await store
                        .RestoreRecoveryManifestAsync(
                            ctx.LocalAiResolvedInstall!.Manifest,
                            originalInstall.Manifest,
                            ct)
                        .ConfigureAwait(false);
                    CompleteReceiptRollback(ctx);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    receiptError = ex;
                    ctx.LocalAiRecoveryRollbackUncertain = true;
                    ctx.LocalAiRecoveryReceiptRollbackAllowed = false;
                    ctx.Logger.Warn(
                        $"Restoring the previous Local AI endpoint receipt failed ({ex.GetType().Name}).");
                }
            }
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

    private static void CompleteReceiptRollback(SetupContext ctx)
    {
        // The recovery guard now owns the settled receipt. Retire the earlier upgrade and
        // recovery baselines so later reverse rollback steps cannot restore them again.
        ctx.LocalAiUpgradeOriginalInstall = null;
        ctx.LocalAiRecoveryOriginalInstall = null;
        ctx.LocalAiRecoveryProviderTransition = false;
        ctx.LocalAiRecoveryReceiptRollbackAllowed = false;
        ctx.LocalAiRecoveryRollbackUncertain = false;
        ctx.LocalAiGatewayPriorState = null;
        ctx.LocalAiRecoveryGatewayConfigurationStartedThisRun = false;
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
