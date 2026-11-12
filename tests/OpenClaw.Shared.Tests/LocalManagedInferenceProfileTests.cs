using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Tests;

public sealed class LocalManagedInferenceProfileTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void EightGiB_SelectsPairedLowMemoryProfile()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(8));
        Assert.True(result.CanInstall);
        Assert.Equal(LocalModelCatalog.Bonsai2ModelId, result.Plan!.Model.Id);
        Assert.Equal(BonsaiRuntimeCatalog.X64, result.Plan.Runtime);
        Assert.Equal(8192, result.Plan.Profile.ContextTokens);
        Assert.InRange(result.RequiredTotalMemoryBytes, 7 * GiB, 8 * GiB);
        Assert.Equal(SpeculativeDecodingMode.None, result.Plan.Model.Recipe.SpeculativeDecoding);
        Assert.NotNull(LocalInferenceProfileRecommendation.From(result));
    }

    [Fact]
    public void LargeGpu_PrefersExistingQwenProfile()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(96));
        Assert.Equal(LocalModelCatalog.Default.Id, result.Plan!.Model.Id);
        Assert.Equal("llama.cpp", result.Plan.Runtime.Family);
        Assert.Null(LocalInferenceProfileRecommendation.From(result));
    }

    [Fact]
    public void BusyGpu_DoesNotDowngradeProfile()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(8, freeVram: 1));
        Assert.Equal(LocalInferenceEligibilityStatus.EligibleButBusy, result.Status);
        Assert.Equal(LocalModelCatalog.Bonsai2ModelId, result.Plan!.Model.Id);
    }

    [Theory]
    [InlineData(6, 32, 20, LocalInferenceEligibilityFailureCode.InsufficientGpuMemory)]
    [InlineData(8, 8, 20, LocalInferenceEligibilityFailureCode.InsufficientSystemMemory)]
    [InlineData(8, 32, 4, LocalInferenceEligibilityFailureCode.InsufficientDiskSpace)]
    public void NoCandidateFits_ReportsLimitingResource(int vram, int ram, int disk,
        LocalInferenceEligibilityFailureCode expected)
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(vram, ram, disk));
        Assert.False(result.CanInstall);
        Assert.Equal(expected, result.FailureCode);
        Assert.Null(LocalInferenceProfileRecommendation.From(result));
    }

    [Fact]
    public void MissingResourceFacts_AreRetryable()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(8) with { LocalAiDiskSpace = null });
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
    }

    [Fact]
    public void InstalledProfile_DoesNotRequireDownloadSpaceAgain()
    {
        var result = LocalInferenceEligibility.EvaluateInstalled(
            Hardware(8, disk: 0), LocalModelCatalog.Bonsai2ModelId);
        Assert.True(result.CanInstall);
        Assert.Equal(0, result.RequiredDiskBytes);
        Assert.True(LocalInferenceEligibility.EvaluateForConfiguredAvailability(
            Hardware(8, disk: 0), LocalModelCatalog.Bonsai2ModelId, hasInstalledReceipt: true).CanInstall);
    }

    [Fact]
    public void ExplicitOversizedModel_IsNotSilentlyReplaced()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(8), LocalModelCatalog.Default.Id);
        Assert.False(result.CanInstall);
        Assert.Equal(LocalModelCatalog.Default.Id, result.Plan!.Model.Id);
    }

    [Fact]
    public void UnsupportedArchitecture_DoesNotPairBonsaiWithStockRuntime()
    {
        var result = LocalInferenceEligibility.Evaluate(
            Hardware(8) with { CpuArchitecture = RuntimeArchitecture.Arm64 }, LocalModelCatalog.Bonsai2ModelId);
        Assert.Equal(LocalInferenceSelectionFailureCode.RuntimeUnavailable, result.SelectionFailureCode);
    }

    [Fact]
    public void OtherVendors_AreNotOfferedCudaProfile()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(8) with
        {
            Gpus = [new GpuInfo(GpuVendor.Amd, "AMD", 8 * GiB, 8 * GiB, CudaMajorVersion: 13, StableId: "GPU-amd")]
        });
        Assert.False(result.CanInstall);
    }

    [Theory]
    [InlineData(true, 8, 8, false, 16)]
    [InlineData(true, 16, 16, true, 16)]
    [InlineData(false, 8, 8, true, 8)]
    [InlineData(false, 8, 4, false, 8)]
    [InlineData(false, 4, 8, false, 8)]
    public void InstallationSpace_AccountsForBothDestinations(bool sharesVolume, int runtimeFree,
        int cacheFree, bool canInstall, int required)
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(8) with
        { LocalAiDiskSpace = new(runtimeFree * GiB, cacheFree * GiB, sharesVolume) });
        Assert.Equal(canInstall, result.CanInstall);
        Assert.Equal(required * GiB, result.RequiredDiskBytes);
        Assert.Equal(Math.Min(runtimeFree, cacheFree) * GiB, result.AvailableDiskBytes);
        if (!canInstall)
            Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientDiskSpace, result.FailureCode);
    }

    [Fact]
    public void InstalledAvailability_WithoutDiskFacts_RemainsRepairable()
    {
        var hardware = Hardware(8) with { LocalAiDiskSpace = null };
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
            LocalInferenceEligibility.Evaluate(hardware).FailureCode);
        Assert.True(LocalInferenceEligibility.EvaluateForConfiguredAvailability(
            hardware, LocalModelCatalog.Bonsai2ModelId, hasInstalledReceipt: true).CanInstall);
    }

    internal static HostHardwareInfo Hardware(int vram, int ram = 32, int disk = 20, int? freeVram = null) =>
        new(RuntimeArchitecture.X64, ram * GiB, ram * GiB,
            [new GpuInfo(GpuVendor.Nvidia, "NVIDIA RTX 4060", vram * GiB, (freeVram ?? vram) * GiB,
                CudaMajorVersion: 13, StableId: "GPU-synthetic-4060")], false, new(disk * GiB, disk * GiB, true));
}
