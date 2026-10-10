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
    Action? reportSettlementFailure = null,
    LocalAiGatewayLifecycle? nativeLifecycle = null) : ISetupLocalAiHost, INativeSetupLocalAiHost
{
    private GatewayRegistrySnapshot? _setupRegistryBaseline;
    private GatewayRecord? _nativeRecord;
    private NativeLocalAiGatewayTarget? _nativeTarget;
    private IGatewayAiSetupTransport? _nativeTransport;
    private Func<CancellationToken, Task>? _authorizeNative;
    public bool HasNativeSelection => _nativeRecord is { } record &&
        nativeLifecycle?.OwnsGateway(record.Id) == true;

    public async Task ReconcileNativeAsync(IGatewayAiSetupTransport transport, string modelRef, CancellationToken ct)
    {
        if (!HasNativeSelection || !modelRef.StartsWith("llamacpp/", StringComparison.Ordinal)) return;
        var install = await loadInstall(ct) ?? throw new InvalidOperationException("The Local AI receipt is unavailable.");
        await nativeLifecycle!.ReconcileVerifiedAsync(_nativeRecord!, transport, install, ct);
    }

    public async Task WithdrawNativeAsync(CancellationToken ct)
    {
        if (!HasNativeSelection) return;
        var runtime = getRuntime() ?? throw new InvalidOperationException("The Local AI runtime is unavailable.");
        await runtime.StopAsync(ct);
        if (runtime.Snapshot.Ownership != LocalAiOwnership.None || runtime.Snapshot.GatewayRouteRequiresResolution)
            throw new InvalidOperationException("The Local AI route could not be withdrawn. Cleanup remains pending for its original Gateway.");
        await nativeLifecycle!.ForgetWithdrawnAsync(_nativeRecord!.Id, ct);
    }

    public void ConfigureNative(GatewayRecord record, IGatewayAiSetupTransport transport,
        Func<CancellationToken, Task> authorize)
    {
        if (nativeLifecycle is null)
            throw new InvalidOperationException("The native Local AI runtime owner is unavailable.");
        _nativeTarget = NativeLocalAiGatewayTarget.Capture(record, transport.Route);
        _nativeRecord = record;
        _nativeTransport = transport;
        _authorizeNative = authorize;
        nativeLifecycle.Register(record, transport);
    }

    public void ReleaseNative(IGatewayAiSetupTransport transport)
    {
        nativeLifecycle?.Release(transport);
        if (ReferenceEquals(_nativeTransport, transport)) _nativeTransport = null;
    }

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

    public Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct) => ObserveAsync(ct, null);

    public async Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress)
    {
        SetupLocalAiTarget target;
        if (_nativeRecord is { } native)
        {
            if (_nativeTransport is { } transport &&
                (!transport.OperatorScopes.Contains("operator.admin", StringComparer.Ordinal) ||
                 new[] { "config.get", "config.patch", "openclaw.setup.verify" }
                    .Any(method => !transport.Methods.Contains(method, StringComparer.Ordinal))))
                return new(LocalAiOnboardingState.UnsupportedGateway);
            await _authorizeNative!(ct);
            target = new(native.Id, string.Empty, new Uri(native.Url).Port,
                null, null, true, GatewayDashboardBinding.Capture(native));
        }
        else
        {
            var resolution = await resolveRoute().WaitAsync(ct);
            var recovery = resolution.RecoveryTarget;
            if (resolution.Route != LocalAiSetupRoute.Recovery || recovery is null ||
                getRegistry()?.GetActive()?.Id != recovery.GatewayId)
                return new(LocalAiOnboardingState.UnsupportedGateway);
            target = ToTarget(recovery);
        }
        LocalAiResolvedInstall? install = null;
        bool damaged = false;
        try { install = await loadInstall(ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { damaged = true; }
        progress?.Report(LocalAiSetupStage.CheckingHardware);
        var hardware = await probeHardware(ct);
        var eligibility = install is null
            ? LocalInferenceEligibility.Evaluate(hardware)
            : LocalInferenceEligibility.EvaluateInstalled(hardware, install.Manifest.ModelCatalogId);
        bool verified = false;
        if (install is not null)
        {
            progress?.Report(LocalAiSetupStage.CheckingFiles);
            try { verified = await inspectFiles(install, ct); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { damaged = true; }
        }
        ct.ThrowIfCancellationRequested();
        if (!target.IsNative && getRegistry()?.GetActive()?.Id != target.GatewayId)
            return new(LocalAiOnboardingState.UnsupportedGateway);
        if (target.IsNative)
            target = target with { ModelCatalogId = install?.Manifest.ModelCatalogId,
                RequestedLocalAiPort = install?.Manifest.RequestedPort };
        var ownership = target.IsNative && _nativeTransport is { } nativeTransport
            ? await nativeLifecycle!.ObserveOwnershipAsync(_nativeRecord!, nativeTransport, install, ct)
            : (NativeLocalAiOwnershipState?)null;
        return LocalAiOnboardingSnapshot.Project(target, eligibility, install, verified, damaged,
            getRuntime()?.Snapshot, Identity(install), nativeOwnership: ownership);
    }

    public async Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
    {
        if (!selected.CanReview)
            throw new InvalidOperationException("Local AI review is not available for this selection.");
        var current = await ObserveAsync(ct);
        RequireSameSelection(selected, current);
        if (!current.CanReview)
            throw new InvalidOperationException("Local AI readiness changed. Check this PC again.");
        if (!current.Target!.IsNative && nativeLifecycle?.HasNativeBinding == true)
            throw new LocalAiSelectionRejectedException(
                "Stop Local AI and release its native Gateway ownership before repairing it for WSL.");
        if (current.Target!.IsNative && getRuntime()?.Snapshot is
            { Ownership: LocalAiOwnership.CompanionManaged } or { GatewayRouteRequiresResolution: true })
            throw new InvalidOperationException("Stop the owned Local AI runtime and resolve its Gateway route before repairing its files.");
        return current.Target!;
    }

    public Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
        => UseAsync(selected, ct, null);

    public async Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress)
    {
        if (!selected.CanUse)
            throw new LocalAiSelectionRejectedException("The selected Local AI model is not ready to start.");
        var current = await ObserveAsync(ct, progress);
        RequireSameSelection(selected, current);
        if (!current.CanUse)
            throw new LocalAiSelectionRejectedException("Local AI readiness changed. Check this PC again.");
        return await UseAdmittedAsync(current, ct, progress);
    }

    public async Task<SetupLocalAiUseResult> UseInstalledAsync(LocalAiInstallAndUseIntent intent, CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress)
    {
        var current = await ObserveAsync(ct, progress);
        intent.RequireInstalledSelection(current);
        return await UseAdmittedAsync(current, ct, progress);
    }

    private async Task<SetupLocalAiUseResult> UseAdmittedAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress)
    {
        var runtime = getRuntime() ?? throw new InvalidOperationException("The managed Local AI runtime is unavailable.");
        var install = await loadInstall(ct) ?? throw new InvalidOperationException("The Local AI receipt is unavailable.");
        if (Identity(install) != selected.ReceiptIdentity)
            throw new LocalAiSelectionRejectedException("The selected Local AI installation changed.");
        LocalAiGatewayProviderCoordinator? provider = null;
        var runtimeProgress = progress is null ? null : new RuntimeStartProgress(progress, ct);
        progress?.Report(LocalAiSetupStage.PreparingGateway);
        if (selected.Target!.IsNative)
        {
            await _authorizeNative!(ct);
            if (_nativeTransport is null)
                throw new LocalAiSelectionRejectedException("Reconnect the native Gateway before using Local AI.");
            if (!nativeLifecycle!.HasNativeBinding && runtime.Snapshot.Ownership == LocalAiOwnership.CompanionManaged)
                throw new LocalAiSelectionRejectedException("Stop the previous Gateway's Local AI runtime before selecting this native Gateway.");
            try { await nativeLifecycle.PrepareAsync(install, ct); }
            catch (LocalAiSelectionRejectedException) when (nativeLifecycle.HasNativeBinding &&
                runtime.Snapshot.Ownership == LocalAiOwnership.CompanionManaged)
            {
                await nativeLifecycle.ReconcileVerifiedAsync(_nativeRecord!, _nativeTransport, install, ct, runtimeProgress);
                await nativeLifecycle.PrepareAsync(install, ct);
            }
        }
        else
        {
            if (nativeLifecycle?.HasNativeBinding == true)
                throw new LocalAiSelectionRejectedException(
                    "Stop Local AI and release its native Gateway ownership before using it with WSL.");
            provider = getProvider();
            var admitted = await provider.ValidatePublicationAsync(install, ct);
            if (!admitted.Success)
                throw new LocalAiSelectionRejectedException(admitted.Detail ?? "Local AI route publication was not admitted.");
        }
        LocalAiRuntimeSnapshot started;
        try
        {
            RequireActiveTarget(selected.Target!, mutationStarted: false);
            if (selected.Target.IsNative)
                started = await runtime.EnsureStartedAsync(ct, runtimeProgress);
            else
            {
                progress?.Report(LocalAiSetupStage.StartingRuntime);
                started = await runtime.EnsureStartedAsync(ct);
            }
        }
        finally
        {
            if (selected.Target.IsNative) nativeLifecycle!.EndEndpointRecovery();
        }
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
        // Native startup owns publication and recovery authorization under the runtime
        // gate. A second host write could otherwise republish after a newer Stop.
        if (!selected.Target.IsNative)
        {
            progress?.Report(LocalAiSetupStage.PublishingProvider);
            var published = await provider!.PublishAsync(install, ct);
            if (!published.Success)
                throw new InvalidOperationException(published.Detail);
        }
        else
        {
            var current = runtime.Snapshot;
            if (current.State != started.State || current.Ownership != started.Ownership ||
                current.Endpoint != started.Endpoint || current.ModelId != started.ModelId ||
                current.ProcessId != started.ProcessId || current.ProcessStartedAtUtc != started.ProcessStartedAtUtc ||
                current.GatewayRouteRequiresResolution ||
                current.ModelEvidence.State is not (LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded) ||
                current.ModelEvidence.Sha256 != started.ModelEvidence.Sha256 ||
                current.ModelEvidence.SizeBytes != started.ModelEvidence.SizeBytes)
                throw new InvalidOperationException("The Local AI runtime changed after startup. Review its current state before continuing.");
        }
        RequireActiveTarget(selected.Target!, mutationStarted: true);
        return new(selected.Target!.GatewayId, selected.ModelRef!);
    }

    private void RequireActiveTarget(SetupLocalAiTarget target, bool mutationStarted)
    {
        if (target.IsNative)
        {
            if (_nativeTransport is null || _nativeTarget?.GatewayId != target.GatewayId ||
                _nativeTarget.Route.EndpointBinding != target.EndpointBinding)
                throw new LocalAiSelectionRejectedException("The selected native Gateway changed.");
            _nativeTarget.RequireCurrent(_nativeTransport.Route);
            return;
        }
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

    private sealed class RuntimeStartProgress(IProgress<LocalAiSetupStage> progress, CancellationToken ct)
        : IProgress<LocalAiRuntimeStartStage>
    {
        public void Report(LocalAiRuntimeStartStage stage)
        {
            if (ct.IsCancellationRequested) return;
            progress.Report(stage switch
            {
                LocalAiRuntimeStartStage.StartingRuntime => LocalAiSetupStage.StartingRuntime,
                LocalAiRuntimeStartStage.PublishingProvider => LocalAiSetupStage.PublishingProvider,
                LocalAiRuntimeStartStage.CheckingConfiguration => LocalAiSetupStage.CheckingConfiguration,
                LocalAiRuntimeStartStage.VerifyingEndpoint => LocalAiSetupStage.VerifyingEndpoint,
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            });
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
