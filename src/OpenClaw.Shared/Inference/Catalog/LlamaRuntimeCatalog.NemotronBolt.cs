using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

public static partial class LlamaRuntimeCatalog
{
    // Bolt needs LiLiCorr decoding from support-lilicorr plus per-layer expert loading.
    // Pin the tested source and payload here while other models keep the stable runtime.
    // Base: https://github.com/frozenblade1224/llama.cpp/tree/a972392d7db3678954c66f06744e535519314009
    public const string NemotronBoltRuntimeFamily = "nemotron-bolt-lilicorr";
    public const string NemotronBoltSourceCommitSha = "b3e4f6ce7f22adb7f1b1eaa012db8367d45a574a";
    public const string NemotronBoltReleaseTag = "bolt-lilicorr-b3e4f6c";
    public const string NemotronBoltArm64RuntimeId = "bolt-lilicorr-b3e4f6c-cuda13-arm64";

    private static readonly LlamaRuntimeVariant s_nemotronBoltArm64 = new(
        NemotronBoltArm64RuntimeId,
        Architecture.Arm64,
        new Version(13, 4),
        Array.AsReadOnly(new[]
        {
            new PinnedArtifact(
                "llama-bolt-lilicorr-b3e4f6c-cuda13-arm64",
                ArtifactRole.RuntimeBinary,
                new GitHubReleaseSource("joelagnel/llama.cpp", NemotronBoltReleaseTag, NemotronBoltSourceCommitSha),
                "llama-bolt-lilicorr-b3e4f6c-bin-win-cuda-13.4-arm64.zip",
                46_836_189,
                new Sha256Digest("f2adf45fda36a336d2a539e418c6e26f51d50d6f7bb3c086c79771ea348a9ee5")),
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
            RuntimeFile("ggml-base.dll", 606_720, "a8e7cfb6f5ad7b866d08b5c012cf9469a9b61456f62d76137db00f25031ce4f4"),
            RuntimeFile("ggml-cpu.dll", 667_136, "5e1fa7cd5d38f218714dd243e3f7079a01e11e7dd02c62490ab33eb5396b9b7a"),
            RuntimeFile("ggml-cuda.dll", 54_052_864, "edf5fe4a86c17b0b64e16f2d634686f929133cfce8802dc7ab726d710afae0a4"),
            RuntimeFile("ggml.dll", 49_664, "b2b8d88e1c1b9a95581a5690ddd5b1691099c2e6d5b7e1794185bac615a12c6d"),
            RuntimeFile("llama-common.dll", 8_754_688, "c3f9a2d9756a222f359b14b21c0464d1fb2ba19ae5d77ee3333515086e454a83"),
            RuntimeFile("llama-server-impl.dll", 3_414_016, "a2e42c8e93194fe67b463b708d7ec0e9f4c10fa2b4707b60126f50a07c463558"),
            RuntimeFile("llama-server.exe", 10_240, "33eea374c24f120b9eaf2303d646f67af41d2db4b37e589276f86340e6efb4ca"),
            RuntimeFile("llama.dll", 2_708_992, "a85d7984020e23437a0ea91adce0142b6b774925f3301c80dedd8b8961188bcd"),
            RuntimeFile("mtmd.dll", 2_408_448, "1d12a069d7cb94bcca9e1d4647108a9bee8d3cd45bdc87623884c23888094f5b"),
            RuntimeFile("vcomp140.dll", 367_160, "1a455c526e42d80d3aa3189e082500b6eb7837fcf8be0e9fbd1f26c92acdab71"),
        }));
}
