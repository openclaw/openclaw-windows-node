using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class NemotronBoltEligibilityTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static LocalModelInfo Bolt => LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)!;
    private static long Required(int context) => LocalInferenceEligibility.GetRequiredMemoryBytes(Bolt,
        LocalModelCatalog.GetProfiles(Bolt).Single(p => p.ContextTokens == context));

    [Theory]
    [InlineData(262144)]
    [InlineData(131072)]
    public void ExplicitBolt_SelectsLargestProfileFittingCurrentCommit(int context)
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(48 * GiB, Required(context)), Bolt.Id);
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(context, result.Plan!.Profile.ContextTokens);
        Assert.Equal(Required(context), result.AvailableFreeMemoryBytes);
        Assert.True(result.CanOffer);
        Assert.Equal(LlamaRuntimeCatalog.NemotronBoltArm64RuntimeId, result.Plan.Runtime.Id);
    }

    [Fact]
    public void ExplicitBolt_OneByteBelow256KSelects128K()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(Required(262144) - 1, 48 * GiB), Bolt.Id);
        Assert.Equal(131072, result.Plan!.Profile.ContextTokens);
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public void ExplicitBolt_OneByteBelow128KIsBusyWithoutShrinkingBelowFloor()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(48 * GiB, Required(131072) - 1), Bolt.Id);
        Assert.Equal(LocalInferenceEligibilityStatus.EligibleButBusy, result.Status);
        Assert.Equal(131072, result.Plan!.Profile.ContextTokens);
        Assert.True(result.CanInstall); // Existing receipts remain recoverable.
        Assert.False(result.CanOffer);
    }

    [Fact]
    public void InstalledBolt_ModelOnlyEvaluationDoesNotDeriveAFreshSmallerTier()
    {
        var result = LocalInferenceEligibility.EvaluateInstalled(Hardware(48 * GiB, Required(131072)), Bolt.Id);
        Assert.Equal(262144, result.Plan!.Profile.ContextTokens);
        Assert.Equal(LocalInferenceEligibilityStatus.EligibleButBusy, result.Status);
        Assert.False(result.CanOffer);
    }

    [Theory]
    [InlineData(262144, 131072, LocalInferenceEligibilityStatus.EligibleButBusy)]
    [InlineData(131072, 262144, LocalInferenceEligibilityStatus.Eligible)]
    public void RecordedBoltProfile_IsRetainedWhenAvailableMemoryChanges(int installedContext,
        int fittingContext, LocalInferenceEligibilityStatus expected)
    {
        var profile = LocalModelCatalog.GetProfiles(Bolt).Single(p => p.ContextTokens == installedContext);
        var plan = new LocalInferencePlan(LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, Bolt.RuntimeFamily)!,
            Bolt, profile, LocalInferenceModelSelectionOrigin.Explicit, "GPU-spark");
        var result = LocalInferenceEligibility.EvaluateRecordedProfile(Hardware(48 * GiB, Required(fittingContext)), plan);
        Assert.Same(plan, result.Plan);
        Assert.Same(profile, result.Plan!.Profile);
        Assert.Equal(expected, result.Status);
        Assert.Equal("GPU-spark", result.SelectedGpu!.StableId);
    }

    [Theory]
    [InlineData(null, 48L)]
    [InlineData(48L, null)]
    public void ExplicitBolt_UnknownCurrentBudgetRequiresRecheck(long? freeGiB, long? commitGiB)
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(freeGiB * GiB, commitGiB * GiB), Bolt.Id);
        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.False(result.CanOffer);
    }

    [Fact]
    public void ExplicitBolt_CannotUseAnUnqualifiedGpuOnAMixedHost()
    {
        var hardware = Hardware(48 * GiB, 48 * GiB);
        hardware = hardware with { Gpus = [hardware.Gpus[0],
            new GpuInfo(GpuVendor.Nvidia, "NVIDIA H100", 200 * GiB, 200 * GiB,
                CudaMajorVersion: 13, StableId: "GPU-H100")] };
        var result = LocalInferenceEligibility.Evaluate(hardware, Bolt.Id);
        Assert.Equal("GPU-spark", result.SelectedGpu!.StableId);
    }

    [Theory]
    [InlineData("NVIDIA H100")]
    [InlineData("NVIDIA RTX Spark N2")]
    public void ExplicitBolt_OnlyQualifiedN1XCanBeOffered(string name)
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(48 * GiB, 48 * GiB, name), Bolt.Id);
        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
    }

    [Fact]
    public void ExplicitBolt_UnknownGpuNameRemainsRetryable()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(48 * GiB, 48 * GiB, "NVIDIA GPU"), Bolt.Id);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
    }

    [Fact]
    public void ExplicitBolt_TotalCapacityBelow128KCannotBeOffered()
    {
        var hardware = Hardware(48 * GiB, 48 * GiB);
        hardware = hardware with { Gpus = [hardware.Gpus[0] with { GpuVisibleMemoryBytes = Required(131072) - 1 }] };
        var result = LocalInferenceEligibility.Evaluate(hardware, Bolt.Id);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.False(result.CanOffer);
        Assert.Equal(131072, result.Plan!.Profile.ContextTokens);
    }

    [Fact]
    public void OtherModels_KeepTheirExistingTotalCapacityAndBusySemantics()
    {
        var result = LocalInferenceEligibility.Evaluate(Hardware(0, null), LocalModelCatalog.Qwen35B_Q4KSModelId);
        Assert.Equal(LocalInferenceEligibilityStatus.EligibleButBusy, result.Status);
        Assert.Equal(LocalModelCatalog.RtxSpark48GbContextTokens, result.Plan!.Profile.ContextTokens);
        Assert.True(result.CanInstall);
        Assert.True(result.CanOffer);
    }

    private static HostHardwareInfo Hardware(long? free, long? commit, string name = "NVIDIA RTX Spark N1X") =>
        new(RuntimeArchitecture.Arm64, 48 * GiB, 32 * GiB,
            [new GpuInfo(GpuVendor.Nvidia, name, 48 * GiB, free, CudaMajorVersion: 13, StableId: "GPU-spark")], false)
        { AvailableCommitMemoryBytes = commit };
}
