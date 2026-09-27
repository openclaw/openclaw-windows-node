using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClawTray.Services;

/// <summary>
/// Adapts existing Local AI owners to onboarding. No process, catalog, receipt writer,
/// Gateway connection, or setup-window lifetime is owned by this adapter.
/// </summary>
internal sealed class SetupLocalAiHost(
    Func<Task<LocalAiSetupResolution>> resolveRoute,
    Func<GatewayRegistry?> getRegistry,
    Func<ILocalAiRuntime?> getRuntime,
    Func<CancellationToken, Task<LocalAiResolvedInstall?>> loadInstall,
    Func<LocalAiResolvedInstall, CancellationToken, Task<bool>> inspectFiles,
    Func<CancellationToken, Task<HostHardwareInfo>> probeHardware,
    Func<LocalAiGatewayProviderCoordinator> getProvider,
    Func<GatewayRecord?, GatewayRecord?, Task>? reconcileConnection = null,
    Action? reportSettlementFailure = null) : ISetupLocalAiHost
{
    private GatewayRegistrySnapshot? _setupRegistryBaseline;

    public GatewayRegistrySnapshot BeginGatewaySetup() => _setupRegistryBaseline =
        (getRegistry() ?? throw new InvalidOperationException("The Gateway registry is unavailable.")).CapturePersistedSnapshot();

    public async Task ReconcileGatewaySetupAsync(GatewayRegistrySnapshot expectedOutput, string? completedGatewayId)
    {
        var registry = getRegistry() ?? throw new InvalidOperationException("The Gateway registry is unavailable.");
        var baseline = _setupRegistryBaseline ?? throw new InvalidOperationException("The setup registry baseline is unavailable.");
        try
        {
            _setupRegistryBaseline = registry.ReconcileSetupOutcome(baseline, expectedOutput, completedGatewayId);
            if (reconcileConnection is not null)
                await reconcileConnection(baseline.Records.FirstOrDefault(record => record.Id == baseline.ActiveId),
                    registry.GetActive());
        }
        catch
        {
            reportSettlementFailure?.Invoke();
            throw;
        }
    }

    public async Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct)
    {
        var resolution = await resolveRoute().WaitAsync(ct);
        var target = resolution.RecoveryTarget;
        if (resolution.Route != LocalAiSetupRoute.Recovery || target is null ||
            getRegistry()?.GetActive()?.Id != target.GatewayId)
            return new(LocalAiOnboardingState.UnsupportedGateway);
        LocalAiResolvedInstall? install = null;
        bool damaged = false;
        try { install = await loadInstall(ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { damaged = true; }
        var hardware = await probeHardware(ct);
        var eligibility = LocalInferenceEligibility.Evaluate(hardware, install?.Manifest.ModelCatalogId);
        bool verified = false;
        if (install is not null)
        {
            try { verified = await inspectFiles(install, ct); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { damaged = true; }
        }
        ct.ThrowIfCancellationRequested();
        if (getRegistry()?.GetActive()?.Id != target.GatewayId)
            return new(LocalAiOnboardingState.UnsupportedGateway);
        return LocalAiOnboardingSnapshot.Project(ToTarget(target), eligibility, install, verified, damaged,
            getRuntime()?.Snapshot, Identity(install));
    }

    public async Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
    {
        if (!selected.CanReview)
            throw new InvalidOperationException("Local AI review is not available for this selection.");
        var current = await ObserveAsync(ct);
        RequireSameSelection(selected, current);
        if (!current.CanReview)
            throw new InvalidOperationException("Local AI readiness changed. Check this PC again.");
        return current.Target!;
    }

    public async Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
    {
        if (!selected.CanUse)
            throw new LocalAiSelectionRejectedException("The selected Local AI model is not ready to start.");
        var current = await ObserveAsync(ct);
        RequireSameSelection(selected, current);
        if (!current.CanUse)
            throw new LocalAiSelectionRejectedException("Local AI readiness changed. Check this PC again.");
        var runtime = getRuntime() ?? throw new InvalidOperationException("The managed Local AI runtime is unavailable.");
        var install = await loadInstall(ct) ?? throw new InvalidOperationException("The Local AI receipt is unavailable.");
        if (Identity(install) != selected.ReceiptIdentity)
            throw new LocalAiSelectionRejectedException("The selected Local AI installation changed.");
        var provider = getProvider();
        var admitted = await provider.ValidatePublicationAsync(install, ct);
        if (!admitted.Success)
            throw new LocalAiSelectionRejectedException(admitted.Detail ?? "Local AI route publication was not admitted.");
        RequireActiveTarget(selected.Target!, mutationStarted: false);
        var started = await runtime.EnsureStartedAsync(ct);
        ct.ThrowIfCancellationRequested();
        RequireActiveTarget(selected.Target!, mutationStarted: true);
        if (started.State is LocalAiRuntimeState.Failed or LocalAiRuntimeState.Conflict &&
            started.Ownership == LocalAiOwnership.None && !started.GatewayRouteRequiresResolution)
            throw new LocalAiStartFailedException(started.Detail ?? "The managed Local AI model could not start.");
        install = await loadInstall(ct) ?? throw new InvalidOperationException("The Local AI receipt is unavailable.");
        if (Identity(install) != selected.ReceiptIdentity || started.State != LocalAiRuntimeState.Healthy ||
            started.Ownership != LocalAiOwnership.CompanionManaged ||
            started.ModelId != install.Manifest.ModelCatalogId || started.Endpoint != install.Endpoint ||
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) != selected.ModelRef ||
            started.ModelEvidence.State is not (LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded))
            throw new InvalidOperationException("The exact managed Local AI model did not become ready.");
        var published = await provider.PublishAsync(install, ct);
        if (!published.Success)
            throw new InvalidOperationException(published.Detail);
        RequireActiveTarget(selected.Target!, mutationStarted: true);
        return new(selected.Target!.GatewayId, selected.ModelRef!);
    }

    private void RequireActiveTarget(SetupLocalAiTarget target, bool mutationStarted)
    {
        var registry = getRegistry();
        var snapshot = registry?.GetSnapshot();
        var owners = snapshot is null ? [] : LocalAiGatewayDistroResolver.FindOwners(snapshot.Records);
        if (owners.Count != 1 || owners[0].Id != target.GatewayId || snapshot?.ActiveId != target.GatewayId ||
            GatewayRecordEditing.ResolveManagedDistroName(owners[0])?.Trim() != target.DistroName ||
            !GatewayRecordEditing.AreEquivalentLoopbackEndpoints(owners[0].Url, $"ws://127.0.0.1:{target.GatewayPort}"))
        {
            const string message = "The managed Gateway changed. Refresh before using Local AI.";
            if (!mutationStarted)
                throw new LocalAiSelectionRejectedException(message);
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireSameSelection(LocalAiOnboardingSnapshot selected, LocalAiOnboardingSnapshot current)
    {
        if (selected.Target != current.Target || selected.ModelRef != current.ModelRef ||
            selected.ReceiptIdentity != current.ReceiptIdentity)
            throw new LocalAiSelectionRejectedException("The selected Gateway or Local AI model changed. Refresh before continuing.");
    }

    private static SetupLocalAiTarget ToTarget(LocalAiRecoveryTarget target) =>
        new(target.GatewayId, target.DistroName, target.GatewayPort, target.ModelCatalogId, target.RequestedLocalAiPort);

    private static string? Identity(LocalAiResolvedInstall? install) => install is null ? null :
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(install.Manifest with { Endpoint = null })));
}
