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
        IReadOnlyList<PinnedArtifact> artifacts)
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
    }

    public string Id { get; }
    public Architecture Architecture { get; }
    public Version CudaVersion { get; }
    public IReadOnlyList<PinnedArtifact> Artifacts { get; }
    public long TotalDownloadSizeBytes => Artifacts.Sum(artifact => artifact.SizeBytes);
}

/// <summary>
/// Integrity-pinned native Windows llama.cpp builds routed by Windows CPU
/// architecture. Unsupported hardware does not receive a CPU or Vulkan fallback.
/// </summary>
public static class LlamaRuntimeCatalog
{
    public const string ReleaseTag = "b10488";
    public const string ReleaseCommitSha = "9d77fa17254e1dee4b9e92504c91611a60b1359f";
    public const string ServerExecutableName = "llama-server.exe";
    public const string X64RuntimeId = "b10488-cuda13-x64";
    public const string Arm64RuntimeId = "b10488-cuda13-arm64";

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
                new Version(13, 3),
                Array.AsReadOnly(
                    new[]
                    {
                        RuntimeArtifact(
                            "llama-b10488-cuda13-x64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b10488-bin-win-cuda-13.3-x64.zip",
                            146_824_581,
                            "f4ea53c2e7f3d295cb9fd092515d50af4969266b4cdae01f03a1cbaa8b4d9af0"),
                        RuntimeArtifact(
                            "cudart-b10488-cuda13-x64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.3-x64.zip",
                            390_970_417,
                            "1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e"),
                    })),
            new LlamaRuntimeVariant(
                Arm64RuntimeId,
                Architecture.Arm64,
                new Version(13, 4),
                Array.AsReadOnly(
                    new[]
                    {
                        RuntimeArtifact(
                            "llama-b10488-cuda13-arm64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b10488-bin-win-cuda-13.4-arm64.zip",
                            140_379_054,
                            "75554d62f4af8f4150d3b4b0cca7df62d44105e98fb7cd92ab2d177e382b441d"),
                        RuntimeArtifact(
                            "cudart-b10488-cuda13-arm64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                            153_318_797,
                            "5a40dc7c5fa3d0a80ceeba4f16f9e8d25d87bcf1399c9233588953c43436c33c"),
                    })),
        });

    public static IReadOnlyList<LlamaRuntimeVariant> Variants => s_variants;

    public static LlamaRuntimeVariant? Find(Architecture architecture) =>
        s_variants.SingleOrDefault(variant => variant.Architecture == architecture);

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
}
