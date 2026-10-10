using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>A byte-pinned file in the runnable llama.cpp payload.</summary>
public sealed record LlamaRuntimeFile(string FileName, long SizeBytes, Sha256Digest Sha256);

/// <summary>A native Windows llama.cpp runtime and every archive required to execute it.</summary>
public sealed record LlamaRuntimeVariant
{
    public LlamaRuntimeVariant(
        string id,
        Architecture architecture,
        Version cudaVersion,
        IReadOnlyList<PinnedArtifact> artifacts,
        string? releaseTag = null,
        IReadOnlyList<LlamaRuntimeFile>? requiredFiles = null)
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
        RequiredFiles = requiredFiles ?? [];
    }

    public string Id { get; }
    public Architecture Architecture { get; }
    public Version CudaVersion { get; }
    public IReadOnlyList<PinnedArtifact> Artifacts { get; }
    public IReadOnlyList<LlamaRuntimeFile> RequiredFiles { get; }
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
    public const string ReleaseTag = "b11320";
    public const string ReleaseCommitSha = "b8f96c3e82284028cb077811ed1666caac3c5bac";
    public const string ServerExecutableName = "llama-server.exe";
    public const string ServerImplementationLibraryName = "llama-server-impl.dll";
    public const string X64RuntimeId = "b11320-cuda13-x64";
    public const string Arm64RuntimeId = "b11320-cuda13-arm64";

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
                            "llama-b11320-cuda13-x64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b11320-bin-win-cuda-13.4-x64.zip",
                            152_787_584,
                            "75afa9d56077ebbd7a6123e923e8b6e82d4f4b1e53947ce04ca14ccbbfbba9c0"),
                        RuntimeArtifact(
                            "cudart-b11320-cuda13-x64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-x64.zip",
                            423_535_356,
                            "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668"),
                    }),
                requiredFiles: X64RuntimeFiles()),
            new LlamaRuntimeVariant(
                Arm64RuntimeId,
                Architecture.Arm64,
                new Version(13, 4),
                Array.AsReadOnly(
                    new[]
                    {
                        RuntimeArtifact(
                            "llama-b11320-cuda13-arm64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b11320-bin-win-cuda-13.4-arm64.zip",
                            144_946_754,
                            "9fcf3fb79c7d107b2fc60cff6e5947b133c965ca071ba0f8b05f641308b8e660"),
                        RuntimeArtifact(
                            "cudart-b11320-cuda13-arm64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                            153_262_407,
                            "642dcde8805b3e3165ca710a5443b3b4044b27d96bd3ee3132473988c9bcb774"),
                    }),
                requiredFiles: Arm64RuntimeFiles()),
        });

    // Retired from new installs and never offered or selected. These exist only so a
    // managed installation recorded before the runtime bump keeps resolving its own
    // receipt and stays launchable until setup upgrades it. Pins are reproduced
    // exactly as they were installed; nothing is remapped.
    private const string LegacyB11026ReleaseTag = "b11026";
    private const string LegacyB11026RuntimeIdX64 = "b11026-cuda13-x64";
    private const string LegacyB11026RuntimeIdArm64 = "b11026-cuda13-arm64";

    private static GitHubReleaseSource LegacyB11026Source { get; } = new(
        "ggml-org/llama.cpp",
        LegacyB11026ReleaseTag,
        "b49650adb31f2e49a0d76113aeb1792134fd8413");

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
            new LlamaRuntimeVariant(
                LegacyB11026RuntimeIdX64,
                Architecture.X64,
                new Version(13, 4),
                Array.AsReadOnly(
                    new[]
                    {
                        LegacyB11026Artifact(
                            "llama-b11026-cuda13-x64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b11026-bin-win-cuda-13.4-x64.zip",
                            150_102_391,
                            "6799f0962d066c54aee3773f0e5efa0076e46418695c0f4f6d24a38e7007dfb1"),
                        LegacyB11026Artifact(
                            "cudart-b11026-cuda13-x64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-x64.zip",
                            423_535_356,
                            "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668"),
                    }),
                LegacyB11026ReleaseTag,
                requiredFiles: LegacyB11026X64RuntimeFiles()),
            new LlamaRuntimeVariant(
                LegacyB11026RuntimeIdArm64,
                Architecture.Arm64,
                new Version(13, 4),
                Array.AsReadOnly(
                    new[]
                    {
                        LegacyB11026Artifact(
                            "llama-b11026-cuda13-arm64",
                            ArtifactRole.RuntimeBinary,
                            "llama-b11026-bin-win-cuda-13.4-arm64.zip",
                            142_993_717,
                            "d4a31d05b4fe997872020d81e8482e7712c7c254ec9c7ccdb9597ae2a31e6728"),
                        LegacyB11026Artifact(
                            "cudart-b11026-cuda13-arm64",
                            ArtifactRole.RuntimeDependency,
                            "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                            153_262_407,
                            "642dcde8805b3e3165ca710a5443b3b4044b27d96bd3ee3132473988c9bcb774"),
                    }),
                LegacyB11026ReleaseTag,
                requiredFiles: LegacyB11026Arm64RuntimeFiles()),
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

    private static IReadOnlyList<LlamaRuntimeFile> X64RuntimeFiles() =>
    [
        RuntimeFile("cublas64_13.dll", 54_942_320, "1119dbca0a808e0c8850bb4221e330daf0b5ddb348d62d37b6ac533854723df9"),
        RuntimeFile("cublasLt64_13.dll", 492_752_496, "0f5bc315fef706b5626248ebd74e8a48a9e82fa2f0cad38330f5f70984913107"),
        RuntimeFile("cudart64_13.dll", 551_024, "05bfafcb97bd53b0089568a96e1e5bd6921637bd0853ff7f5342bd31a6a16890"),
        RuntimeFile("ggml-base.dll", 801_280, "777562364f0528eaadd7865faf0e6e0b17ff3904e50a18438caebc4bfd0790e3"),
        RuntimeFile("ggml-cpu-alderlake.dll", 1_431_552, "6201c30f8b96e131654d196125c659d554ba478294c0703da0b7a20a89618122"),
        RuntimeFile("ggml-cpu-cannonlake.dll", 1_657_856, "39a18866d22962cf20c604c93f877b351bd280d798d794c8e8905d03ca73c228"),
        RuntimeFile("ggml-cpu-cascadelake.dll", 1_642_496, "e74f4902fa3bd58ccdfe75c36517a33574eeabb370e18b7ca803b9ee3f0e5113"),
        RuntimeFile("ggml-cpu-cooperlake.dll", 1_643_520, "65bde4b5d95ff05523dbd3a403abbf47de569fae92708a38ebfd8abfa934a64c"),
        RuntimeFile("ggml-cpu-haswell.dll", 1_436_160, "a54e0bb576b0b17453d0db649d0f367b59aeeaab9f5e394df501891dc7437142"),
        RuntimeFile("ggml-cpu-icelake.dll", 1_649_152, "4734e2f5ad4a9003e0345d2dd785598fd8ea1af1f23408316b14aa54f2fbd855"),
        RuntimeFile("ggml-cpu-ivybridge.dll", 1_307_136, "a6bc4be1e4cec08406fddead48feafc29dc5cc2ee3d4a30367335dc0ec277b8b"),
        RuntimeFile("ggml-cpu-piledriver.dll", 1_310_208, "e86eb4d9c67a713b3a9faf7ffd430fc933d88fa993c150ba15fcc1ccc9edf334"),
        RuntimeFile("ggml-cpu-sandybridge.dll", 1_286_656, "b2a4ac1f2a4391a707b23848a10326e42d86abf6e31ab3f1aa31eac8ff1a5f0c"),
        RuntimeFile("ggml-cpu-sapphirerapids.dll", 1_920_000, "85b7c5163e8be5576e2bc87678c2a979c6d85b70be1b78317f0e7ee7324ff87e"),
        RuntimeFile("ggml-cpu-skylakex.dll", 1_651_712, "572c006b7ab4df21f35f469ff7b20b9959a97097d2edb3f1fb28ee834c4c8f2f"),
        RuntimeFile("ggml-cpu-sse42.dll", 916_480, "de669e22953f32567c0512199448fbd3259f96bd90b1d41967d42479a6d4f1be"),
        RuntimeFile("ggml-cpu-x64.dll", 908_800, "95660d872ed83b6335708dbf539d1b15ca60ea5832a0aed59e9c6d38d6ed468e"),
        RuntimeFile("ggml-cpu-zen4.dll", 1_649_664, "e75dea4fc377ffe75096cdca48cd26fc4777f4ace76c94483a2414c8c41b7b02"),
        RuntimeFile("ggml-cuda.dll", 147_597_824, "af6121ddd035db3fddb90bdc7dbbef8bfb244db522157af459b9682f0d18934e"),
        RuntimeFile("ggml-rpc.dll", 168_448, "29cd136e276a998b7d1ec43b5dd2d6d9dee837c38bbae73ffbc02e015584c740"),
        RuntimeFile("ggml.dll", 79_872, "96a162e0be5a3c798af6b31a5d21c1bd04493914e7b11ae8f9212d0bf0996e6c"),
        RuntimeFile("libomp.dll", 768_000, "a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c"),
        RuntimeFile("llama-common.dll", 7_891_968, "a6a87f92224a11a562f5397554119882d81b9e0038ea98cf8aa4edc34f3d6d2d"),
        RuntimeFile("llama-server-impl.dll", 8_946_688, "6dfeb749b21b6cd3f4a64d0319b75a090be3d79f222053239ac6a4527a5386ed"),
        RuntimeFile("llama-server.exe", 9_216, "96bfefe33c2cf0f1f421cae2f9e1e4a385264c8d4d731f7b08ff880edbb4f133"),
        RuntimeFile("llama.dll", 3_270_656, "cfbf1a2ac5d676ccb3c7685098877f832a90285ad21575faf5a71eefece7f759"),
        RuntimeFile("mtmd.dll", 1_794_560, "c29eacab7497aa8b18951ea2e7466c8fd8867ff49d122322a203e8d6c7a87dfa"),
    ];

    private static IReadOnlyList<LlamaRuntimeFile> Arm64RuntimeFiles() =>
    [
        RuntimeFile("cublas64_13.dll", 24_207_984, "49e8fa23d88ac0cbae27e200fc092b894dd55b4de5cb4293993986e29fb9b65a"),
        RuntimeFile("cublasLt64_13.dll", 193_128_560, "daf579ae36bb3c85e2340c57a1994f634e07c6f55ef70dd4cdc26695de21c752"),
        RuntimeFile("cudart64_13.dll", 606_832, "bd927ddf03823eeead7c8b261a8669d96764b89109311f4ab6accde8b7d97ec2"),
        RuntimeFile("ggml-base.dll", 664_576, "4817c34746df9b1b519d3da9140e61752c4a852a4d1e9df63dabd8ae069bd03e"),
        RuntimeFile("ggml-cpu.dll", 834_560, "2896d13bf7b05f80b0f4f1e4d1f85c19c3ebc207757117ea709a8e4dfe0914f7"),
        RuntimeFile("ggml-cuda.dll", 145_212_928, "c877fcb29338139d502065d56bf93e1bbd8b3092ddc2dd032bd47b6395b2b8b3"),
        RuntimeFile("ggml-rpc.dll", 155_648, "7b237414f5739105c41cf55cd4ad9e7c14f13ba417253d7bb3bfac961406a9cb"),
        RuntimeFile("ggml.dll", 70_656, "09f47cabdce4c7efd13b68b5f4206b276516f89e09b946460b048c7eb5d215ff"),
        RuntimeFile("libomp.dll", 764_928, "26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a"),
        RuntimeFile("llama-common.dll", 6_983_680, "fca38964f724eb153bb0d35926a397cfa010142cbeb6649f63d13a64b090bfc9"),
        RuntimeFile("llama-server-impl.dll", 8_201_728, "cb751a06de6ac4fef398704c26807ff2a1740d6711d9f2ab7318ccb27d3e77f9"),
        RuntimeFile("llama-server.exe", 9_728, "67a7789b17b2ec98ca87c079bf6686da7e5b9e56d4d9d6df8c3a08d4b654b6b5"),
        RuntimeFile("llama.dll", 2_864_640, "9d18dfdd61e5f027a1763196f6c0d258c3e2307e6f4886da51679317dd779d8c"),
        RuntimeFile("mtmd.dll", 1_507_328, "d47dc5f6441efa1de3176f60da2c5da28c4da411807be6c6b8a9c054f61cb37a"),
    ];

    private static IReadOnlyList<LlamaRuntimeFile> LegacyB11026X64RuntimeFiles() =>
    [
        RuntimeFile("cublas64_13.dll", 54_942_320, "1119dbca0a808e0c8850bb4221e330daf0b5ddb348d62d37b6ac533854723df9"),
        RuntimeFile("cublasLt64_13.dll", 492_752_496, "0f5bc315fef706b5626248ebd74e8a48a9e82fa2f0cad38330f5f70984913107"),
        RuntimeFile("cudart64_13.dll", 551_024, "05bfafcb97bd53b0089568a96e1e5bd6921637bd0853ff7f5342bd31a6a16890"),
        RuntimeFile("ggml-base.dll", 796_672, "9591ebfaf72ef7c2e75a432cf01ba6bed0d5456da7b90309936fe09e0500ba68"),
        RuntimeFile("ggml-cpu-alderlake.dll", 1_231_360, "0969dbeed91f774458515a8fd921726f5942f5137ec5a5997a0880d3f02a038e"),
        RuntimeFile("ggml-cpu-cannonlake.dll", 1_449_984, "2836bd703fff44f0eeb9941ef18969bba98f13122e2024d4a8bc15a85fae42e0"),
        RuntimeFile("ggml-cpu-cascadelake.dll", 1_435_136, "6725778107d185f4e05f2d3f8d659fcd4542afb009818ac3be203008031e3450"),
        RuntimeFile("ggml-cpu-cooperlake.dll", 1_435_648, "53840f9263acb66a9ca3bb622519b278137d78f39222e8f605d5434bb612c463"),
        RuntimeFile("ggml-cpu-haswell.dll", 1_236_992, "f0ba6f9a021844633d9f6ad698611a0feb1edcdf53314d6caaaa4e471198388b"),
        RuntimeFile("ggml-cpu-icelake.dll", 1_441_792, "33eb7a600f3e8b8f3a6f7247514811f5be2ee7ab1c633c0c0f122203e53b7e43"),
        RuntimeFile("ggml-cpu-ivybridge.dll", 1_128_448, "7ac1bfb78ace2334434f53b83e77efa04f674645747629580b256d8c7e13f8ba"),
        RuntimeFile("ggml-cpu-piledriver.dll", 1_132_544, "dc110e4efce523b2e8546902978feb5d55827183e8ac5bcf30406f8ca6ab5923"),
        RuntimeFile("ggml-cpu-sandybridge.dll", 1_108_992, "955481a44165fc4e88e01d8ebf77136b6221416e199e43bfab5fde803010f69c"),
        RuntimeFile("ggml-cpu-sapphirerapids.dll", 1_712_640, "e1bc4661f5ccc88d453ab1ca6de03bad76e146c552617c1a958012d50fc24bc0"),
        RuntimeFile("ggml-cpu-skylakex.dll", 1_443_328, "8d307a16b28688dcd482d6d5936483a6ef73e2f48eb44b411dee551d94f9d4d8"),
        RuntimeFile("ggml-cpu-sse42.dll", 933_376, "893ed3a1a926ffa701e0f60cc24a44b15806bba55ac5f3436b2cf38e9d03e4f4"),
        RuntimeFile("ggml-cpu-x64.dll", 925_184, "388d4169ff64fea07a2c05957514ab9de772bd59c3a4aed520cdc68eeb88395b"),
        RuntimeFile("ggml-cpu-zen4.dll", 1_442_304, "455347a1c14ae54e46aaa502b8e5190aedebc5f7f86fbaef3961a20eb48a7bde"),
        RuntimeFile("ggml-cuda.dll", 145_156_096, "f9301c1aa3c6b0ccf79909ab79e167aaf253d63518325381ce50ec3b60ed853e"),
        RuntimeFile("ggml-rpc.dll", 168_448, "64b0b9eb3fa10fbe254305797fbf8608ef0ec669e14a19ea9ced9891ddd2f42a"),
        RuntimeFile("ggml.dll", 79_872, "5b8379cdff3fb1696f792ac15752efc4403211eeeb1ed5807d081689559d72f5"),
        RuntimeFile("libomp.dll", 768_000, "a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c"),
        RuntimeFile("llama-common.dll", 7_775_744, "aa4670c731d4716d3f4e24035ed44b5b16a0137fd25e258d52c9a0fe0d88e931"),
        RuntimeFile("llama-server-impl.dll", 8_904_704, "2f3c6bed1270f77d74650c4124229a48e5248c39971da00b1a6fafc29fe77ae0"),
        RuntimeFile("llama-server.exe", 9_216, "bfb73ca63cbac5835681d8a489d22617995c85d4ca93ee36e2fd36508c96646d"),
        RuntimeFile("llama.dll", 3_167_232, "520e77d1e72831ef27ae892553087b85a76fe54052aba20493cc6358c51590d7"),
        RuntimeFile("mtmd.dll", 1_772_032, "131d3ddce28051fd48ca07a0fa9128fb35996631afa516e43ecc5094b7aa402f"),
    ];

    private static IReadOnlyList<LlamaRuntimeFile> LegacyB11026Arm64RuntimeFiles() =>
    [
        RuntimeFile("cublas64_13.dll", 24_207_984, "49e8fa23d88ac0cbae27e200fc092b894dd55b4de5cb4293993986e29fb9b65a"),
        RuntimeFile("cublasLt64_13.dll", 193_128_560, "daf579ae36bb3c85e2340c57a1994f634e07c6f55ef70dd4cdc26695de21c752"),
        RuntimeFile("cudart64_13.dll", 606_832, "bd927ddf03823eeead7c8b261a8669d96764b89109311f4ab6accde8b7d97ec2"),
        RuntimeFile("ggml-base.dll", 660_992, "b1d5638d0ef6c85bee3260a8f1b73ee1ad7c9ca7af8f8da9781401a87c7eaa90"),
        RuntimeFile("ggml-cpu.dll", 842_240, "58cb47330f127ac91292f38f34ad43159d0810bfbe607c9d3171f9bf37cdb872"),
        RuntimeFile("ggml-cuda.dll", 142_825_984, "2eccae00ceb715b3e80a9fe35a17215cd6e89461b20babeca4733efaf7cb404c"),
        RuntimeFile("ggml-rpc.dll", 155_648, "9ab1462ea0e6dcb9f7c419b5de6521d14c35b1ce5d5c747aa53fd948f4b42565"),
        RuntimeFile("ggml.dll", 70_656, "ac57ba4b185be40f73e7812964ffc4cdc0baed991d819de0dfa87efaabeff597"),
        RuntimeFile("libomp.dll", 764_928, "26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a"),
        RuntimeFile("llama-common.dll", 6_897_152, "be077fc2a2dbc381ccf60d5064c482705e8b22338444687d2ee3e7fe90c8146e"),
        RuntimeFile("llama-server-impl.dll", 8_159_232, "86d2a84bbb5ab58fddba842865317aab996fe731ac97ec334b54d556d04ffa90"),
        RuntimeFile("llama-server.exe", 9_728, "6971517a6fb336d3bafccc7e2bef83e2d9a37e6bb73773e5ce174b9a6b0b8d83"),
        RuntimeFile("llama.dll", 2_775_040, "e50d26edb2e174f65156b46bb00c5d69c824cae5582a5b18eb3df7bf9b65afb3"),
        RuntimeFile("mtmd.dll", 1_486_336, "b202b9b04b386570c772c06ec643736f63c0e5ff128dad1a085fc46c1e9b68b7"),
    ];

    private static LlamaRuntimeFile RuntimeFile(string fileName, long sizeBytes, string sha256) =>
        new(fileName, sizeBytes, new Sha256Digest(sha256));

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

    private static PinnedArtifact LegacyB11026Artifact(
        string id,
        ArtifactRole role,
        string fileName,
        long sizeBytes,
        string sha256) =>
        new(
            id,
            role,
            LegacyB11026Source,
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
