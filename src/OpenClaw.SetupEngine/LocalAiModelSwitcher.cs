// <summary>
// Switches the managed Local AI model without discarding a serving installation. It validates and
// acquires replacement weights, commits a receipt only after a bounded inference proof, and restores
// the prior receipt if the new model cannot load. The app owns one instance and concurrent switches fail.
// Usage:
//   LocalAiModelSwitchTarget target = await switcher.PrepareCatalogAsync(hardware, modelId, ct);
//   LocalAiRuntimeSnapshot snapshot = await switcher.SwitchAsync(target, force: false, progress, ct);
//   // A successful snapshot proves the selected model is loaded by the managed router.
// </summary>

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Changes the managed Local AI model in place: the new weights are acquired and verified
/// while the current model keeps serving, then the runtime withdraws the old route,
/// commits the new receipt, and restarts. A running switch counts as successful only
/// after the new model answers one bounded request and the router reports that exact
/// model as Loaded; a failed or timed-out load, like a failed start, restores the
/// previous receipt. Previously used weights stay in the shared Hugging Face cache.
/// Only one switch runs at a time per instance; App owns the single instance, so a
/// concurrent call from another page is rejected before it reads the receipt.
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
    private readonly Func<ILlamaServerInferenceClient> _createInferenceClient;
    private readonly TimeSpan _loadTimeout;

    /// <summary>
    /// Whole seconds; read once at process start. Invalid or out-of-range values warn and keep the default.
    /// Kept under the log sanitizer's long-token redaction length so the name stays readable in logs.
    /// </summary>
    public const string LoadTimeoutEnvironmentVariable = "OPENCLAW_LOCAL_AI_SWITCH_LOAD_TIMEOUT";
    /// <summary>Matches setup's first-load budget, LocalAiConfig.InferenceTimeoutSeconds.</summary>
    internal const int DefaultLoadTimeoutSeconds = 600;
    internal const int MinimumLoadTimeoutSeconds = 30;
    internal const int MaximumLoadTimeoutSeconds = 3600;
    internal static readonly TimeSpan DefaultLoadTimeout =
        ResolveLoadTimeout(Environment.GetEnvironmentVariable(LoadTimeoutEnvironmentVariable));

    public LocalAiModelSwitcher(ILocalAiRuntime runtime, string localDataDirectory, Func<string?> getApiKey)
        : this(
            runtime,
            localDataDirectory,
            new HuggingFaceModelInstaller(s_downloadClient),
            new LlamaRuntimeInstaller(s_runtimeClient),
            new HuggingFaceModelReferenceResolver(s_downloadClient).ResolveAsync,
            CreateInferenceClient(getApiKey),
            DefaultLoadTimeout)
    {
    }

    internal LocalAiModelSwitcher(
        ILocalAiRuntime runtime,
        string localDataDirectory,
        IHuggingFaceModelAcquirer modelAcquirer,
        ILlamaRuntimeAcquirer runtimeAcquirer,
        Func<string, CancellationToken, Task<LocalCustomModelRecipe>> resolveCustom,
        Func<ILlamaServerInferenceClient> createInferenceClient,
        TimeSpan loadTimeout)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        _localDataDirectory = localDataDirectory;
        _modelAcquirer = modelAcquirer ?? throw new ArgumentNullException(nameof(modelAcquirer));
        _runtimeAcquirer = runtimeAcquirer ?? throw new ArgumentNullException(nameof(runtimeAcquirer));
        _resolveCustom = resolveCustom ?? throw new ArgumentNullException(nameof(resolveCustom));
        _createInferenceClient = createInferenceClient ?? throw new ArgumentNullException(nameof(createInferenceClient));
        if (loadTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(loadTimeout), "The model load timeout must be positive.");
        _loadTimeout = loadTimeout;
    }

    /// <summary>
    /// A null key means the router was launched without auth (no native binding), so no
    /// Authorization header. The null check must stay outside the delegate:
    /// LlamaServerInferenceClient throws when its delegate returns null.
    /// </summary>
    internal static Func<ILlamaServerInferenceClient> CreateInferenceClient(Func<string?> getApiKey)
    {
        ArgumentNullException.ThrowIfNull(getApiKey);
        return () =>
        {
            string? key = getApiKey();
            return new LlamaServerInferenceClient(key is null ? null : () => key);
        };
    }

    internal static TimeSpan ResolveLoadTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromSeconds(DefaultLoadTimeoutSeconds);
        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) &&
            seconds is >= MinimumLoadTimeoutSeconds and <= MaximumLoadTimeoutSeconds)
            return TimeSpan.FromSeconds(seconds);
        Trace.TraceWarning(
            $"Ignoring {LoadTimeoutEnvironmentVariable}='{raw}': expected whole seconds from " +
            $"{MinimumLoadTimeoutSeconds} to {MaximumLoadTimeoutSeconds}. " +
            $"Using the default {DefaultLoadTimeoutSeconds}s.");
        return TimeSpan.FromSeconds(DefaultLoadTimeoutSeconds);
    }

    private LocalAiManifestStore Store => new(new LocalAiPaths(_localDataDirectory));

    /// <summary>Reports whether this instance currently owns an in-progress model switch.</summary>
    public bool IsSwitching => Volatile.Read(ref _switching) != 0;

    public event EventHandler? IsSwitchingChanged;

    /// <summary>Loads the currently installed plan when its receipt still resolves to a catalog plan.</summary>
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

    /// <summary>Prepares a catalog model target for switching on the detected hardware.</summary>
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

    /// <summary>Resolves and prepares a user-supplied Hugging Face model target for switching.</summary>
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

    /// <summary>Switches to a prepared target and restores the prior installation if the switch fails.</summary>
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
            if (started.State != LocalAiRuntimeState.Healthy)
            {
                startFailure = started.Detail;
            }
            else
            {
                progress?.Report(new(LocalAiModelSwitchPhase.LoadingModel));
                (LocalAiRuntimeSnapshot? loaded, startFailure) =
                    await VerifyModelLoadsAsync(started.Endpoint, replacement.ModelAlias).ConfigureAwait(false);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            // A thrown start or load check (for example a lost Gateway connection after the
            // route was rebound, or llama-server's own load failure text) gets the same
            // restoration as a returned failure; the new receipt is already committed and
            // must not be left in place.
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

    /// <summary>
    /// Router health accepts an unloaded preset model (load-on-startup = false), so only a real
    /// request proves the new weights load. Mirrors setup's VerifyLocalAiInferenceStep. Runs after
    /// the commit, so it is bounded by the load timeout only, never by the caller's token.
    /// </summary>
    private async Task<(LocalAiRuntimeSnapshot? Loaded, string? Failure)> VerifyModelLoadsAsync(Uri endpoint, string modelAlias)
    {
        using ILlamaServerInferenceClient client = _createInferenceClient();
        using var timeout = new CancellationTokenSource(_loadTimeout);
        try
        {
            await client.VerifyAsync(endpoint, modelAlias, timeout.Token).ConfigureAwait(false);
            LocalAiRuntimeSnapshot loaded = await _runtime.RefreshAsync(timeout.Token).ConfigureAwait(false);
            if (loaded is { State: LocalAiRuntimeState.Healthy, Ownership: LocalAiOwnership.CompanionManaged } &&
                loaded.ModelEvidence.State == LocalAiModelAvailabilityState.Loaded &&
                string.Equals(loaded.ModelEvidence.ServerModelId, modelAlias, StringComparison.Ordinal))
                return (loaded, null);
            return (null, "llama-server answered but did not report the new model as loaded.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return (null, $"It did not finish loading within {(int)_loadTimeout.TotalSeconds} seconds.");
        }
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
