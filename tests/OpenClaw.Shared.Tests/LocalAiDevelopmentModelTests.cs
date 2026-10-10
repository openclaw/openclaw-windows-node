using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Guard-on tests mutate a process-wide environment variable, so they run alone
/// after every parallel collection has finished.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalAiDevelopmentModelCollection
{
    public const string Name = "Local AI development models";
}

/// <summary>
/// The development low-memory guard brings back Qwen3.5 9B as a recommended,
/// fully profiled model only when no production model fits, and only for the exact value "1".
/// </summary>
[Collection(LocalAiDevelopmentModelCollection.Name)]
public sealed class LocalAiDevelopmentModelTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Evaluate_DefaultPicksQwen9BOnSixteenGiBOnlyWithGuard()
    {
        HostHardwareInfo hardware = Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16));
        using var env = new EnvironmentScope(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "1");

        LocalInferenceEligibilityResult guarded = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, guarded.Status);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, guarded.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.ReducedContextTokens, guarded.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.F16, guarded.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(16_069_374_304L, guarded.RequiredTotalMemoryBytes);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Default, guarded.Plan?.ModelSelectionOrigin);

        env.Set(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);
        LocalInferenceEligibilityResult unguarded = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, unguarded.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, unguarded.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, unguarded.Plan?.Model.Id);
    }

    [Fact]
    public void Evaluate_DefaultPicksQwen9BMinimumContextOnTwelveGiB()
    {
        using var env = new EnvironmentScope(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "1");

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-12", 12, 12)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.MinimumContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.F16, result.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(12_579_713_376L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Evaluate_EightGiBStillUnsupportedWithGuard()
    {
        using var env = new EnvironmentScope(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "1");

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-8", 8, 8)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, result.Plan?.Model.Id);
        Assert.Equal(11_447_251_296L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Evaluate_GuardKeepsQwen38PreferredWhenItFits()
    {
        using var env = new EnvironmentScope(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "1");

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(Gpu("NVIDIA arbitrary adapter", "GPU-24", 24, 24)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.MinimumContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.F16, result.Plan?.Profile.KeyCachePrecision);
        Assert.Equal(25_322_810_272L, result.RequiredTotalMemoryBytes);
    }

    [Fact]
    public void Catalog_GuardOffersQwen9BOnlyForValueOne()
    {
        const string id = LocalModelCatalog.Qwen9BModelId;
        using var env = new EnvironmentScope(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "1");

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
            StableId: stableId);
}
