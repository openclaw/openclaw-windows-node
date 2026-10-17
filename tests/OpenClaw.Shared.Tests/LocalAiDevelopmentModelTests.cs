using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Guard tests mutate a process-wide environment variable, so they run alone
/// after every parallel collection has finished. The low-memory models are
/// offered by default; "0" is the explicit opt-out.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalAiDevelopmentModelCollection
{
    public const string Name = "Local AI development models";
}

/// <summary>
/// The low-memory Qwen3.5 9B and dense DeepSeek-R1-Distill-Qwen-14B recipes
/// are recommended, fully profiled models by default when no production model
/// fits, and are hidden only by the exact opt-out value "0".
/// </summary>
[Collection(LocalAiDevelopmentModelCollection.Name)]
public sealed class LocalAiDevelopmentModelTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Evaluate_DefaultPicksQwen9BOnSixteenGiBByDefault()
    {
        HostHardwareInfo hardware = Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16));
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        LocalInferenceEligibilityResult enabled = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, enabled.Status);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, enabled.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.ReducedContextTokens, enabled.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.F16, enabled.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(16_069_374_304L, enabled.RequiredTotalMemoryBytes);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Default, enabled.Plan?.ModelSelectionOrigin);

        env.Set(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "0");
        LocalInferenceEligibilityResult disabled = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalModelCatalog.Bonsai2ModelId, disabled.Plan?.Model.Id);
    }

    [Fact]
    public void Evaluate_DefaultPicksQwen9BMinimumContextOnTwelveGiB()
    {
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-12", 12, 12)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.MinimumContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.F16, result.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(12_579_713_376L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Evaluate_EightGiBStillUnsupportedWithLowMemoryModels()
    {
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-8", 8, 8)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
        Assert.Equal(11_447_251_296L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Evaluate_LowMemoryModelsKeepQwen38PreferredWhenItFits()
    {
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-24", 24, 24)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.MinimumContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.F16, result.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(25_322_810_272L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Evaluate_TwelveGiBPicksQwen9BOverDeepSeek14BByDefault()
    {
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA TITAN V", "GPU-TITANV", 12, 12)));

        // 9B outranks the 14B (priority 100 vs 50) and its 65,536-token F16
        // profile still fits 12 GiB, so the dense 14B stays an alternative.
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
    }

    [Fact]
    public void Evaluate_ExplicitDeepSeek14BFitsTwelveGiBAtEightKQ8()
    {
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA TITAN V", "GPU-TITANV", 12, 12)),
            LocalModelCatalog.DeepSeek14BModelId);

        // 196,608 bytes/token of F16 KV (48 layers, 8 KV heads, head dimension
        // 128) push every 16K-or-larger tier past 12 GiB, so the dense distill
        // bottoms out at its 8,192-token Q8_0 tier on a TITAN V.
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.DeepSeek14BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.DevelopmentMinimumContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.Q8_0, result.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Explicit, result.Plan?.ModelSelectionOrigin);
        Assert.Equal(12_489_809_312L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Evaluate_ExplicitDeepSeek14BFitsSixteenGiBAtThirtyTwoKQ8()
    {
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16)),
            LocalModelCatalog.DeepSeek14BModelId);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.DevelopmentLowMemoryContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.Q8_0, result.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(15_056_723_360L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Catalog_OffersDeepSeek14BUntilOptOut()
    {
        const string id = LocalModelCatalog.DeepSeek14BModelId;
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        Assert.Contains(LocalModelCatalog.Models, model => model.Id == id);
        LocalModelInfo offered = Assert.IsType<LocalModelInfo>(LocalModelCatalog.Find(id));
        Assert.Equal(6, LocalModelCatalog.GetProfiles(offered).Count);
        Assert.False(LocalModelCatalog.IsLegacy(id));

        env.Set(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "0");

        Assert.DoesNotContain(LocalModelCatalog.Models, model => model.Id == id);
        Assert.Null(LocalModelCatalog.Find(id));
        LocalInferenceRunProfile only = Assert.Single(
            LocalModelCatalog.GetProfiles(LocalModelCatalog.FindInstalled(id)!));
        Assert.Equal("ctx-262144-f16", only.Id);
        Assert.True(LocalModelCatalog.IsLegacy(id));
    }

    [Fact]
    public void Catalog_OffersQwen9BUntilOptOut()
    {
        const string id = LocalModelCatalog.Qwen9BModelId;
        using var env = new EnvironmentScope(
            LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        Assert.Contains(LocalModelCatalog.Models, model => model.Id == id);
        Assert.Contains(LocalModelCatalog.ExplicitAlternatives, model => model.Id == id);
        LocalModelInfo offered = Assert.IsType<LocalModelInfo>(LocalModelCatalog.Find(id));
        Assert.Equal(8, LocalModelCatalog.GetProfiles(offered).Count);
        Assert.False(LocalModelCatalog.IsLegacy(id));

        env.Set(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "0");

        Assert.DoesNotContain(LocalModelCatalog.Models, model => model.Id == id);
        Assert.DoesNotContain(LocalModelCatalog.ExplicitAlternatives, model => model.Id == id);
        Assert.Null(LocalModelCatalog.Find(id));
        LocalInferenceRunProfile only = Assert.Single(
            LocalModelCatalog.GetProfiles(LocalModelCatalog.FindInstalled(id)!));
        Assert.Equal("ctx-262144-f16", only.Id);
        Assert.True(LocalModelCatalog.IsLegacy(id));
    }

    private static HostHardwareInfo Hardware(params GpuInfo[] gpus) =>
        new(RuntimeArchitecture.X64, 64 * GiB, 48 * GiB, gpus, false);

    private static GpuInfo Gpu(string name, string? stableId, long totalGiB, long freeGiB) =>
        new(
            GpuVendor.Nvidia,
            name,
            totalGiB * GiB,
            freeGiB * GiB,
            DriverVersion: "616.30",
            CudaMajorVersion: 13,
            StableId: stableId,
            ComputeCapability: new Version(12, 0));
}
