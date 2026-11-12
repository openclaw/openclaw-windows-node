using System.Buffers.Binary;
using System.Text;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class LocalCustomModelTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const string Revision = "50968a4468ef4233ed78cd7c3de230dd1d61a56b";

    [Theory]
    [InlineData("unsloth/Qwen3-8B-GGUF", "unsloth/Qwen3-8B-GGUF", "main", null, null)]
    [InlineData("unsloth/Qwen3-8B-GGUF:Q4_K_M", "unsloth/Qwen3-8B-GGUF", "main", null, "Q4_K_M")]
    [InlineData("unsloth/Qwen3-8B-GGUF/sub/Qwen3-8B-Q8_0.gguf", "unsloth/Qwen3-8B-GGUF", "main", "sub/Qwen3-8B-Q8_0.gguf", null)]
    [InlineData("https://huggingface.co/unsloth/Qwen3-8B-GGUF", "unsloth/Qwen3-8B-GGUF", "main", null, null)]
    [InlineData("hf.co/unsloth/Qwen3-8B-GGUF/tree/v2", "unsloth/Qwen3-8B-GGUF", "v2", null, null)]
    [InlineData("https://huggingface.co/unsloth/Qwen3-8B-GGUF/blob/main/Qwen3-8B-Q4_K_M.gguf?download=true",
        "unsloth/Qwen3-8B-GGUF", "main", "Qwen3-8B-Q4_K_M.gguf", null)]
    [InlineData("https://www.huggingface.co/unsloth/Qwen3-8B-GGUF/resolve/abc123/x/y.gguf",
        "unsloth/Qwen3-8B-GGUF", "abc123", "x/y.gguf", null)]
    public void Reference_AcceptsSupportedForms(string input, string repo, string revision, string? path, string? quant)
    {
        Assert.True(HuggingFaceModelReference.TryParse(input, out HuggingFaceModelReference? reference, out string error), error);
        Assert.Equal(new HuggingFaceModelReference(repo, revision, path, quant), reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/unsloth/Qwen3-8B-GGUF")]
    [InlineData("unsloth/Qwen3-8B-GGUF/../secret.gguf")]
    [InlineData("unsloth/Qwen3-8B-GGUF/readme.md")]
    [InlineData("unsloth/Qwen3-8B-GGUF:Q4 K")]
    [InlineData("just-a-name")]
    public void Reference_RejectsUnsafeOrUnsupportedInput(string input)
    {
        Assert.False(HuggingFaceModelReference.TryParse(input, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task Gguf_ReadsGeometryUsingMaxOfPerLayerHeadCountAndSkipsOtherValues()
    {
        byte[] header = Gguf(w =>
        {
            w.String("general.architecture", "qwen3");
            w.StringArray("tokenizer.ggml.tokens", ["a", "b", "c"]);
            w.UInt32("qwen3.block_count", 28);
            w.UInt32("qwen3.context_length", 40960);
            w.Int32Array("qwen3.attention.head_count_kv", [0, 8, 4]);
            w.UInt32("qwen3.attention.head_count", 16);
            w.UInt32("qwen3.embedding_length", 1024);
            w.Float32("qwen3.rope.freq_base", 1_000_000f);
        });

        GgufModelMetadata metadata = await GgufMetadataReader.ReadAsync(new MemoryStream(header), CancellationToken.None);

        Assert.Equal(new GgufModelMetadata("qwen3", null, 28, 40960, 8, 64), metadata);
    }

    [Fact]
    public async Task Gguf_RejectsBadMagicTruncationAndMissingLayout()
    {
        byte[] valid = Gguf(w => w.String("general.architecture", "llama"));
        byte[] badMagic = (byte[])valid.Clone();
        badMagic[0] = (byte)'X';

        await Assert.ThrowsAsync<InvalidDataException>(() => GgufMetadataReader.ReadAsync(new MemoryStream(badMagic), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => GgufMetadataReader.ReadAsync(new MemoryStream(valid[..^3]), default));
        InvalidDataException missing = await Assert.ThrowsAsync<InvalidDataException>(
            () => GgufMetadataReader.ReadAsync(new MemoryStream(valid), default));
        Assert.Contains("attention layout", missing.Message);
    }

    [Fact]
    public void CustomModel_IdIsStableSafeAndDistinctFromCatalog()
    {
        LocalCustomModelRecipe recipe = Recipe(5 * GiB, trainedContext: 40960);
        string id = LocalCustomModel.CreateId(recipe);

        Assert.Equal(id, LocalCustomModel.CreateId(recipe with { DisplayName = "renamed" }));
        Assert.NotEqual(id, LocalCustomModel.CreateId(recipe with { FilePath = "other.gguf" }));
        Assert.Matches("^hf-[0-9a-f]{16}$", id);
        Assert.True(LocalModelCatalog.RequiresRecordedProfile(id));
        Assert.All(LocalModelCatalog.Models, model => Assert.False(LocalCustomModel.IsCustomId(model.Id)));
        Assert.Equal("Q4_K_M", LocalCustomModel.ToModelInfo(recipe).Quantization);
    }

    [Fact]
    public void CustomModel_ProfilesNeverExceedTrainedContext()
    {
        Assert.Equal(["ctx-40960-f16", "ctx-40960-q8_0"],
            LocalCustomModel.GetProfiles(Recipe(GiB, trainedContext: 40960)).Select(p => p.Id));
        Assert.Equal(["ctx-131072-f16", "ctx-131072-q8_0", "ctx-65536-f16", "ctx-65536-q8_0"],
            LocalCustomModel.GetProfiles(Recipe(GiB, trainedContext: 131072)).Select(p => p.Id));
    }

    [Fact]
    public void CustomModel_FitsOrReportsInsufficientMemoryWithSmallestPlan()
    {
        HostHardwareInfo hardware = Hardware(24 * GiB);

        LocalInferenceEligibilityResult fits = LocalInferenceEligibility.EvaluateCustom(
            hardware, LocalCustomModel.ToModelInfo(Recipe(5 * GiB, trainedContext: 40960)));
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, fits.Status);
        Assert.Equal("ctx-40960-f16", fits.Plan!.Profile.Id);

        LocalInferenceEligibilityResult tooBig = LocalInferenceEligibility.EvaluateCustom(
            hardware, LocalCustomModel.ToModelInfo(Recipe(30 * GiB, trainedContext: 40960)));
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, tooBig.FailureCode);
        Assert.Equal("ctx-40960-q8_0", tooBig.Plan!.Profile.Id);
        Assert.Equal("GPU-test", tooBig.SelectedGpu!.StableId);
    }

    private static LocalCustomModelRecipe Recipe(long size, int trainedContext) =>
        new("unsloth/Qwen3-8B-GGUF", Revision, "Qwen3-8B-Q4_K_M.gguf", size, new string('a', 64),
            "Qwen3-8B-Q4_K_M", "qwen3", 36, 8, 128, trainedContext);

    private static HostHardwareInfo Hardware(long vram) =>
        new(RuntimeArchitecture.X64, 64 * GiB, 48 * GiB,
            [new GpuInfo(GpuVendor.Nvidia, "NVIDIA Test", vram, vram, DriverVersion: "620.0", CudaMajorVersion: 13, StableId: "GPU-test")],
            false, new LocalAiDiskSpaceInfo(500 * GiB, 500 * GiB, true));

    private static byte[] Gguf(Action<GgufWriter> write)
    {
        var writer = new GgufWriter();
        write(writer);
        return writer.Build();
    }

    private sealed class GgufWriter
    {
        private readonly MemoryStream _body = new();
        private ulong _count;

        public void String(string key, string value) { Key(key); U32(8); Str(value); }
        public void UInt32(string key, uint value) { Key(key); U32(4); U32(value); }
        public void Float32(string key, float value) { Key(key); U32(6); Bytes(BitConverter.GetBytes(value)); }
        public void StringArray(string key, string[] values)
        {
            Key(key); U32(9); U32(8); U64((ulong)values.Length);
            foreach (string value in values) Str(value);
        }
        public void Int32Array(string key, int[] values)
        {
            Key(key); U32(9); U32(5); U64((ulong)values.Length);
            foreach (int value in values) Bytes(BitConverter.GetBytes(value));
        }

        public byte[] Build()
        {
            var header = new MemoryStream();
            header.Write("GGUF"u8);
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 3); header.Write(buffer[..4]);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0); header.Write(buffer);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, _count); header.Write(buffer);
            _body.WriteTo(header);
            return header.ToArray();
        }

        private void Key(string key) { _count++; Str(key); }
        private void Str(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); U64((ulong)bytes.Length); Bytes(bytes); }
        private void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); _body.Write(b); }
        private void U64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); _body.Write(b); }
        private void Bytes(byte[] value) => _body.Write(value);
    }
}
