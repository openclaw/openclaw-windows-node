using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>PrismML's ternary kernels are required for PTQ1_0; stock llama.cpp is incompatible.</summary>
public static class BonsaiRuntimeCatalog
{
    public const string Family = "prism-llama.cpp";
    public const string ReleaseTag = "prism-b10772-e8fd209";
    private static readonly GitHubReleaseSource Source = new(
        "PrismML-Eng/llama.cpp", ReleaseTag, "e8fd209c975c80aa7fb36a8423571ff3b1b7e955");

    public static LlamaRuntimeVariant X64 { get; } = new(
        "prism-b10772-cuda13-x64", Architecture.X64, new Version(13, 3),
        [
            new("prism-b10772-cuda13-x64", ArtifactRole.RuntimeBinary, Source,
                "llama-prism-b10772-e8fd209-bin-win-cuda-13.3-x64.zip", 179_142_811,
                new Sha256Digest("984512655d5797835308ae7466c9d144f23ac511c1f18112806468c6bfb8876e")),
            new("prism-b10772-cudart13-x64", ArtifactRole.RuntimeDependency, Source,
                "cudart-llama-bin-win-cuda-13.3-x64.zip", 390_970_417,
                new Sha256Digest("1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e")),
        ], ReleaseTag, Files(), Family);

    public static LlamaRuntimeVariant? Find(Architecture architecture) =>
        architecture == Architecture.X64 ? X64 : null;

    private static IReadOnlyList<LlamaRuntimeFile> Files() =>
    [
        new("cublas64_13.dll", 51_870_320, new Sha256Digest("f1d500d0cd892f5b8c6b6cdbffd82d0c55d5f5427215668e7ceb55aeeccc1b63")),
        new("cublasLt64_13.dll", 460_301_424, new Sha256Digest("b592cd016d7673e9cb97716a22b27c4010ee635377a3ba28f37070a9bdb76a68")),
        new("cudart64_13.dll", 551_024, new Sha256Digest("b00ca6f53699120da815bf3e06e2e4285fae2f201235b883dcbb50eec51e2a2a")),
        new("ggml-base.dll", 674_304, new Sha256Digest("060073a280e093bbff389fe09d8aed1c1996e883428fd3eebefe4496aec3f9ba")),
        new("ggml-cpu-alderlake.dll", 980_480, new Sha256Digest("95200b9234a831827781baf3acde27cfda1822f0ec564d9c74c375052422a6e0")),
        new("ggml-cpu-cannonlake.dll", 1_217_536, new Sha256Digest("b9ecd088887e8ffb2ef72f44e5a08fc76946581377a466be56beb8d88f3ac404")),
        new("ggml-cpu-cascadelake.dll", 1_218_560, new Sha256Digest("1d72713556e07078a976e369d19158a40205bc7eda6c1c8d0a2823d4d594910e")),
        new("ggml-cpu-haswell.dll", 983_040, new Sha256Digest("9d96df29f6ca35aa805edebbc62ca6a91d99759afbd82377bedd885824f3f5ca")),
        new("ggml-cpu-icelake.dll", 1_218_560, new Sha256Digest("c4a0e9b1df2b3bfd77cd9976536af0d1bb51638fb1ac77e7de1aea916d8e7234")),
        new("ggml-cpu-sandybridge.dll", 889_856, new Sha256Digest("9b0f9a4b1e7436c5ab70481081e5888ef679f66b22c4774602e60f6a0b5c03e8")),
        new("ggml-cpu-skylakex.dll", 1_217_536, new Sha256Digest("d4339ff1c1575a00236e3972e45ddda714dea24504a4b171596e0b978e6713ff")),
        new("ggml-cpu-sse42.dll", 809_984, new Sha256Digest("6e55c107285cfd328407bb9cec5c36e79f8deaef2a82ef78720676d990fcba66")),
        new("ggml-cpu-ssse3.dll", 825_856, new Sha256Digest("b5e78c258ae507326b028a47e458cccd68921af5df2549d98a26702e319fdc6e")),
        new("ggml-cpu-x64.dll", 811_008, new Sha256Digest("d43093e2644f2aa0f13730f635557fb15a85bc93a61802aa67132e243f76ffe8")),
        new("ggml-cuda.dll", 180_640_768, new Sha256Digest("df27ccd87bf41fa78a3a1f737c7abe91c40dccd384922464cabd2cbcb34921d5")),
        new("ggml-rpc.dll", 124_416, new Sha256Digest("65af4c4b557009fedf08fe9c6d364c92e71f0a587e2a9ecea66a1e19011d9557")),
        new("ggml.dll", 66_560, new Sha256Digest("d072d1d3f7ae2a454758f52155d8fc163cc4e6aa2ded00e5d1215d0b22a4a430")),
        new("llama-common.dll", 9_692_672, new Sha256Digest("fae4f7e0682c9c8199ed61a12af4f91920bf8f14463d432ae2e9112632dba738")),
        new("llama-server-impl.dll", 7_484_928, new Sha256Digest("e48ca90b072a2ecb59868a290f5f0bf2b17c46e7fa59fa8d1d0cd1490babc76b")),
        new("llama-server.exe", 10_752, new Sha256Digest("4047b972165ae40c66f66772a909c8739540a5acd3643ef9770db30325f162a8")),
        new("llama.dll", 2_574_848, new Sha256Digest("d6e06b2255b7982d0982c3cabda37560f22e4be81d012560f5c9614962c4e2c3")),
        new("mtmd.dll", 2_377_728, new Sha256Digest("362830fe81e3e34e4528a800d77a52687a8838eb5d7678d4f5d0c083fc638631")),
    ];
}
