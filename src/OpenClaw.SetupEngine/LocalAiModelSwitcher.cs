using System.Collections.Immutable;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Changes the managed Local AI model in place: the new weights are acquired and verified
/// while the current model keeps serving, then the runtime withdraws the old route,
/// commits the new receipt, and restarts. A failed start restores the previous receipt.
/// Previously used weights stay in the shared Hugging Face cache. Only one switch runs at a
/// time per instance; App owns the single instance, so a concurrent call from another page
/// is rejected before it reads the receipt.
/// </summary>
public sealed class LocalAiModelSwitcher : ILocalAiModelSwitcher
{
    private static readonly HttpClient s_downloadClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly HttpClient s_runtimeClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly ILocalAiRuntime _runtime;
    private readonly string _localDataDirectory;
    private readonly IHuggingFaceModelAcquirer _modelAcquirer;
    private readonly ILlamaRuntimeAcquirer _runtimeAcquirer;
    private readonly Func<string, CancellationToken, Task<LocalCustomModelRecipe>> _resolveCustom;
    private int _switching;

    public LocalAiModelSwitcher(ILocalAiRuntime runtime, string localDataDirectory)
        : this(
            runtime,
            localDataDirectory,
            new HuggingFaceModelInstaller(s_downloadClient),
            new LlamaRuntimeInstaller(s_runtimeClient),
            new HuggingFaceModelReferenceResolver(s_downloadClient).ResolveAsync)
    {
    }

    internal LocalAiModelSwitcher(
        ILocalAiRuntime runtime,
        string localDataDirectory,
        IHuggingFaceModelAcquirer modelAcquirer,
        ILlamaRuntimeAcquirer runtimeAcquirer,
        Func<string, CancellationToken, Task<LocalCustomModelRecipe>> resolveCustom)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        _localDataDirectory = localDataDirectory;
        _modelAcquirer = modelAcquirer ?? throw new ArgumentNullException(nameof(modelAcquirer));
        _runtimeAcquirer = runtimeAcquirer ?? throw new ArgumentNullException(nameof(runtimeAcquirer));
        _resolveCustom = resolveCustom ?? throw new ArgumentNullException(nameof(resolveCustom));
    }

    private LocalAiManifestStore Store => new(new LocalAiPaths(_localDataDirectory));

    public bool IsSwitching => Volatile.Read(ref _switching) != 0;

    public event EventHandler? IsSwitchingChanged;

    public async Task<LocalInferencePlan?> LoadInstalledPlanAsync(CancellationToken ct)
    {
        try
        {
            LocalAiResolvedInstall? install = await Store.LoadAsync(ct).ConfigureAwait(false);
            return install is null ? null : LlamaServerRouterConfiguration.ResolveInstalledPlan(install);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task<LocalAiModelSwitchTarget> PrepareCatalogAsync(HostHardwareInfo hardware, string modelId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ct.ThrowIfCancellationRequested();
        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware, modelId);
        if (!eligibility.CanOffer || eligibility.Plan is null ||
            eligibility.SelectedGpu is not { StableId: { Length: > 0 } })
            throw new LocalAiModelSwitchException(Describe(eligibility));
        return Task.FromResult(ToTarget(eligibility, requiresForce: false));
    }

    public async Task<LocalAiModelSwitchTarget> PrepareCustomAsync(HostHardwareInfo hardware, string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        LocalCustomModelRecipe recipe = await _resolveCustom(reference, ct).ConfigureAwait(false);
        LocalModelInfo model = LocalCustomModel.ToModelInfo(recipe);
        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.EvaluateCustom(hardware, model);
        if (eligibility.CanOffer && eligibility.Plan is not null && eligibility.SelectedGpu is { StableId: { Length: > 0 } })
            return ToTarget(eligibility, requiresForce: false);
        if (eligibility.FailureCode == LocalInferenceEligibilityFailureCode.InsufficientGpuMemory &&
            eligibility.Plan is not null && eligibility.SelectedGpu is { StableId: { Length: > 0 } })
            return ToTarget(eligibility, requiresForce: true);
        throw new LocalAiModelSwitchException(Describe(eligibility));
    }

    public async Task<LocalAiRuntimeSnapshot> SwitchAsync(
        LocalAiModelSwitchTarget target,
        bool force,
        IProgress<LocalAiModelSwitchProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.RequiresForce && !force)
            throw new LocalAiModelSwitchException("Confirm that you want to switch to a model that may not fit.");

        if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0)
            throw new LocalAiModelSwitchException("Another model change is still finishing. Try again when it completes.");
        IsSwitchingChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            return await SwitchCoreAsync(target, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _switching, 0);
            IsSwitchingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<LocalAiRuntimeSnapshot> SwitchCoreAsync(
        LocalAiModelSwitchTarget target,
        IProgress<LocalAiModelSwitchProgress>? progress,
        CancellationToken ct)
    {
        LocalAiResolvedInstall current = await Store.LoadAsync(ct).ConfigureAwait(false)
            ?? throw new LocalAiModelSwitchException("Install Local AI before changing its model.");
        if (string.Equals(current.Manifest.ModelCatalogId, target.Plan.Model.Id, StringComparison.OrdinalIgnoreCase))
            throw new LocalAiModelSwitchException("This model is already in use.");

        LocalInferencePlan plan = target.Plan;
        // Reuse only the runtime this plan qualified. A retired pin of the same family can
        // predate the new recipe's minimum llama.cpp build, so it is upgraded side by side.
        bool reuseRuntime = string.Equals(current.Manifest.RuntimeId, plan.Runtime.Id, StringComparison.Ordinal);
        if (!reuseRuntime && (!LocalAiPathPolicy.TryResolve(
                _localDataDirectory,
                LlamaRuntimeInstaller.Component(plan.Runtime),
                out LocalAiSetupPaths setupPaths,
                out _) ||
            string.Equals(setupPaths.InstallDirectory, Path.GetDirectoryName(current.ExecutablePath),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new LocalAiModelSwitchException(
                "This model needs a different llama-server runtime that cannot be installed beside the current one.");
        }

        bool wasRunning = _runtime.Snapshot is
            { State: LocalAiRuntimeState.Healthy, Ownership: LocalAiOwnership.CompanionManaged };
        var paths = new LocalAiPaths(_localDataDirectory);
        LocalAiInstallManifest replacement = await AcquireAsync(paths, current, plan, target.GpuStableId, reuseRuntime, progress, ct)
            .ConfigureAwait(false);

        // From here on the change commits; cancellation would leave a half-switched route.
        progress?.Report(new(LocalAiModelSwitchPhase.StoppingRuntime));
        LocalAiRuntimeSnapshot stopped;
        try
        {
            stopped = await _runtime.ReplaceInstallAsync(replacement, CancellationToken.None).ConfigureAwait(false);
        }
        catch when (wasRunning)
        {
            try { await _runtime.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
            throw;
        }
        if (!wasRunning)
            return stopped;

        progress?.Report(new(LocalAiModelSwitchPhase.StartingRuntime));
        string? startFailure;
        try
        {
            LocalAiRuntimeSnapshot started = await _runtime.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
            if (started.State == LocalAiRuntimeState.Healthy)
                return started;
            startFailure = started.Detail;
        }
        catch (Exception ex)
        {
            // A thrown start (for example a lost Gateway connection after the route was
            // rebound) gets the same restoration as a returned failure; the new receipt is
            // already committed and must not be left in place.
            startFailure = ex.Message;
        }

        progress?.Report(new(LocalAiModelSwitchPhase.RollingBack));
        try
        {
            await _runtime.ReplaceInstallAsync(current.Manifest, CancellationToken.None).ConfigureAwait(false);
            await _runtime.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new LocalAiModelSwitchException(
                $"The new model could not start and the previous model could not be restored: {ex.Message}");
        }
        throw new LocalAiModelSwitchException(
            $"The new model could not start, so Local AI went back to the previous model. {startFailure}".TrimEnd());
    }

    private async Task<LocalAiInstallManifest> AcquireAsync(
        LocalAiPaths paths,
        LocalAiResolvedInstall current,
        LocalInferencePlan plan,
        string gpuId,
        bool reuseRuntime,
        IProgress<LocalAiModelSwitchProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new(LocalAiModelSwitchPhase.Resolving));
        var downloadProgress = new SynchronousProgress<HuggingFaceModelInstallProgress>(value =>
            progress?.Report(new(
                value.Phase == HuggingFaceModelInstallPhase.Verifying
                    ? LocalAiModelSwitchPhase.Verifying
                    : LocalAiModelSwitchPhase.Downloading,
                value.CompletedBytes,
                value.TotalBytes)));
        try
        {
            HuggingFaceModelInstallResult modelInstall = await _modelAcquirer.InstallAsync(
                    _localDataDirectory,
                    LlamaRuntimeInstaller.Component(plan.Runtime),
                    plan.Model,
                    downloadProgress,
                    ct)
                .ConfigureAwait(false);
            var additional = ImmutableArray.CreateBuilder<HuggingFaceAdditionalAssetInstallResult>();
            foreach (PinnedArtifact artifact in LocalModelCatalog.AdditionalArtifacts(plan.Model))
            {
                additional.Add(await _modelAcquirer
                    .InstallAdditionalAssetAsync(_localDataDirectory, artifact, downloadProgress, ct)
                    .ConfigureAwait(false));
            }

            string engineVersion = current.Manifest.EngineVersion;
            string runtimeId = current.Manifest.RuntimeId;
            string architecture = current.Manifest.Architecture;
            string executablePath = current.Manifest.ExecutablePath;
            ImmutableArray<LocalAiAssetReceipt> runtimeAssets = current.Manifest.RuntimeAssets;
            if (!reuseRuntime)
            {
                progress?.Report(new(LocalAiModelSwitchPhase.InstallingRuntime));
                LlamaRuntimeInstallResult runtimeInstall = await _runtimeAcquirer
                    .InstallAsync(_localDataDirectory, plan.Runtime, null, ct)
                    .ConfigureAwait(false);
                engineVersion = plan.Runtime.ReleaseTag;
                runtimeId = plan.Runtime.Id;
                architecture = LocalAiManifestFactory.ArchitectureName(plan.Runtime.Architecture);
                executablePath = Path.GetRelativePath(paths.RootDirectory, runtimeInstall.ExecutablePath);
                runtimeAssets = LocalAiManifestFactory.BuildRuntimeReceipts(plan.Runtime, runtimeInstall);
            }

            return LocalAiManifestFactory.Create(
                paths,
                plan,
                gpuId,
                current.Manifest.RequestedPort,
                engineVersion,
                runtimeId,
                architecture,
                executablePath,
                runtimeAssets,
                modelInstall,
                additional.ToImmutable());
        }
        catch (Exception ex) when (ex is HuggingFaceModelInstallException or LocalAiArtifactInstallException or
            HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new LocalAiModelSwitchException($"The model download failed: {ex.Message}");
        }
    }

    private static LocalAiModelSwitchTarget ToTarget(LocalInferenceEligibilityResult eligibility, bool requiresForce) =>
        new(
            eligibility.Plan!,
            eligibility.SelectedGpu!.StableId!,
            eligibility.SelectedGpu.Name,
            eligibility.RequiredTotalMemoryBytes,
            eligibility.DetectedTotalMemoryBytes,
            requiresForce);

    private static string Describe(LocalInferenceEligibilityResult eligibility) => eligibility.FailureCode switch
    {
        LocalInferenceEligibilityFailureCode.CatalogSelectionFailed => "Local AI needs a supported NVIDIA GPU and runtime on this PC.",
        LocalInferenceEligibilityFailureCode.DriverTooOld => "Update the NVIDIA driver to use this model.",
        LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow => "This GPU is too old for the Local AI runtime.",
        LocalInferenceEligibilityFailureCode.InsufficientGpuMemory => "This model needs more GPU memory than this PC has.",
        LocalInferenceEligibilityFailureCode.InsufficientSystemMemory => "This model needs more system memory than this PC has.",
        LocalInferenceEligibilityFailureCode.InsufficientDiskSpace => "There is not enough free disk space for this model.",
        LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete => "GPU details could not be read. Recheck and try again.",
        _ => "This model cannot run on this PC.",
    };
}
