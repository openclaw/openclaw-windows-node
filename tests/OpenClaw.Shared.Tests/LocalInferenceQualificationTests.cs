using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using System.Reflection;
using System.Runtime.InteropServices;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class LocalInferenceQualificationTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    [Fact]
    public void CudaVisibleDevicesSelector_FormatsDriverUuidLikeNvml()
    {
        byte[] cudaUuid = Convert.FromHexString("CC66BCA6B5FFDD70995CD81A07ADD980");

        Assert.Equal(
            "GPU-cc66bca6-b5ff-dd70-995c-d81a07add980",
            NvcudaDriver.ToCudaVisibleDevicesSelector(cudaUuid));
    }

    [Fact]
    public void CudaProbe_LoadsNativeDriverOnlyFromSystem32()
    {
        MethodInfo[] imports = typeof(NvcudaDriver).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is { } import &&
                import.Value.Contains("nvcuda", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(imports);
        Assert.All(imports, method =>
        {
            DefaultDllImportSearchPathsAttribute attribute = Assert.IsType<DefaultDllImportSearchPathsAttribute>(
                method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>());
            Assert.Equal(DllImportSearchPath.System32, attribute.Paths);
        });
    }

    [Fact]
    public void CudaProbe_MissingUuidKeepsDetectedNvidiaGpuAsIncompleteFacts()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Uuid = null };

        HostHardwareInfo hardware = Probe(reader);

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.StableId);
        Assert.Equal(32 * GiB, gpu.GpuVisibleMemoryBytes);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.Equal(LocalInferenceSelectionFailureCode.None, result.SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_FailingUuidEntryPointKeepsDetectedNvidiaGpuAsIncompleteFacts()
    {
        var reader = new StubCudaDeviceReader
        {
            DeviceCount = 1,
            UuidFailure = () => new EntryPointNotFoundException("cuDeviceGetUuid_v2"),
        };

        HostHardwareInfo hardware = Probe(reader);

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.StableId);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.NotEqual(LocalInferenceSelectionFailureCode.NoNvidiaGpu, result.SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_KeepsHealthyGpuWhenAnotherDeviceFailsItsUuidLookup()
    {
        var reader = new StubCudaDeviceReader
        {
            DeviceCount = 2,
            UuidByDevice = device => device == 0 ? null : "GPU-healthy",
        };

        HostHardwareInfo hardware = Probe(reader);

        Assert.Equal(2, hardware.Gpus.Count);
        Assert.Null(hardware.Gpus[0].StableId);
        Assert.Equal("GPU-healthy", hardware.Gpus[1].StableId);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-healthy", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void CudaProbe_FailedDeviceCountKeepsRetryableNvidiaFactsInsteadOfNoGpu()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = null };

        HostHardwareInfo hardware = Probe(reader);

        Assert.True(hardware.HasNvidiaGpu);
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
    }

    [Fact]
    public void CudaProbe_FailedDeviceHandleKeepsRetryableNvidiaFacts()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, DeviceHandle = null };

        HostHardwareInfo hardware = Probe(reader);

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.StableId);
        Assert.Equal(
            LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
            LocalInferenceEligibility.Evaluate(hardware).FailureCode);
    }

    [Fact]
    public void CudaProbe_MissingNameStillReportsTheDetectedNvidiaDevice()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Name = null };

        GpuInfo gpu = Assert.Single(Probe(reader).Gpus);

        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.False(string.IsNullOrWhiteSpace(gpu.Name));
        Assert.Equal("GPU-stub", gpu.StableId);
    }

    [Fact]
    public void CudaProbe_AbsentDriverIsDefinitiveNoNvidiaGpu() =>
        AssertDefinitiveNoNvidiaGpu(CudaDriverAvailability.Absent);

    [Fact]
    public void CudaProbe_DriverReportingNoDeviceIsDefinitiveNoNvidiaGpu() =>
        AssertDefinitiveNoNvidiaGpu(CudaDriverAvailability.NoDevice);

    private static void AssertDefinitiveNoNvidiaGpu(CudaDriverAvailability availability)
    {
        var reader = new StubCudaDeviceReader { Availability = availability };

        HostHardwareInfo hardware = Probe(reader);

        Assert.False(hardware.HasNvidiaGpu);
        Assert.Equal(
            LocalInferenceSelectionFailureCode.NoNvidiaGpu,
            LocalInferenceEligibility.Evaluate(hardware).SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_DriverInitializationFailureStaysRetryableRatherThanNoGpu()
    {
        // A driver/runtime mismatch fails cuInit while NVIDIA hardware is still
        // present, so presence is unknown rather than disproven.
        var reader = new StubCudaDeviceReader { Availability = CudaDriverAvailability.Failed };

        HostHardwareInfo hardware = Probe(reader);

        Assert.True(hardware.HasNvidiaGpu);
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.NotEqual(LocalInferenceSelectionFailureCode.NoNvidiaGpu, result.SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_ZeroReportedDevicesIsAlsoDefinitiveNoNvidiaGpu()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 0 };

        Assert.Equal(
            LocalInferenceSelectionFailureCode.NoNvidiaGpu,
            LocalInferenceEligibility.Evaluate(Probe(reader)).SelectionFailureCode);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64)]
    [InlineData(RuntimeArchitecture.Arm64)]
    public void CudaProbe_Qualifies48GbRtxSparkWithoutCappingAtIts16GbCarveout(
        RuntimeArchitecture architecture)
    {
        var reader = new StubCudaDeviceReader
        {
            Name = "NVIDIA RTX Spark N1X",
            Memory = (46_114L * MiB, 46_332L * MiB),
        };
        HostHardwareInfo hardware = Probe(reader) with { CpuArchitecture = architecture };

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(46_332L * MiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(46_114L * MiB, gpu.FreeGpuVisibleMemoryBytes);
        Assert.Null(gpu.SharedGpuMemoryBytes);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(46_332L * MiB, result.DetectedTotalMemoryBytes);
        Assert.Equal(46_114L * MiB, result.AvailableFreeMemoryBytes);
    }

    [Theory]
    [InlineData(15_061L, 16_375L)]
    [InlineData(0L, 46_332L)]
    [InlineData(30_720L, 49_152L)]
    public void CudaProbe_UsesCudaTotalAndFreeMemoryWithoutOtherMemorySources(long freeMiB, long totalMiB)
    {
        var reader = new StubCudaDeviceReader { Memory = (freeMiB * MiB, totalMiB * MiB) };

        GpuInfo gpu = Assert.Single(Probe(reader).Gpus);

        Assert.Equal(totalMiB * MiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(freeMiB * MiB, gpu.FreeGpuVisibleMemoryBytes);
        Assert.Null(gpu.SharedGpuMemoryBytes);
    }

    [Fact]
    public void CudaProbe_MissingCudaMemoryKeepsIdentifiedDeviceRetryable()
    {
        HostHardwareInfo hardware = Probe(new StubCudaDeviceReader { Memory = null });

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal("GPU-stub", gpu.StableId);
        Assert.Null(gpu.GpuVisibleMemoryBytes);
        Assert.Null(gpu.FreeGpuVisibleMemoryBytes);
        Assert.Equal(
            LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
            LocalInferenceEligibility.Evaluate(hardware).FailureCode);
    }

    [Fact]
    public void Evaluate_InconclusiveGpuIsNotHiddenByADefinitivelyUnsupportedGpu()
    {
        // The unreadable device could still be supported, so reporting the other
        // adapter's definitive verdict would disable recheck on this machine.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64,
            new GpuInfo(GpuVendor.Nvidia, "NVIDIA unreadable adapter", CudaMajorVersion: 13),
            Gpu("NVIDIA small adapter", "GPU-small", 16, 16));

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
    }

    [Fact]
    public void Evaluate_EligibleGpuStillWinsOverAnInconclusiveGpu()
    {
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64,
            new GpuInfo(GpuVendor.Nvidia, "NVIDIA unreadable adapter", CudaMajorVersion: 13),
            Gpu("NVIDIA capable adapter", "GPU-capable", 48, 48));

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-capable", result.SelectedGpu?.StableId);
    }

    private static HostHardwareInfo Probe(ICudaDeviceReader reader) =>
        new CudaHostHardwareProbe(reader).Probe();

    private sealed class StubCudaDeviceReader : ICudaDeviceReader
    {
        public CudaDriverAvailability Availability { get; init; } = CudaDriverAvailability.Ready;
        public int? DeviceCount { get; init; } = 1;
        public int? DeviceHandle { get; init; } = 0;
        public string? Name { get; init; } = "NVIDIA GeForce RTX 5090";
        public string? Uuid { get; init; } = "GPU-stub";
        public (long FreeBytes, long TotalBytes)? Memory { get; init; } = (32 * GiB, 32 * GiB);
        public Func<int, string?>? UuidByDevice { get; init; }
        public Func<Exception>? UuidFailure { get; init; }

        public CudaDriverAvailability TryInitialize() => Availability;

        public int? TryReadDeviceCount() => DeviceCount;

        public int? TryReadCudaMajorVersion() => 13;

        public int? TryReadDeviceHandle(int ordinal) => DeviceHandle is null ? null : ordinal;

        public string? TryReadDeviceName(int device) => Name;

        public string? TryReadDeviceUuid(int device) =>
            UuidFailure is not null
                ? throw UuidFailure()
                : UuidByDevice is not null ? UuidByDevice(device) : Uuid;

        public (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device) => Memory;
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA GeForce RTX 5080", LlamaRuntimeCatalog.X64RuntimeId)]
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

    // Boundaries verified against real hardware: a real 48GB-SKU RTX Spark
    // reads ~45.25 GiB (48,585,498,624 bytes) via cuMemGetInfo, matching the
    // "Gb48" case below almost exactly.
    [Theory]
    [InlineData(30, null)] // 32GB SKU: no local AI recommended
    [InlineData(45, LocalModelCatalog.Qwen35B_IQ4XSModelId)] // 48GB SKU -> 24GB recipe
    [InlineData(62, LocalModelCatalog.Qwen38_27BModelId)] // 64GB SKU -> 28GB recipe
    [InlineData(120, LocalModelCatalog.Qwen38_27B_DFlashModelId)] // 128GB SKU -> 48GB recipe (default)
    public void Evaluate_RoutesRtxSparkByFixedSkuTable(long totalGiB, string? expectedModelId)
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.Arm64, Gpu("NVIDIA RTX Spark N1X", "GPU-spark", totalGiB, totalGiB)));

        if (expectedModelId is null)
        {
            Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
            Assert.Equal(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, result.FailureCode);
            Assert.Equal(LocalInferenceSelectionFailureCode.NotRecommendedForSku, result.SelectionFailureCode);
            Assert.Null(result.Plan);
        }
        else
        {
            Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
            Assert.Equal(expectedModelId, result.Plan?.Model.Id);
        }
    }

    [Fact]
    public void Evaluate_Rtx5090WithSparkSizedMemoryIgnoresSkuTable()
    {
        // A non-Spark GPU that happens to have Spark-sized memory must still
        // take the generic priority/fit-test path -- SKU routing is keyed
        // strictly off the RTX Spark name, not memory size.
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA GeForce RTX 5090", "GPU-5090", totalGiB: 45, freeGiB: 45)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
    }

    [Fact]
    public void Evaluate_SparkRecipeIsBoundToTheSparkGpuOnMixedHosts()
    {
        // The SKU table answers "what should THIS Spark run", so the recipe and the
        // GPU that runs it must be the same adapter. A discrete GPU with more free
        // memory must not win the eligibility ranking and end up running a recipe
        // that was chosen for the Spark.
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(
                RuntimeArchitecture.Arm64,
                Gpu("NVIDIA RTX Spark N1X", "GPU-spark", totalGiB: 45, freeGiB: 45),
                Gpu("NVIDIA GeForce RTX 5090", "GPU-5090", totalGiB: 80, freeGiB: 80)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen35B_IQ4XSModelId, result.Plan?.Model.Id);
        Assert.Equal("GPU-spark", result.SelectedGpu?.StableId);
        Assert.Equal("GPU-spark", result.Plan?.BoundGpuStableId);
    }

    [Fact]
    public void Evaluate_UnrecommendedSparkSkuStillQualifiesADiscreteGpuOnTheSameHost()
    {
        // A 32 GB Spark has no recommended model, but that is a statement about the
        // Spark, not about the host. An eligible discrete GPU beside it must still
        // qualify through the generic path instead of the whole host being rejected.
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(
                RuntimeArchitecture.X64,
                Gpu("NVIDIA RTX Spark N1X", "GPU-spark32", totalGiB: 30, freeGiB: 30),
                Gpu("NVIDIA GeForce RTX 5090", "GPU-5090", totalGiB: 32, freeGiB: 32)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal("GPU-5090", result.SelectedGpu?.StableId);
        Assert.Null(result.Plan?.BoundGpuStableId);
    }

    [Fact]
    public void Evaluate_UnrecommendedSparkSkuAloneStillReportsNotRecommended()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.Arm64, Gpu("NVIDIA RTX Spark N1X", "GPU-spark32", 30, 30)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(
            LocalInferenceSelectionFailureCode.NotRecommendedForSku,
            result.SelectionFailureCode);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(62)]
    [InlineData(120)]
    public void Evaluate_SparkRecommendationRoundTrippedBySetupKeepsItsSkuProfile(long totalGiB)
    {
        // Normal setup persists the recommended model id and passes it back as an
        // explicit request, so the recommendation must resolve identically both ways.
        // Otherwise the SKU's pinned profile (the 64 GB tier's reduced context is not
        // the largest that merely fits) is silently replaced by the generic fit-test.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.Arm64,
            Gpu("NVIDIA RTX Spark N1X", "GPU-spark", totalGiB, totalGiB));

        LocalInferenceEligibilityResult recommended = LocalInferenceEligibility.Evaluate(hardware);
        LocalInferenceEligibilityResult roundTripped = LocalInferenceEligibility.Evaluate(
            hardware,
            recommended.Plan!.Model.Id);

        Assert.Equal(recommended.Plan!.Model.Id, roundTripped.Plan?.Model.Id);
        Assert.Equal(recommended.Plan!.Profile.Id, roundTripped.Plan?.Profile.Id);
        Assert.Equal("GPU-spark", roundTripped.Plan?.BoundGpuStableId);
        Assert.Equal(recommended.SelectedGpu?.StableId, roundTripped.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_ExplicitNonRecommendedModelOnSparkStillUsesTheGenericFitTest()
    {
        // A real user override must not be forced onto the SKU recipe.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.Arm64,
            Gpu("NVIDIA RTX Spark N1X", "GPU-spark", 62, 62));

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            hardware,
            LocalModelCatalog.Qwen27BModelId);

        Assert.Equal(LocalModelCatalog.Qwen27BModelId, result.Plan?.Model.Id);
        Assert.Null(result.Plan?.BoundGpuStableId);
    }

    [Fact]
    public void EvaluateForConfiguredAvailability_KeepsAValidSavedModelOn32GbSpark()
    {
        // A 32 GB Spark has no recommended default, but it still runs a model that was
        // already configured. Rerunning setup must not switch Local AI off on that machine.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.Arm64, Gpu("NVIDIA RTX Spark N1X", "GPU-spark32", 30, 30));

        LocalInferenceEligibilityResult result =
            LocalInferenceEligibility.EvaluateForConfiguredAvailability(
                hardware,
                LocalModelCatalog.Qwen38_27BModelId);

        Assert.True(result.CanInstall);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.NotNull(result.SelectedGpu);
    }

    [Fact]
    public void EvaluateForConfiguredAvailability_PreservesTheRecoveryPinnedModelAndProfileOn32GbSpark()
    {
        // Recovery pins the configured model and reuses its resolved plan. On a SKU with no
        // recommended default that selection must survive the availability gate with the same
        // model and the same profile the explicit path resolves, so a recovery rerun does not
        // silently move an existing install to a different context or KV precision.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.Arm64, Gpu("NVIDIA RTX Spark N1X", "GPU-spark32", 30, 30));
        const string pinnedModelId = LocalModelCatalog.Qwen38_27BModelId;

        LocalInferenceEligibilityResult availability =
            LocalInferenceEligibility.EvaluateForConfiguredAvailability(hardware, pinnedModelId);
        LocalInferenceEligibilityResult pinned =
            LocalInferenceEligibility.Evaluate(hardware, pinnedModelId);

        Assert.True(availability.CanInstall);
        Assert.Equal(pinnedModelId, availability.Plan?.Model.Id);
        Assert.Equal(pinned.Plan?.Profile.Id, availability.Plan?.Profile.Id);
        Assert.Equal(pinned.Plan?.Profile.ContextTokens, availability.Plan?.Profile.ContextTokens);
        Assert.Equal(pinned.Plan?.Profile.KeyCachePrecision, availability.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(pinned.SelectedGpu?.StableId, availability.SelectedGpu?.StableId);
    }

    [Fact]
    public void EvaluateForConfiguredAvailability_WithNoSavedModelStillReportsNotRecommended()
    {
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.Arm64, Gpu("NVIDIA RTX Spark N1X", "GPU-spark32", 30, 30));

        LocalInferenceEligibilityResult result =
            LocalInferenceEligibility.EvaluateForConfiguredAvailability(hardware, configuredModelId: null);

        Assert.False(result.CanInstall);
        Assert.Equal(
            LocalInferenceSelectionFailureCode.NotRecommendedForSku,
            result.SelectionFailureCode);
    }

    [Fact]
    public void EvaluateForConfiguredAvailability_UnknownSavedModelReportsThatModelsFailure()
    {
        // The reason must name what is wrong with the saved selection, not fall back to the
        // SKU's generic no-recommendation message, or setup offers no path to recovery.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.Arm64, Gpu("NVIDIA RTX Spark N1X", "GPU-spark32", 30, 30));

        LocalInferenceEligibilityResult result =
            LocalInferenceEligibility.EvaluateForConfiguredAvailability(hardware, "no-such-model-id");

        Assert.False(result.CanInstall);
        Assert.Equal(LocalInferenceSelectionFailureCode.UnknownModel, result.SelectionFailureCode);
        Assert.Equal(
            LocalInferenceUnavailableReasonKind.UnknownModel,
            LocalInferenceEligibilityDiagnostics.GetUnavailableReason(result).Kind);
    }

    [Fact]
    public void EvaluateForConfiguredAvailability_OversizedSavedModelReportsCapacityNotSkuPolicy()
    {
        // A saved model that no longer fits must report the capacity shortfall, including the
        // model name and the required and detected memory the setup page renders.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.Arm64, Gpu("NVIDIA RTX Spark N1X", "GPU-sparkSmall", 12, 12));

        LocalInferenceEligibilityResult result =
            LocalInferenceEligibility.EvaluateForConfiguredAvailability(
                hardware,
                LocalModelCatalog.Qwen38_27BModelId);

        Assert.False(result.CanInstall);
        Assert.Equal(
            LocalInferenceEligibilityFailureCode.InsufficientGpuMemory,
            result.FailureCode);
        LocalInferenceUnavailableReason reason =
            LocalInferenceEligibilityDiagnostics.GetUnavailableReason(result);
        Assert.Equal(LocalInferenceUnavailableReasonKind.InsufficientGpuMemory, reason.Kind);
        Assert.False(string.IsNullOrWhiteSpace(reason.ModelDisplayName));
    }

    [Fact]
    public void EvaluateForConfiguredAvailability_LeavesNonSparkDevicesUnchanged()
    {
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64, Gpu("NVIDIA GeForce RTX 5090", "GPU-5090", 32, 32));

        LocalInferenceEligibilityResult withSaved =
            LocalInferenceEligibility.EvaluateForConfiguredAvailability(
                hardware, LocalModelCatalog.Qwen27BModelId);
        LocalInferenceEligibilityResult device = LocalInferenceEligibility.Evaluate(hardware);

        Assert.True(withSaved.CanInstall);
        Assert.Equal(device.Plan?.Model.Id, withSaved.Plan?.Model.Id);
    }

    [Fact]
    public void Evaluate_HugeNonSparkGpuStillDefaultsToTheRecommendedModel()
    {
        // A priority-0, IsExplicitAlternative model must never win the generic
        // default/fallback pick regardless of available memory. The guard in
        // SelectDefaultModelAndProfile keeps that true as the catalog grows.
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary huge adapter", "GPU-huge", totalGiB: 200, freeGiB: 200)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.DoesNotContain(
            LocalModelCatalog.Models,
            m => m.RecommendationPriority == 0 && m.IsExplicitAlternative && m.Id == result.Plan!.Model.Id);
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
    public void Evaluate_UnsetModelRejects16GiBCapacity()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.DoesNotContain(LocalModelCatalog.Models, model => model.Id == "qwen3.5-9b-mtp-q4-k-m");
    }

    [Fact]
    public void Evaluate_Removed16GiBModelIdIsUnknown()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-32", 32, 32)),
            "qwen3.5-9b-mtp-q4-k-m");

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, result.FailureCode);
        Assert.Equal(LocalInferenceSelectionFailureCode.UnknownModel, result.SelectionFailureCode);
        Assert.Null(result.Plan);
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
        GpuInfo unsupported = Gpu("NVIDIA incompatible", "GPU-old", 48, 48) with { CudaMajorVersion = 12 };
        GpuInfo busy = Gpu("NVIDIA busy", "GPU-busy", 32, 1);
        GpuInfo eligible = Gpu("NVIDIA ready", "GPU-ready", 32, 32);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, unsupported, busy, eligible),
            LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-ready", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_IgnoresLegacySharedMemoryFields()
    {
        GpuInfo gpu = Gpu("NVIDIA generic unified memory", "GPU-shared", 8, 8) with
        {
            SharedGpuMemoryBytes = 16 * GiB,
            FreeSharedGpuMemoryBytes = null,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.Arm64, gpu));

        // Separate shared-memory estimates are not added to CUDA-visible memory.
        // Qwen3.5 9B is retired, so the fallback is the smallest offered model.
        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(8 * GiB, result.DetectedTotalMemoryBytes);
        Assert.Equal(8 * GiB, result.AvailableFreeMemoryBytes);
    }

    [Fact]
    public void Evaluate_RanksEligibleAdaptersByFreeThenTotalThenUuid()
    {
        GpuInfo moreTotal = Gpu("NVIDIA total", "GPU-z", 64, 42);
        GpuInfo moreFree = Gpu("NVIDIA free", "GPU-b", 48, 43);
        GpuInfo sameFreeAndTotalLowerUuid = Gpu("NVIDIA tie", "GPU-a", 48, 43);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, moreTotal, moreFree, sameFreeAndTotalLowerUuid),
            LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal("GPU-a", result.SelectedGpu?.StableId);
    }

    [Theory]
    [InlineData(null, 13, LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)]
    [InlineData("GPU-cuda", 12, LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow)]
    public void Evaluate_RequiresStableIdAndCompatibleCuda(
        string? stableId,
        int cudaMajor,
        LocalInferenceEligibilityFailureCode expectedFailure)
    {
        GpuInfo gpu = Gpu("NVIDIA arbitrary", stableId, 32, 32) with
        {
            CudaMajorVersion = cudaMajor,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(expectedFailure, result.FailureCode);
    }

    [Fact]
    public void Evaluate_IdentifiedGpuWithoutMemoryIsIncompleteRatherThanAbsent()
    {
        GpuInfo gpu = Gpu("NVIDIA identified", "GPU-identified", 32, 32) with
        {
            GpuVisibleMemoryBytes = null,
            FreeGpuVisibleMemoryBytes = null,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.Equal("GPU-identified", result.SelectedGpu?.StableId);
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

    [Theory]
    [InlineData("b10655-cuda13-x64", "b10655")]
    [InlineData("b10655-cuda13-arm64", "b10655")]
    public void FindInstalled_ResolvesRetiredRuntimeSoExistingInstallsStayLaunchable(
        string runtimeId,
        string expectedReleaseTag)
    {
        // An installation recorded before the runtime bump must keep resolving its own
        // receipt, otherwise updating the app strands it until setup repairs it.
        LlamaRuntimeVariant? installed = LlamaRuntimeCatalog.FindInstalled(runtimeId);

        Assert.NotNull(installed);
        Assert.Equal(runtimeId, installed.Id);
        Assert.Equal(expectedReleaseTag, installed.ReleaseTag);
        Assert.False(string.Equals(LlamaRuntimeCatalog.ReleaseTag, installed.ReleaseTag, StringComparison.Ordinal));
    }

    [Fact]
    public void RetiredRuntimeIsNeverOfferedForNewInstalls()
    {
        Assert.DoesNotContain(
            LlamaRuntimeCatalog.Variants,
            variant => variant.ReleaseTag != LlamaRuntimeCatalog.ReleaseTag);
        Assert.All(
            LlamaRuntimeCatalog.Variants,
            variant => Assert.Equal(LlamaRuntimeCatalog.ReleaseTag, variant.ReleaseTag));
    }

    [Fact]
    public void FindInstalled_RejectsUnknownRuntimeId()
    {
        Assert.Null(LlamaRuntimeCatalog.FindInstalled("b00000-cuda13-x64"));
        Assert.Null(LlamaRuntimeCatalog.FindInstalled(null));
    }
}
