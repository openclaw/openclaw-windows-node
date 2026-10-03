using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

public enum LocalAiOnboardingState
{
    Checking, SetUp, StartAndUse, Use, Repair, BusyGpu, Unsupported, Unknown, UnsupportedGateway, Working, Reconcile,
    ManagementBlocked
}

/// <summary>Read-only discovery evidence, never authorization to mutate or adopt a route.</summary>
public enum NativeLocalAiOwnershipState
{
    Unselected, SameOwner, RecoveryRequired, MissingReceipt, InvalidReceipt, DifferentOwner, Unavailable
}

public enum LocalAiSetupStage
{
    CheckingHardware, CheckingFiles, PreparingGateway, StartingRuntime, PublishingProvider,
    CheckingConfiguration, VerifyingEndpoint
}

/// <summary>The exact existing Gateway, not a request to create or replace one.</summary>
public sealed record SetupLocalAiTarget(
    string GatewayId, string DistroName, int GatewayPort,
    string? ModelCatalogId, int? RequestedLocalAiPort,
    bool IsNative = false, string? EndpointBinding = null);

public interface INativeSetupLocalAiHost
{
    bool HasNativeSelection { get; }
    void ConfigureNative(OpenClaw.Connection.GatewayRecord record, IGatewayAiSetupTransport transport,
        Func<CancellationToken, Task> authorize);
    void ReleaseNative(IGatewayAiSetupTransport transport);
    Task ReconcileNativeAsync(IGatewayAiSetupTransport transport, string modelRef, CancellationToken ct);
    Task WithdrawNativeAsync(CancellationToken ct);
    Task<SetupLocalAiUseResult> UseInstalledAsync(LocalAiInstallAndUseIntent intent, CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress);
}

public sealed record LocalAiOnboardingSnapshot(
    LocalAiOnboardingState State,
    SetupLocalAiTarget? Target = null,
    string? ModelRef = null,
    string? ReceiptIdentity = null,
    string? GpuName = null,
    string? ModelName = null,
    string? ReasonKey = null,
    LocalInferenceEligibilityResult? Eligibility = null,
    bool HasInstallationEvidence = false,
    NativeLocalAiOwnershipState? NativeOwnership = null)
{
    // Only a fresh, conclusively incompatible device loses this choice. Configuration,
    // catalog and driver failures still need attention, as does any known installation.
    public bool ShowLocalChoice => State != LocalAiOnboardingState.Unsupported ||
        HasInstallationEvidence || Eligibility is not { CanInstall: false } eligibility ||
        !(eligibility.FailureCode is LocalInferenceEligibilityFailureCode.InsufficientGpuMemory or
            LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow ||
          eligibility.FailureCode == LocalInferenceEligibilityFailureCode.CatalogSelectionFailed &&
            eligibility.SelectionFailureCode == LocalInferenceSelectionFailureCode.NoNvidiaGpu);

    public bool CanReview => Target is not null && State is LocalAiOnboardingState.SetUp or LocalAiOnboardingState.Repair;
    public bool CanUse => Target is not null && State is LocalAiOnboardingState.StartAndUse or LocalAiOnboardingState.Use or LocalAiOnboardingState.Reconcile;
    // A changed revision can contain incompatible provider edits. Keep ordinary
    // model use available until explicit recovery confirms the managed route.
    public bool ReplacesDetectedChoice => CanUse && (Target?.IsNative != true ||
        NativeOwnership == NativeLocalAiOwnershipState.SameOwner);
    public bool CanRefresh => State is LocalAiOnboardingState.BusyGpu or LocalAiOnboardingState.Unknown or
        LocalAiOnboardingState.Unsupported or LocalAiOnboardingState.UnsupportedGateway or LocalAiOnboardingState.Working or
        LocalAiOnboardingState.ManagementBlocked;

    public static LocalAiOnboardingSnapshot Project(
        SetupLocalAiTarget? target, LocalInferenceEligibilityResult? eligibility,
        LocalAiResolvedInstall? install, bool filesVerified, bool receiptDamaged,
        LocalAiRuntimeSnapshot? runtime, string? receiptIdentity = null, bool installationKnown = false,
        NativeLocalAiOwnershipState? nativeOwnership = null)
    {
        if (target?.IsNative == true && nativeOwnership is { } ownership)
        {
            var reason = ownership switch
            {
                NativeLocalAiOwnershipState.MissingReceipt => "LocalOwnershipMissing",
                NativeLocalAiOwnershipState.InvalidReceipt or NativeLocalAiOwnershipState.DifferentOwner => "LocalOwnershipInvalid",
                NativeLocalAiOwnershipState.Unavailable => "LocalOwnershipUnavailable",
                NativeLocalAiOwnershipState.SameOwner or NativeLocalAiOwnershipState.RecoveryRequired
                    when (receiptDamaged || install is null || !filesVerified) &&
                        (runtime?.Ownership == LocalAiOwnership.CompanionManaged ||
                         runtime?.GatewayRouteRequiresResolution == true) => "LocalOwnershipFiles",
                _ => null
            };
            if (reason is not null)
                return new(LocalAiOnboardingState.ManagementBlocked, target, ReasonKey: reason,
                    HasInstallationEvidence: true, NativeOwnership: ownership);
        }
        var hasInstallationEvidence = installationKnown || install is not null || receiptDamaged ||
            receiptIdentity is not null || target?.ModelCatalogId is not null ||
            runtime?.ModelId is not null || runtime?.Ownership == LocalAiOwnership.CompanionManaged ||
            runtime?.State is LocalAiRuntimeState.Starting or LocalAiRuntimeState.Stopping or
                LocalAiRuntimeState.Healthy or LocalAiRuntimeState.Failed or LocalAiRuntimeState.Conflict;
        if (target is null)
            return new(LocalAiOnboardingState.UnsupportedGateway, HasInstallationEvidence: hasInstallationEvidence);
        if (eligibility is null || eligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)
            return new(LocalAiOnboardingState.Unknown, target, ReceiptIdentity: receiptIdentity,
                GpuName: eligibility?.SelectedGpu?.Name, ModelName: eligibility?.Plan?.Model.DisplayName,
                Eligibility: eligibility, HasInstallationEvidence: hasInstallationEvidence);
        var result = new LocalAiOnboardingSnapshot(LocalAiOnboardingState.Unsupported, target,
            install is null ? null : LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
            receiptIdentity, eligibility.SelectedGpu?.Name, eligibility.Plan?.Model.DisplayName,
            eligibility.CanInstall ? null : Reason(eligibility), eligibility, hasInstallationEvidence, nativeOwnership);
        if (!eligibility.CanInstall)
            return result;
        if (runtime?.State is LocalAiRuntimeState.Starting or LocalAiRuntimeState.Stopping)
            return result with { State = LocalAiOnboardingState.Working };
        bool exactHealthyRuntime = install is not null && filesVerified &&
            runtime is { State: LocalAiRuntimeState.Healthy, Ownership: LocalAiOwnership.CompanionManaged } &&
            runtime.ModelId == install.Manifest.ModelCatalogId && runtime.Endpoint == install.Endpoint &&
            runtime.ModelEvidence.State is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded;
        if (eligibility.Status == LocalInferenceEligibilityStatus.EligibleButBusy &&
            !(exactHealthyRuntime && runtime!.ModelEvidence.State == LocalAiModelAvailabilityState.Loaded))
            return result with { State = LocalAiOnboardingState.BusyGpu };
        if (target.IsNative && install is not null && filesVerified &&
            (nativeOwnership == NativeLocalAiOwnershipState.RecoveryRequired ||
             runtime?.GatewayRouteRequiresResolution == true ||
             runtime is { State: LocalAiRuntimeState.Failed, Ownership: LocalAiOwnership.CompanionManaged }))
            return result with { State = LocalAiOnboardingState.Reconcile };
        if (receiptDamaged || install is not null && (!filesVerified ||
            runtime?.State is LocalAiRuntimeState.Failed or LocalAiRuntimeState.Conflict ||
            runtime?.State == LocalAiRuntimeState.Healthy && !exactHealthyRuntime))
            return result with { State = LocalAiOnboardingState.Repair };
        if (install is null)
            return result with { State = LocalAiOnboardingState.SetUp };
        if (runtime is null)
            return result with { State = LocalAiOnboardingState.Unknown };
        return result with { State = exactHealthyRuntime ? LocalAiOnboardingState.Use : LocalAiOnboardingState.StartAndUse };
    }

    private static string Reason(LocalInferenceEligibilityResult result) =>
        LocalInferenceEligibilityDiagnostics.GetUnavailableReason(result).Kind switch
        {
            LocalInferenceUnavailableReasonKind.NoNvidiaGpu => "LocalAi_Reason_NoNvidiaGpu",
            LocalInferenceUnavailableReasonKind.RuntimeUnavailable => "LocalAi_Reason_RuntimeUnavailable",
            LocalInferenceUnavailableReasonKind.UnknownModel => "LocalAi_Reason_UnknownModel",
            LocalInferenceUnavailableReasonKind.CudaCapabilityTooLow => "LocalAi_Reason_CudaCapabilityTooLow",
            _ => "LocalAi_Reason_Generic",
        };
}

public sealed record SetupLocalAiUseResult(
    string GatewayId, string ModelRef,
    SetupCompletionIntent CompletionIntent = SetupCompletionIntent.CustodianOnboarding);
public sealed class LocalAiSelectionRejectedException(string message) : InvalidOperationException(message);
/// <summary>The runtime returned after confirming cleanup. No uncertain publication remains.</summary>
public sealed class LocalAiStartFailedException(string message) : InvalidOperationException(message);

/// <summary>Window-scoped, single-use consent for the exact native installation reviewed by the user.</summary>
public sealed class LocalAiInstallAndUseIntent
{
    private int _consumed;
    public SetupLocalAiTarget Target { get; }
    public SetupLocalAiUseResult Expected { get; }
    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;

    public LocalAiInstallAndUseIntent(SetupLocalAiTarget target, string modelCatalogId, int requestedPort)
    {
        if (!target.IsNative || string.IsNullOrWhiteSpace(target.EndpointBinding) ||
            string.IsNullOrWhiteSpace(modelCatalogId) || requestedPort is < 0 or > 65535)
            throw new LocalAiSelectionRejectedException("Review the native Gateway and Local AI model before installing.");
        Target = target with { ModelCatalogId = modelCatalogId, RequestedLocalAiPort = requestedPort };
        Expected = new(target.GatewayId, $"llamacpp/{modelCatalogId}");
    }

    public void Consume()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new LocalAiSelectionRejectedException("This Local AI installation action has already been attempted.");
    }

    public void RequireInstalledSelection(LocalAiOnboardingSnapshot current)
    {
        if (!current.CanUse || current.Target != Target || current.ModelRef != Expected.ModelRef ||
            string.IsNullOrWhiteSpace(current.ReceiptIdentity))
            throw new LocalAiSelectionRejectedException("The reviewed Gateway or installed Local AI model changed. Review Local AI again.");
    }
}

/// <summary>
/// Owns the explicit local mutation independently of bounded discovery/connection requests.
/// An uncertain result retains both identities and may only be reconciled, never replayed.
/// </summary>
public sealed class LocalAiOnboardingUse(ISetupLocalAiHost host)
{
    private Task _mutation = Task.CompletedTask;
    public SetupLocalAiUseResult? Expected { get; private set; }

    public Task UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress = null)
    {
        if (Expected is not null)
            throw new InvalidOperationException("Reconcile the selected Local AI Gateway and model before another action.");
        Expected = new(selected.Target!.GatewayId, selected.ModelRef!);
        return _mutation = UseCoreAsync(() => host.UseAsync(selected, ct, progress));
    }

    public Task UseInstalledAsync(LocalAiInstallAndUseIntent intent, CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (Expected is not null || host is not INativeSetupLocalAiHost native)
            throw new LocalAiSelectionRejectedException("Reconnect the selected native Gateway before continuing Local AI setup.");
        intent.Consume();
        Expected = intent.Expected;
        return _mutation = UseCoreAsync(() => native.UseInstalledAsync(intent, ct, progress));
    }

    private async Task UseCoreAsync(Func<Task<SetupLocalAiUseResult>> action)
    {
        try
        {
            var used = await action();
            if (used != Expected)
                throw new InvalidDataException("Local AI returned a different Gateway or model.");
        }
        catch (Exception ex) when (ex is LocalAiSelectionRejectedException or LocalAiStartFailedException)
        {
            Expected = null;
            throw;
        }
    }

    public async Task DrainAsync()
    {
        try { await _mutation; }
        catch { /* The action's caller reports the outcome. The lifetime owner only drains cleanup. */ }
    }

    public static void RequireGateway(string? expectedGatewayId, string actualGatewayId)
    {
        if (expectedGatewayId is not null && expectedGatewayId != actualGatewayId)
            throw new LocalAiSelectionRejectedException("The selected Local AI Gateway changed. Return to the selected Gateway before verifying.");
    }
}

public static class LocalAiInstallationObservation
{
    public static Task<bool> InspectAsync(LocalAiResolvedInstall install, CancellationToken ct) =>
        new LocalAiInstallReconciler().InspectAsync(install, ct);
}

/// <summary>Observation never calls a runtime refresh (which may publish or withdraw a route).</summary>
public interface ISetupLocalAiHost
{
    OpenClaw.Connection.GatewayRegistrySnapshot BeginGatewaySetup();
    Task ReconcileGatewaySetupAsync(OpenClaw.Connection.GatewayRegistrySnapshot expectedOutput, string? completedGatewayId);
    Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct);
    Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct, IProgress<LocalAiSetupStage>? progress)
        => ObserveAsync(ct);
    Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct);
    Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct);
    Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct,
        IProgress<LocalAiSetupStage>? progress) => UseAsync(selected, ct);
}

/// <summary>Independent, generation-fenced page observation. No install or runtime mutation port is used here.</summary>
public sealed class LocalAiOnboardingObservation(ISetupLocalAiHost host) : IAsyncDisposable
{
    private CancellationTokenSource? _request;
    private readonly List<Task> _requests = [];
    private long _generation;
    private bool _closed;
    public LocalAiOnboardingSnapshot Snapshot { get; private set; } = new(LocalAiOnboardingState.Checking);
    public event Action? Changed;

    public Task RefreshAsync() => RefreshAsync(null);

    public Task RefreshAsync(IProgress<LocalAiSetupStage>? progress)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _request?.Cancel();
        _request?.Dispose();
        _request = new();
        var generation = ++_generation;
        Snapshot = new(LocalAiOnboardingState.Checking);
        Changed?.Invoke();
        var task = ObserveAsync(generation, _request.Token, progress);
        _requests.RemoveAll(request => request.IsCompleted);
        _requests.Add(task);
        return task;
    }

    private async Task ObserveAsync(long generation, CancellationToken ct, IProgress<LocalAiSetupStage>? progress)
    {
        LocalAiOnboardingSnapshot result;
        var finished = false;
        try
        {
            result = await host.ObserveAsync(ct, new SynchronousProgress<LocalAiSetupStage>(stage =>
            {
                if (!finished && !_closed && !ct.IsCancellationRequested && generation == _generation)
                    progress?.Report(stage);
            }));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Local AI observation failed ({0}).", ex.GetType().Name);
            result = new(LocalAiOnboardingState.Unknown);
        }
        finally { finished = true; }
        if (_closed || ct.IsCancellationRequested || generation != _generation)
            return;
        Snapshot = result;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        ++_generation;
        Changed = null;
        _request?.Cancel();
        try { await Task.WhenAll(_requests).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { /* Late results remain fenced even for an uncooperative probe. */ }
        finally { _request?.Dispose(); }
    }
}
