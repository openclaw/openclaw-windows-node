using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

public static partial class LlamaRuntimeCatalog
{
    // Bolt uses upstream LiLiCorr support for the public drafter tensor names.
    // Keep its runtime pin independent from the stable runtime used by other models.
    public const string NemotronBoltRuntimeFamily = "nemotron-bolt-lilicorr";
    public const string NemotronBoltSourceCommitSha = "23f7aabc16c72f6f707dba227251c35d7bd1ed96";
    public const string NemotronBoltReleaseTag = "b11700";
    public const string NemotronBoltArm64RuntimeId = "bolt-lilicorr-b11700-cuda13-arm64";

    private static readonly LlamaRuntimeVariant s_nemotronBoltArm64 = new(
        NemotronBoltArm64RuntimeId,
        Architecture.Arm64,
        new Version(13, 4),
        Array.AsReadOnly(new[]
        {
            new PinnedArtifact(
                "llama-bolt-lilicorr-b11700-cuda13-arm64",
                ArtifactRole.RuntimeBinary,
                new GitHubReleaseSource("ggml-org/llama.cpp", NemotronBoltReleaseTag, NemotronBoltSourceCommitSha),
                "llama-b11700-bin-win-cuda-13.4-arm64.zip",
                146_943_791,
                new Sha256Digest("8474f9edb24e917e5bf0c3824dd79bda5659483ee6adfcbf27197d05defb2783")),
            new PinnedArtifact(
                "cudart-b11320-cuda13-arm64",
                ArtifactRole.RuntimeDependency,
                new GitHubReleaseSource("ggml-org/llama.cpp", ReleaseTag, ReleaseCommitSha),
                "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                153_262_407,
                new Sha256Digest("642dcde8805b3e3165ca710a5443b3b4044b27d96bd3ee3132473988c9bcb774"),
                LocalInferenceCatalogProvenance.NvidiaCair),
        }),
        releaseTag: NemotronBoltReleaseTag,
        family: NemotronBoltRuntimeFamily,
        requiredFiles: Array.AsReadOnly(new[]
        {
            RuntimeFile("cublas64_13.dll", 24_207_984, "49e8fa23d88ac0cbae27e200fc092b894dd55b4de5cb4293993986e29fb9b65a"),
            RuntimeFile("cublasLt64_13.dll", 193_128_560, "daf579ae36bb3c85e2340c57a1994f634e07c6f55ef70dd4cdc26695de21c752"),
            RuntimeFile("cudart64_13.dll", 606_832, "bd927ddf03823eeead7c8b261a8669d96764b89109311f4ab6accde8b7d97ec2"),
            RuntimeFile("ggml-base.dll", 667_136, "ad891be29b12d53c5e7f5b681b7ba9ebedb5101f04314d0cbd83795bcb460896"),
            RuntimeFile("ggml-cpu.dll", 835_072, "e49583ceb5ad0c076f2b9905bac66eb1dd49d34fda14f0a4f4fd368fa2b9fef8"),
            RuntimeFile("ggml-cuda.dll", 146_929_152, "e3c7a7b5a79cc71cb667957ad2e5b28644ea843344c217a31fde13ed22e1e8ae"),
            RuntimeFile("ggml-rpc.dll", 215_040, "e7617fb63ae34218fbc15d4644057c4f44a3590afc21f8bfb4cabe6030996fc5"),
            RuntimeFile("ggml.dll", 69_120, "cca7613b2124c1b2cd9e53663269cd64fbcb1b480a465872915130b967c50fec"),
            RuntimeFile("llama-common.dll", 7_157_760, "b88408489b7445b4bba94cccf18d54b6f07618fae7a8a643c5db3d768f7fb5d6"),
            RuntimeFile("llama-server-impl.dll", 8_471_040, "43ff8798b8bf397c6fd39cb47a0366a9a5fa0a72c17185e51261747451542349"),
            RuntimeFile("llama-server.exe", 9_728, "acaea7451d287ee8812bc2f8175594cbb43008313450ecc22e6e2f4b69b6220d"),
            RuntimeFile("llama.dll", 3_121_152, "bf1083da93ce656214a74cae8fdad3580ee96e0bd4e8b27e65c8b6b65c5d3005"),
            RuntimeFile("mtmd.dll", 1_576_960, "4b34d7a35913a85a013b1050c143480c87c18aeb7f1421c0574447c2203270e4"),
            RuntimeFile("libomp.dll", 764_928, "26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a"),
        }));
}
