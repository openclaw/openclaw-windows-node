using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>A native Windows llama.cpp runtime and every archive required to execute it.</summary>
public sealed record LlamaRuntimeVariant
{
    public LlamaRuntimeVariant(
        string id,
        Architecture architecture,
        Version cudaVersion,
        IReadOnlyList<PinnedArtifact> artifacts,
        string? releaseTag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(cudaVersion);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (architecture is not (Architecture.X64 or Architecture.Arm64))
            throw new ArgumentOutOfRangeException(nameof(architecture), "Only native Windows x64 and ARM64 runtimes are cataloged.");
        if (cudaVersion.Major != 13)
            throw new ArgumentOutOfRangeException(nameof(cudaVersion), "The qualified runtime requires CUDA 13.");
        if (artifacts.Count != 2 ||
            artifacts.Count(artifact => artifact.Role == ArtifactRole.RuntimeBinary) != 1 ||
            artifacts.Count(artifact => artifact.Role == ArtifactRole.RuntimeDependency) != 1)
        {
            throw new ArgumentException(
                "A CUDA runtime variant requires one llama.cpp archive and one CUDA runtime archive.",
                nameof(artifacts));
        }

        Id = id;
        Architecture = architecture;
        CudaVersion = cudaVersion;
        Artifacts = artifacts;
        ReleaseTag = releaseTag ?? LlamaRuntimeCatalog.ReleaseTag;
    }

    public string Id { get; }
    public Architecture Architecture { get; }
    public Version CudaVersion { get; }
    public IReadOnlyList<PinnedArtifact> Artifacts { get; }
    /// <summary>
    /// The llama.cpp release this variant was pinned from. Equals
    /// <see cref="LlamaRuntimeCatalog.ReleaseTag"/> for the current runtime and
    /// keeps its own older value for a retired one, so an installed receipt is
    /// validated against the release it actually recorded.
    /// </summary>
    public string ReleaseTag { get; }
    public long TotalDownloadSizeBytes => Artifacts.Sum(artifact => artifact.SizeBytes);
}

/// <summary>
/// Integrity-pinned native Windows llama.cpp builds routed by Windows CPU
/// architecture. Unsupported hardware does not receive a CPU or Vulkan fallback.
/// </summary>
public static class LlamaRuntimeCatalog
{
    public const string ReleaseTag = "b11026";
    public const string ReleaseCommitSha = "b49650adb31f2e49a0d76113aeb1792134fd8413";
    public const string ServerExecutableName = "llama-server.exe";
    public const string X64RuntimeId = "b11026-cuda13-x64";
    public const string Arm64RuntimeId = "b11026-cuda13-arm64";

    public static GitHubReleaseSource Source { get; } = new(
        "ggml-org/llama.cpp",
        ReleaseTag,
        ReleaseCommitSha);

    private static readonly ReadOnlyCollection<LlamaRuntimeVariant> s_variants = Array.AsReadOnly(
        new[]
        {
            new LlamaRuntimeVariant(
                X64RuntimeId,
                Architecture.X64,
                new Version(13, 4),
                Array.AsReadOnly(
                    new[]
                    {
                        RuntimeArtifact(
                            "llama-b11026-cuda13-x64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b11026-bin-win-cuda-13.4-x64.zip",
                            150_102_391,
                            "6799f0962d066c54aee3773f0e5efa0076e46418695c0f4f6d24a38e7007dfb1"),
                        RuntimeArtifact(
                            "cudart-b11026-cuda13-x64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-x64.zip",
                            423_535_356,
                            "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668"),
                    })),
            new LlamaRuntimeVariant(
                Arm64RuntimeId,
                Architecture.Arm64,
                new Version(13, 4),
                Array.AsReadOnly(
                    new[]
                    {
                        RuntimeArtifact(
                            "llama-b11026-cuda13-arm64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b11026-bin-win-cuda-13.4-arm64.zip",
                            142_993_717,
                            "d4a31d05b4fe997872020d81e8482e7712c7c254ec9c7ccdb9597ae2a31e6728"),
                        RuntimeArtifact(
                            "cudart-b11026-cuda13-arm64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                            153_262_407,
                            "642dcde8805b3e3165ca710a5443b3b4044b27d96bd3ee3132473988c9bcb774"),
                    })),
        });

    // Retired from new installs and never offered or selected. These exist only so a
    // managed installation recorded before the runtime bump keeps resolving its own
    // receipt and stays launchable until setup upgrades it. Pins are reproduced
    // exactly as they were installed; nothing is remapped.
    private const string LegacyB10655ReleaseTag = "b10655";
    private const string LegacyB10655RuntimeIdX64 = "b10655-cuda13-x64";
    private const string LegacyB10655RuntimeIdArm64 = "b10655-cuda13-arm64";

    private static GitHubReleaseSource LegacyB10655Source { get; } = new(
        "ggml-org/llama.cpp",
        LegacyB10655ReleaseTag,
        "cb300598d5f90189cb69d2702f4930aaf99d32a2");

    private static readonly ReadOnlyCollection<LlamaRuntimeVariant> s_legacyVariants = Array.AsReadOnly(
        new[]
        {
            new LlamaRuntimeVariant(
                LegacyB10655RuntimeIdX64,
                Architecture.X64,
                new Version(13, 3),
                Array.AsReadOnly(
                    new[]
                    {
                        LegacyB10655Artifact(
                            "llama-b10655-cuda13-x64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b10655-bin-win-cuda-13.3-x64.zip",
                            146_478_045,
                            "be61636141327b3ca4d437c17489fd69964838a31a5fe3e97400f0dcd9f669dc"),
                        LegacyB10655Artifact(
                            "cudart-b10655-cuda13-x64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.3-x64.zip",
                            390_970_417,
                            "1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e"),
                    }),
                LegacyB10655ReleaseTag),
            new LlamaRuntimeVariant(
                LegacyB10655RuntimeIdArm64,
                Architecture.Arm64,
                new Version(13, 4),
                Array.AsReadOnly(
                    new[]
                    {
                        LegacyB10655Artifact(
                            "llama-b10655-cuda13-arm64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b10655-bin-win-cuda-13.4-arm64.zip",
                            140_055_278,
                            "567e61b4129e0d5b0580e5d3ea86b82ab5b6bee745ee02f69b58af799b49a582"),
                        LegacyB10655Artifact(
                            "cudart-b10655-cuda13-arm64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                            153_318_797,
                            "5a40dc7c5fa3d0a80ceeba4f16f9e8d25d87bcf1399c9233588953c43436c33c"),
                    }),
                LegacyB10655ReleaseTag),
        });

    public static IReadOnlyList<LlamaRuntimeVariant> Variants => s_variants;

    public static LlamaRuntimeVariant? Find(Architecture architecture) =>
        s_variants.SingleOrDefault(variant => variant.Architecture == architecture);

    /// <summary>
    /// Resolves a runtime id from an already-installed receipt, including a retired
    /// runtime from before the last version bump. Use this only on installed-receipt
    /// validation and launch paths. Selection and acquisition must keep using
    /// <see cref="Find"/> and <see cref="Variants"/> so a retired runtime is never
    /// installed again.
    /// </summary>
    public static LlamaRuntimeVariant? FindInstalled(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : s_variants.SingleOrDefault(variant => string.Equals(variant.Id, id, StringComparison.Ordinal))
                ?? s_legacyVariants.SingleOrDefault(variant => string.Equals(variant.Id, id, StringComparison.Ordinal));

    private static PinnedArtifact RuntimeArtifact(
        string id,
        ArtifactRole role,
        string fileName,
        long sizeBytes,
        string sha256) =>
        new(
            id,
            role,
            Source,
            fileName,
            sizeBytes,
            new Sha256Digest(sha256),
            LocalInferenceCatalogProvenance.NvidiaCair);

    private static PinnedArtifact LegacyB10655Artifact(
        string id,
        ArtifactRole role,
        string fileName,
        long sizeBytes,
        string sha256) =>
        new(
            id,
            role,
            LegacyB10655Source,
            fileName,
            sizeBytes,
            new Sha256Digest(sha256),
            LocalInferenceCatalogProvenance.NvidiaCair);
}
