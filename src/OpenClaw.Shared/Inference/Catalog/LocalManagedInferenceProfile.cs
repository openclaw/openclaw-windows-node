namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>A paired runtime/model recipe and its host resource requirements.</summary>
public sealed record LocalManagedInferenceProfile(
    LlamaRuntimeVariant Runtime,
    LocalModelInfo Model,
    LocalInferenceRunProfile RunProfile)
{
    public GpuVendor GpuVendor => GpuVendor.Nvidia;
    public long RequiredGpuMemoryBytes => LocalInferenceEligibility.GetRequiredMemoryBytes(Model, RunProfile);
    public long MinimumSystemMemoryBytes => Model.MinimumSystemMemoryBytes;
    public long MinimumFreeDiskBytes => Model.MinimumFreeDiskBytes;
    public long RequiredDiskBytes(HostHardwareInfo hardware) =>
        hardware.LocalAiDiskSpace?.RequiredBytes(MinimumFreeDiskBytes) ?? MinimumFreeDiskBytes;

    internal LocalInferenceEligibilityFailureCode CheckHostResources(HostHardwareInfo hardware, bool installed)
    {
        if (MinimumSystemMemoryBytes > 0)
        {
            if (hardware.TotalPhysicalMemoryBytes is null)
                return LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete;
            if (hardware.TotalPhysicalMemoryBytes < MinimumSystemMemoryBytes)
                return LocalInferenceEligibilityFailureCode.InsufficientSystemMemory;
        }
        // Existing receipts do not need room to download their already-installed files.
        if (!installed && MinimumFreeDiskBytes > 0)
        {
            if (hardware.LocalAiDiskSpace is null)
                return LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete;
            // Installation keeps both a cached model and a compatibility copy next to
            // the runtime. Their reserves must add up when they share a volume.
            if (hardware.LocalAiDiskSpace.AvailableBytes < RequiredDiskBytes(hardware))
                return LocalInferenceEligibilityFailureCode.InsufficientDiskSpace;
        }
        return LocalInferenceEligibilityFailureCode.None;
    }

    internal bool Fits(HostHardwareInfo hardware, bool installed = false) =>
        CheckHostResources(hardware, installed) == LocalInferenceEligibilityFailureCode.None &&
        hardware.Gpus.Any(gpu => gpu.Vendor == GpuVendor &&
            LocalInferenceQualificationPolicy.HasRuntimePrerequisites(gpu, Runtime) &&
            LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(gpu) >= RequiredGpuMemoryBytes &&
            (installed || !LocalInferenceQualificationPolicy.IsNemotronBolt(Model) ||
                (LocalInferenceQualificationPolicy.IsQualifiedBoltGpu(gpu) &&
                 LocalInferenceQualificationPolicy.GetCurrentFreeMemoryBytes(hardware, gpu, Model) >= RequiredGpuMemoryBytes)));
}
