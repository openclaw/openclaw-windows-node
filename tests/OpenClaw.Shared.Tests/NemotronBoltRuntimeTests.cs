using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class NemotronBoltRuntimeTests
{
    private const string BoltFamily = LlamaRuntimeCatalog.NemotronBoltRuntimeFamily;

    [Fact]
    public void ExplicitBolt_UsesDedicatedNativeArm64Runtime()
    {
        var runtime = Assert.IsType<LlamaRuntimeVariant>(LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, BoltFamily));

        Assert.Equal(LlamaRuntimeCatalog.NemotronBoltArm64RuntimeId, runtime.Id);
        Assert.Equal(RuntimeArchitecture.Arm64, runtime.Architecture);
        Assert.Contains(runtime.RequiredFiles, file => file.FileName == "libomp.dll");
        Assert.NotNull(LlamaRuntimeCatalog.FindInstalled(runtime.Id));
        Assert.DoesNotContain(runtime, LlamaRuntimeCatalog.Variants);
    }

    [Fact]
    public void OtherSelections_KeepStableRuntime()
    {
        Assert.Same(LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64),
            LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, "llama.cpp"));
        Assert.Same(BonsaiRuntimeCatalog.X64,
            LlamaRuntimeCatalog.Find(RuntimeArchitecture.X64, BonsaiRuntimeCatalog.Family));
        Assert.Null(LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, "unknown-family"));
    }

    [Fact]
    public void ExplicitBolt_UnsupportedArchitectureHasNoRuntimeFallback()
    {
        Assert.Null(LlamaRuntimeCatalog.Find(RuntimeArchitecture.X64, BoltFamily));
    }

    [Fact]
    public void ExplicitBolt_ReusesBytePinnedStableCudaDependency()
    {
        var runtime = Assert.IsType<LlamaRuntimeVariant>(LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, BoltFamily));
        var stable = LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64)!;

        var shared = stable.Artifacts.Single(a => a.Role == ArtifactRole.RuntimeDependency);
        var bolt = runtime.Artifacts.Single(a => a.Role == ArtifactRole.RuntimeDependency);

        // The same CUDA runtime bytes, whichever stable release currently carries them.
        Assert.Equal(shared.RelativePath, bolt.RelativePath);
        Assert.Equal(shared.SizeBytes, bolt.SizeBytes);
        Assert.Equal(shared.Sha256, bolt.Sha256);
        // Bolt installs recorded the release this archive was downloaded from. Moving the stable
        // runtime must not change that address, or their receipts stop matching the catalog.
        Assert.Equal("b11320", Assert.IsType<GitHubReleaseSource>(bolt.Source).ReleaseTag);
    }
}
