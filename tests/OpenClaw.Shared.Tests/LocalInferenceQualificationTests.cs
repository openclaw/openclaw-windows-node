using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class LocalInferenceQualificationTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX Spark N1X", LlamaRuntimeCatalog.X64RuntimeId)]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA GeForce RTX 5090", LlamaRuntimeCatalog.Arm64RuntimeId)]
    public void Evaluate_RoutesRuntimeByArchitectureWithoutGpuSkuPairing(
        RuntimeArchitecture architecture,
        string gpuName,
        string expectedRuntimeId)
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(architecture, Gpu(gpuName, "GPU-generic", totalGiB: 32, freeGiB: 32)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(expectedRuntimeId, result.Plan?.Runtime.Id);
    }

    [Fact]
    public void Evaluate_UnsetModelChoosesLargestModelThatFitsTotalCapacity()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-24", 24, 24)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Default, result.Plan?.ModelSelectionOrigin);
    }

    [Fact]
    public void Evaluate_UnsetModelRejectsCapacityBelowSmallestCompleteRecipe()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
    }

    [Fact]
    public void Evaluate_ExplicitModelNeverDowngradesAndReportsExactCapacity()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16)),
            LocalModelCatalog.Qwen35BModelId);

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.Default.Weights.SizeBytes + 13 * GiB, result.RequiredTotalMemoryBytes);
        Assert.Equal(16 * GiB, result.DetectedTotalMemoryBytes);
    }

    [Theory]
    [InlineData(LocalModelCatalog.Qwen35BModelId, 5)]
    [InlineData(LocalModelCatalog.Qwen27BModelId, 16)]
    [InlineData(LocalModelCatalog.Qwen9BModelId, 8)]
    public void GetRequiredMemoryBytes_IncludesRecipeKvCacheAndWorkspace(
        string modelId,
        long expectedCacheGiB)
    {
        LocalModelInfo model = LocalModelCatalog.Find(modelId)!;

        long required = LocalInferenceEligibility.GetRequiredMemoryBytes(model);

        Assert.Equal(model.Weights.SizeBytes + (expectedCacheGiB + 8) * GiB, required);
    }

    [Fact]
    public void Evaluate_RanksEligibleBeforeBusyAndUnsupportedAdapters()
    {
        GpuInfo unsupported = Gpu("NVIDIA old", "GPU-old", 48, 48) with { DriverVersion = "614.99" };
        GpuInfo busy = Gpu("NVIDIA busy", "GPU-busy", 32, 1);
        GpuInfo eligible = Gpu("NVIDIA ready", "GPU-ready", 24, 24);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, unsupported, busy, eligible),
            LocalModelCatalog.Qwen9BModelId);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-ready", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_CountsSharedMemoryForAnyNvidiaGpuAndUnknownSharedFreeIsNotBusy()
    {
        GpuInfo gpu = Gpu("NVIDIA generic unified memory", "GPU-shared", 8, 8) with
        {
            SharedGpuMemoryBytes = 16 * GiB,
            FreeSharedGpuMemoryBytes = null,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.Arm64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
        Assert.Equal(24 * GiB, result.DetectedTotalMemoryBytes);
        Assert.Null(result.AvailableFreeMemoryBytes);
    }

    [Fact]
    public void Evaluate_RanksEligibleAdaptersByFreeThenTotalThenUuid()
    {
        GpuInfo moreTotal = Gpu("NVIDIA total", "GPU-z", 48, 24);
        GpuInfo moreFree = Gpu("NVIDIA free", "GPU-b", 32, 26);
        GpuInfo sameFreeAndTotalLowerUuid = Gpu("NVIDIA tie", "GPU-a", 32, 26);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, moreTotal, moreFree, sameFreeAndTotalLowerUuid),
            LocalModelCatalog.Qwen9BModelId);

        Assert.Equal("GPU-a", result.SelectedGpu?.StableId);
    }

    [Theory]
    [InlineData(null, "616.30", 13, LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)]
    [InlineData("GPU-old", "614.99", 13, LocalInferenceEligibilityFailureCode.DriverTooOld)]
    [InlineData("GPU-cuda", "616.30", 12, LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow)]
    public void Evaluate_RequiresStableUuidDriverAndCuda(
        string? stableId,
        string driverVersion,
        int cudaMajor,
        LocalInferenceEligibilityFailureCode expectedFailure)
    {
        GpuInfo gpu = Gpu("NVIDIA arbitrary", stableId, 32, 32) with
        {
            DriverVersion = driverVersion,
            CudaMajorVersion = cudaMajor,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(expectedFailure, result.FailureCode);
    }

    [Fact]
    public void Evaluate_ReportsNoNvidiaGpu()
    {
        var hardware = new HostHardwareInfo(
            RuntimeArchitecture.X64,
            null,
            null,
            [new GpuInfo(GpuVendor.Amd, "AMD GPU")],
            false);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceSelectionFailureCode.NoNvidiaGpu, result.SelectionFailureCode);
    }

    [Fact]
    public void Probe_JoinsDxgiByNormalizedExactName()
    {
        GpuInfo gpu = ProbeWithDxgi(
            "NVIDIA   Generic GPU",
            new Dictionary<string, DxgiGpuMemoryInfo>
            {
                ["NVIDIA Generic GPU"] = new(10 * GiB, 9 * GiB),
            });

        Assert.Equal(10 * GiB, gpu.SharedGpuMemoryBytes);
    }

    [Theory]
    [InlineData("NVIDIA Generic GPU (Device 1)", "NVIDIA Generic GPU")]
    [InlineData("NVIDIA Generic GPU", "NVIDIA Generic GPU (Device 1)")]
    public void Probe_JoinsDxgiByUniqueBidirectionalContainment(string nvmlName, string dxgiName)
    {
        GpuInfo gpu = ProbeWithDxgi(
            nvmlName,
            new Dictionary<string, DxgiGpuMemoryInfo>
            {
                [dxgiName] = new(10 * GiB, null),
            });

        Assert.Equal(10 * GiB, gpu.SharedGpuMemoryBytes);
    }

    [Fact]
    public void Probe_DoesNotJoinAmbiguousDxgiContainmentMatches()
    {
        GpuInfo gpu = ProbeWithDxgi(
            "NVIDIA Generic GPU (Device 1)",
            new Dictionary<string, DxgiGpuMemoryInfo>
            {
                ["NVIDIA Generic GPU"] = new(10 * GiB, null),
                ["Generic GPU (Device 1)"] = new(12 * GiB, null),
            });

        Assert.Null(gpu.SharedGpuMemoryBytes);
    }

    [Fact]
    public void Probe_DoesNotJoinOneDxgiBudgetToDuplicateNvmlNames()
    {
        var probe = new NvmlHostHardwareProbe(
            () => new NvmlProbeResult(
                [
                    new NvmlGpuSnapshot("NVIDIA Duplicate GPU", "GPU-a", 8UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024),
                    new NvmlGpuSnapshot("NVIDIA  Duplicate GPU", "GPU-b", 8UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024),
                ],
                "616.30",
                13),
            () => null,
            () => new Dictionary<string, DxgiGpuMemoryInfo>
            {
                ["NVIDIA Duplicate GPU"] = new(10 * GiB, 9 * GiB),
            },
            RuntimeArchitecture.X64);

        GpuInfo[] gpus = probe.Probe().NvidiaGpus.ToArray();

        Assert.Equal(2, gpus.Length);
        Assert.All(gpus, gpu => Assert.Null(gpu.SharedGpuMemoryBytes));
    }

    [Fact]
    public void DxgiCapture_OmitsDuplicateNormalizedAdapterNames()
    {
        var results = new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase);
        var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DxgiGpuMemoryProbe.AddMemoryByName(
            results,
            ambiguousNames,
            "NVIDIA Duplicate GPU",
            new DxgiGpuMemoryInfo(10 * GiB, 9 * GiB));
        DxgiGpuMemoryProbe.AddMemoryByName(
            results,
            ambiguousNames,
            "NVIDIA   Duplicate GPU",
            new DxgiGpuMemoryInfo(12 * GiB, 11 * GiB));

        Assert.Empty(results);
        Assert.Contains("NVIDIA Duplicate GPU", ambiguousNames);
    }

    private static HostHardwareInfo Hardware(RuntimeArchitecture architecture, params GpuInfo[] gpus) =>
        new(architecture, 64 * GiB, 48 * GiB, gpus, false);

    private static GpuInfo Gpu(
        string name,
        string? stableId,
        long totalGiB,
        long freeGiB) =>
        new(
            GpuVendor.Nvidia,
            name,
            totalGiB * GiB,
            freeGiB * GiB,
            DriverVersion: "616.30",
            CudaMajorVersion: 13,
            StableId: stableId);

    private static GpuInfo ProbeWithDxgi(
        string nvmlName,
        IReadOnlyDictionary<string, DxgiGpuMemoryInfo> dxgiMemory)
    {
        var probe = new NvmlHostHardwareProbe(
            () => new NvmlProbeResult(
                [new NvmlGpuSnapshot(nvmlName, "GPU-probe", 8UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024)],
                "616.30",
                13),
            () => null,
            () => dxgiMemory,
            RuntimeArchitecture.X64);

        return Assert.Single(probe.Probe().NvidiaGpus);
    }
}
