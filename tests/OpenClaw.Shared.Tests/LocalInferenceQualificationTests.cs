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
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.IntermediateContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.Q8_0, result.Plan?.Profile.KeyCachePrecision);
    }

    [Fact]
    public void Evaluate_UnsetModelChoosesHighestPriorityModelThatFitsTotalCapacity()
    {
        var cases = new[]
        {
            (TotalBytes: 34_190_458_880L, FreeBytes: 32_432_455_680L,
                ModelId: LocalModelCatalog.Qwen38_27BModelId,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0,
                RequiredBytes: 31_253_556_128L),
            (TotalBytes: 24 * GiB, FreeBytes: 24 * GiB,
                ModelId: LocalModelCatalog.Qwen38_27BModelId,
                ContextTokens: LocalModelCatalog.MinimumContextTokens,
                Precision: KvCachePrecision.F16,
                RequiredBytes: 25_322_810_272L),
            (TotalBytes: 16 * GiB, FreeBytes: 16 * GiB,
                ModelId: LocalModelCatalog.Qwen9BModelId,
                ContextTokens: LocalModelCatalog.ReducedContextTokens,
                Precision: KvCachePrecision.F16,
                RequiredBytes: 16_069_374_304L),
        };
        foreach (var testCase in cases)
        {
            GpuInfo gpu = Gpu("NVIDIA arbitrary adapter", "GPU-capacity", 1, 1) with
            {
                GpuVisibleMemoryBytes = testCase.TotalBytes,
                FreeGpuVisibleMemoryBytes = testCase.FreeBytes,
            };
            LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
                Hardware(RuntimeArchitecture.X64, gpu));

            Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
            Assert.Equal(testCase.ModelId, result.Plan?.Model.Id);
            Assert.Equal(testCase.ContextTokens, result.Plan?.Profile.ContextTokens);
            Assert.Equal(testCase.Precision, result.Plan?.Profile.KeyCachePrecision);
            Assert.Equal(testCase.RequiredBytes, result.RequiredTotalMemoryBytes);
            Assert.True(result.Plan?.Profile.ContextTokens >= LocalModelCatalog.MinimumContextTokens);
            Assert.Equal(LocalInferenceModelSelectionOrigin.Default, result.Plan?.ModelSelectionOrigin);
        }
    }

    [Fact]
    public void Evaluate_UnsetModelRejectsCapacityBelowSmallestCompleteRecipe()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-10", 10, 10)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
    }

    [Fact]
    public void Evaluate_ExplicitModelNeverDowngradesAndReportsExactCapacity()
    {
        var cases = new[]
        {
            (ModelId: LocalModelCatalog.Qwen38_27BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 31_253_556_128L),
            (ModelId: LocalModelCatalog.Qwen35BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 32_532_584_736L),
            (ModelId: LocalModelCatalog.Qwen27BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 31_895_889_024L),
            (ModelId: LocalModelCatalog.Qwen9BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.NativeContextTokens,
                Precision: KvCachePrecision.F16, RequiredBytes: 24_122_437_984L),
            (ModelId: LocalModelCatalog.Qwen35BModelId, TotalGiB: 16,
                Status: LocalInferenceEligibilityStatus.Unsupported,
                ContextTokens: LocalModelCatalog.MinimumContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 27_742_689_568L),
        };
        foreach (var testCase in cases)
        {
            LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
                Hardware(RuntimeArchitecture.X64, Gpu(
                    "NVIDIA arbitrary adapter", "GPU-explicit", testCase.TotalGiB, testCase.TotalGiB)),
                testCase.ModelId);

            Assert.Equal(testCase.Status, result.Status);
            Assert.Equal(testCase.ModelId, result.Plan?.Model.Id);
            Assert.Equal(testCase.ContextTokens, result.Plan?.Profile.ContextTokens);
            Assert.Equal(testCase.Precision, result.Plan?.Profile.KeyCachePrecision);
            Assert.Equal(testCase.RequiredBytes, result.RequiredTotalMemoryBytes);
            Assert.Equal(testCase.TotalGiB * GiB, result.DetectedTotalMemoryBytes);
            if (testCase.Status == LocalInferenceEligibilityStatus.Unsupported)
                Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        }
    }

    [Theory]
    [InlineData(LocalModelCatalog.Qwen35BModelId, 5_120, 512, 2_720, 272, 8)]
    [InlineData(LocalModelCatalog.Qwen38_27BModelId, 16_384, 1_024, 8_704, 544, 8)]
    [InlineData(LocalModelCatalog.Qwen27BModelId, 16_384, 1_024, 8_704, 544, 8)]
    [InlineData(LocalModelCatalog.Qwen9BModelId, 8_192, 1_024, 4_352, 544, 8)]
    public void GetRequiredMemoryBytes_IncludesRecipeKvCacheAndWorkspace(
        string modelId,
        long expectedF16CacheMiB,
        long expectedF16DraftCacheMiB,
        long expectedQ8CacheMiB,
        long expectedQ8DraftCacheMiB,
        long expectedQ8WorkspaceGiB)
    {
        LocalModelInfo model = LocalModelCatalog.Find(modelId)!;
        LocalInferenceRunProfile f16Profile = LocalModelCatalog.GetProfiles(model)[0];
        LocalInferenceRunProfile q8Profile = LocalModelCatalog.GetProfiles(model)[1];

        long f16Required = LocalInferenceEligibility.GetRequiredMemoryBytes(model, f16Profile);
        long q8Required = LocalInferenceEligibility.GetRequiredMemoryBytes(model, q8Profile);

        Assert.Equal(
            model.Weights.SizeBytes +
            (expectedF16CacheMiB + expectedF16DraftCacheMiB) * 1024 * 1024 +
            LocalModelCatalog.RuntimeWorkspaceReserveBytes,
            f16Required);
        Assert.Equal(
            model.Weights.SizeBytes +
            (expectedQ8CacheMiB + expectedQ8DraftCacheMiB) * 1024 * 1024 +
            expectedQ8WorkspaceGiB * GiB,
            q8Required);
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
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
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
