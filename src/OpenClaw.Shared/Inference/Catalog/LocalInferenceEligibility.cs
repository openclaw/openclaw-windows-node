namespace OpenClaw.Shared.Inference.Catalog;

public enum LocalInferenceEligibilityStatus
{
    Eligible = 0,
    EligibleButBusy = 1,
    Unsupported = 2,
}

public enum LocalInferenceEligibilityFailureCode
{
    None = 0,
    CatalogSelectionFailed = 1,
    HardwareFactsIncomplete = 2,
    InsufficientGpuMemory = 3,
    DriverTooOld = 4,
    CudaCapabilityTooLow = 5,
    InsufficientSystemMemory = 6,
    InsufficientDiskSpace = 7,
    ModelNotQualifiedForDevice = 8,
}

public sealed record LocalInferenceEligibilityResult(
    LocalInferenceEligibilityStatus Status,
    LocalInferenceEligibilityFailureCode FailureCode,
    LocalInferenceSelectionFailureCode SelectionFailureCode,
    LocalInferencePlan? Plan,
    GpuInfo? SelectedGpu,
    long RequiredTotalMemoryBytes,
    long? DetectedTotalMemoryBytes,
    long RequiredFreeMemoryBytes,
    long? AvailableFreeMemoryBytes,
    long RequiredSystemMemoryBytes = 0,
    long? DetectedSystemMemoryBytes = null,
    long RequiredDiskBytes = 0,
    long? AvailableDiskBytes = null)
{
    public bool CanInstall => Status is
        LocalInferenceEligibilityStatus.Eligible or
        LocalInferenceEligibilityStatus.EligibleButBusy;

    /// <summary>Bolt requires a currently fitting 128K-or-larger profile before fresh selection.</summary>
    public bool CanOffer => CanInstall &&
        (Plan is not { } plan || !LocalInferenceQualificationPolicy.IsNemotronBolt(plan.Model) ||
            (Status == LocalInferenceEligibilityStatus.Eligible &&
             plan.Profile.ContextTokens >= LocalModelCatalog.ReducedContextTokens));
}

/// <summary>
/// Applies the pinned model capacity, driver, and CUDA guardrails after catalog
/// selection. Total GPU memory is stable capacity. Free GPU memory is launch
/// readiness and never changes the selected model automatically.
/// </summary>
public static class LocalInferenceEligibility
{
    public const long RuntimeWorkspaceReserveBytes = LocalModelCatalog.RuntimeWorkspaceReserveBytes;
    public static Version MinimumNvidiaDriverVersion { get; } = new(615, 0);

    public static long GetRequiredMemoryBytes(
        LocalModelInfo model,
        LocalInferenceRunProfile profile) =>
        LocalInferenceQualificationPolicy.GetRequiredMemoryBytes(model, profile);

    /// <summary>
    /// Device eligibility for deciding whether Local AI stays available, given the model
    /// already configured on this machine.
    /// </summary>
    /// <remarks>
    /// A SKU with no recommended default is a statement about what to install by default,
    /// not about what the device can run. Gating availability purely on the default pick
    /// would switch Local AI off on a setup rerun for a machine that already has a working
    /// configured model, so once a model is configured this reports on that model: a
    /// selection that still passes the full capacity fit-test is retained, and one that does
    /// not carries its own failure (unknown model, or the model name with its required and
    /// detected memory) rather than the SKU's generic no-recommendation reason. With no model
    /// configured, the device result stands and fresh setup is unchanged.
    /// </remarks>
    public static LocalInferenceEligibilityResult EvaluateForConfiguredAvailability(
        HostHardwareInfo hardware,
        string? configuredModelId,
        bool hasInstalledReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        LocalInferenceEligibilityResult device = Evaluate(hardware);
        if (!device.CanInstall && hasInstalledReceipt && !string.IsNullOrWhiteSpace(configuredModelId))
        {
            LocalInferenceEligibilityResult installed = EvaluateInstalled(hardware, configuredModelId);
            if (installed.CanInstall)
                return installed;
        }
        if (device.CanInstall ||
            device.SelectionFailureCode != LocalInferenceSelectionFailureCode.NotRecommendedForSku ||
            string.IsNullOrWhiteSpace(configuredModelId))
        {
            return device;
        }

        return Evaluate(hardware, configuredModelId);
    }

    public static LocalInferenceEligibilityResult Evaluate(
        HostHardwareInfo hardware,
        string? requestedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        return Evaluate(hardware, LocalInferenceSelector.Select(hardware, requestedModelId));
    }

    /// <summary>
    /// Evaluates the explicit model recorded by an existing installation receipt,
    /// including a retired model that is no longer offered for fresh selection.
    /// </summary>
    public static LocalInferenceEligibilityResult EvaluateInstalled(
        HostHardwareInfo hardware,
        string installedModelId)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        return Evaluate(hardware, LocalInferenceSelector.SelectInstalled(hardware, installedModelId), installed: true);
    }

    /// <summary>Rechecks an already-qualified receipt plan without selecting a different profile or adapter.</summary>
    public static LocalInferenceEligibilityResult EvaluateRecordedProfile(
        HostHardwareInfo hardware,
        LocalInferencePlan recordedPlan)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(recordedPlan);
        if (hardware.CpuArchitecture != recordedPlan.Runtime.Architecture)
            return Unsupported(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed,
                LocalInferenceSelectionFailureCode.RuntimeUnavailable);
        return Evaluate(hardware, LocalInferenceSelectionResult.Selected(recordedPlan), installed: true);
    }

    private static LocalInferenceEligibilityResult Evaluate(
        HostHardwareInfo hardware,
        LocalInferenceSelectionResult selection,
        bool installed = false)
    {
        if (!selection.IsSelected || selection.Plan is null)
        {
            return Unsupported(
                LocalInferenceEligibilityFailureCode.CatalogSelectionFailed,
                selection.FailureCode);
        }

        LocalInferencePlan plan = selection.Plan;
        long requiredMemoryBytes = GetRequiredMemoryBytes(plan.Model, plan.Profile);
        // A plan bound to one adapter (an RTX Spark SKU recipe) must be assessed only
        // against that adapter. Ranking every NVIDIA GPU here would let the recipe
        // chosen for the Spark be reported against, and then launched on, a different
        // GPU on a mixed host.
        IEnumerable<GpuInfo> candidateGpus = plan.BoundGpuStableId is { Length: > 0 } boundId
            ? hardware.NvidiaGpus.Where(gpu => string.Equals(gpu.StableId, boundId, StringComparison.Ordinal))
            : hardware.NvidiaGpus;
        CandidateAssessment? selected = candidateGpus
            .Select(gpu => Assess(hardware, gpu, plan, requiredMemoryBytes))
            .OrderBy(candidate => StatusRank(candidate.Status))
            .ThenBy(candidate => DefinitivenessRank(candidate.FailureCode))
            .ThenByDescending(candidate => candidate.FreeMemoryBytes.HasValue)
            .ThenByDescending(candidate => candidate.FreeMemoryBytes ?? long.MinValue)
            .ThenByDescending(candidate => candidate.TotalMemoryBytes)
            .ThenBy(candidate => candidate.Gpu.StableId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Gpu.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is null)
            return Unsupported(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete);

        var managedProfile = new LocalManagedInferenceProfile(plan.Runtime, plan.Model, plan.Profile);
        LocalInferenceEligibilityFailureCode resourceFailure = managedProfile.CheckHostResources(hardware, installed);
        bool resourcesBlocked = selected.Status != LocalInferenceEligibilityStatus.Unsupported &&
            resourceFailure != LocalInferenceEligibilityFailureCode.None;

        return new LocalInferenceEligibilityResult(
            resourcesBlocked ? LocalInferenceEligibilityStatus.Unsupported : selected.Status,
            resourcesBlocked ? resourceFailure : selected.FailureCode,
            LocalInferenceSelectionFailureCode.None,
            plan,
            selected.Gpu,
            requiredMemoryBytes,
            selected.TotalMemoryBytes > 0 ? selected.TotalMemoryBytes : null,
            requiredMemoryBytes,
            selected.FreeMemoryBytes,
            managedProfile.MinimumSystemMemoryBytes,
            hardware.TotalPhysicalMemoryBytes,
            installed ? 0 : managedProfile.RequiredDiskBytes(hardware),
            hardware.LocalAiDiskSpace?.AvailableBytes);
    }

    private static LocalInferenceEligibilityResult Unsupported(
        LocalInferenceEligibilityFailureCode failureCode,
        LocalInferenceSelectionFailureCode selectionFailureCode = LocalInferenceSelectionFailureCode.None,
        GpuInfo? selectedGpu = null) =>
        new(
            LocalInferenceEligibilityStatus.Unsupported,
            failureCode,
            selectionFailureCode,
            null,
            selectedGpu,
            0,
            selectedGpu is null ? null : LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(selectedGpu),
            0,
            selectedGpu is null ? null : LocalInferenceQualificationPolicy.GetEffectiveFreeMemoryBytes(selectedGpu));

    private static CandidateAssessment Assess(
        HostHardwareInfo hardware,
        GpuInfo gpu,
        LocalInferencePlan plan,
        long requiredMemoryBytes)
    {
        LlamaRuntimeVariant runtime = plan.Runtime;
        long totalMemoryBytes = LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(gpu);
        long? freeMemoryBytes = LocalInferenceQualificationPolicy.GetCurrentFreeMemoryBytes(hardware, gpu, plan.Model);
        bool isBolt = LocalInferenceQualificationPolicy.IsNemotronBolt(plan.Model);
        if (!LocalInferenceQualificationPolicy.HasCompleteFacts(gpu) ||
            (isBolt && (freeMemoryBytes is null || string.IsNullOrWhiteSpace(gpu.Name) ||
                string.Equals(gpu.Name, CudaHostHardwareProbe.UnidentifiedGpuName, StringComparison.Ordinal))))
        {
            return UnsupportedCandidate(
                gpu,
                LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
                totalMemoryBytes,
                freeMemoryBytes);
        }

        if (isBolt && !LocalInferenceQualificationPolicy.IsQualifiedBoltGpu(gpu))
        {
            return UnsupportedCandidate(
                gpu,
                LocalInferenceEligibilityFailureCode.ModelNotQualifiedForDevice,
                totalMemoryBytes,
                freeMemoryBytes);
        }

        if (gpu.CudaMajorVersion < runtime.CudaVersion.Major)
        {
            return UnsupportedCandidate(
                gpu,
                LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow,
                totalMemoryBytes,
                freeMemoryBytes);
        }

        if (totalMemoryBytes < requiredMemoryBytes)
        {
            return UnsupportedCandidate(
                gpu,
                LocalInferenceEligibilityFailureCode.InsufficientGpuMemory,
                totalMemoryBytes,
                freeMemoryBytes);
        }

        LocalInferenceEligibilityStatus status =
            freeMemoryBytes is not null && freeMemoryBytes < requiredMemoryBytes
                ? LocalInferenceEligibilityStatus.EligibleButBusy
                : LocalInferenceEligibilityStatus.Eligible;
        return new CandidateAssessment(
            gpu,
            status,
            LocalInferenceEligibilityFailureCode.None,
            totalMemoryBytes,
            freeMemoryBytes);
    }

    private static CandidateAssessment UnsupportedCandidate(
        GpuInfo gpu,
        LocalInferenceEligibilityFailureCode failureCode,
        long totalMemoryBytes,
        long? freeMemoryBytes) =>
        new(
            gpu,
            LocalInferenceEligibilityStatus.Unsupported,
            failureCode,
            totalMemoryBytes,
            freeMemoryBytes);

    private static int StatusRank(LocalInferenceEligibilityStatus status) => status switch
    {
        LocalInferenceEligibilityStatus.Eligible => 0,
        LocalInferenceEligibilityStatus.EligibleButBusy => 1,
        _ => 2,
    };

    /// <summary>
    /// Ranks an inconclusive candidate ahead of a definitively incompatible one.
    /// A GPU whose facts could not be read might still work, so reporting the
    /// retryable state keeps recheck available instead of showing a permanent
    /// "this device cannot run Local AI" verdict from a different adapter.
    /// </summary>
    private static int DefinitivenessRank(LocalInferenceEligibilityFailureCode failureCode) =>
        failureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete ? 0 : 1;

    private sealed record CandidateAssessment(
        GpuInfo Gpu,
        LocalInferenceEligibilityStatus Status,
        LocalInferenceEligibilityFailureCode FailureCode,
        long TotalMemoryBytes,
        long? FreeMemoryBytes);
}
