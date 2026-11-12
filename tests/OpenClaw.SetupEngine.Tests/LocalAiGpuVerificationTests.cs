using System.Runtime.InteropServices;
using OpenClaw.Shared.Inference;
using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.Tests;

public class LocalAiGpuVerificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateCudaModulePath_ResolvesReceiptAliasAndRejectsExternalModule(bool external)
    {
        string logicalExe = Path.GetFullPath(Path.Combine("logical", "runtime", "llama-server.exe"));
        string physicalRoot = Path.GetFullPath(Path.Combine("physical", "runtime"));
        string module = Path.Combine(external ? physicalRoot + "-other" : physicalRoot, "ggml-cuda.dll");
        var resolved = new List<string>();
        string Resolve(string path)
        {
            resolved.Add(path);
            return path == logicalExe ? Path.Combine(physicalRoot, "llama-server.exe") : path;
        }

        if (external)
            Assert.Throws<InvalidDataException>(() =>
                VerifyLocalAiGpuLoadStep.ValidateCudaModulePath(logicalExe, module, Resolve));
        else
            VerifyLocalAiGpuLoadStep.ValidateCudaModulePath(logicalExe, module, Resolve);

        Assert.Equal(new[] { logicalExe, module }, resolved);
    }

    [Fact]
    public void ValidateCudaModulePath_FailsClosedWhenPhysicalPathCannotBeResolved()
    {
        Assert.Throws<IOException>(() => VerifyLocalAiGpuLoadStep.ValidateCudaModulePath(
            "llama-server.exe", "ggml-cuda.dll", _ => throw new IOException("unavailable")));
    }

    [Fact]
    public void ParseGpuLoadEvidence_ReadsFullOffloadAndCudaModelBuffer()
    {
        const string log = """
            load_tensors: offloaded 42/42 layers to GPU
            load_tensors:        CUDA0 model buffer size = 21087.70 MiB
            """;

        LocalAiGpuLogEvidence evidence = WindowsLocalAiGpuEvidenceProbe.ParseGpuLoadEvidence(log);

        Assert.Equal(42, evidence.OffloadedLayers);
        Assert.Equal(42, evidence.TotalLayers);
        Assert.Equal(22_112_056_115L, evidence.CudaModelBufferBytes);
    }

    [Fact]
    public void ParseGpuLoadEvidence_AllowsMissingCudaModelBuffer()
    {
        const string log = "load_tensors: offloaded 42/42 layers to GPU";

        LocalAiGpuLogEvidence evidence = WindowsLocalAiGpuEvidenceProbe.ParseGpuLoadEvidence(log);

        Assert.Null(evidence.CudaModelBufferBytes);
    }

    [Fact]
    public void ParseGpuLoadEvidence_BoltDraftDoesNotReplaceTargetEvidence()
    {
        const string log = """
            [123] srv    load_model: loading model 'bolt.gguf'
            [123] load_tensors: offloaded 89/89 layers to GPU
            [123] load_tensors:        CUDA0 model buffer size = 20446.77 MiB
            [123] common_speculative_init_result: loading draft model 'lilicorr.gguf'
            [123] load_tensors: offloaded 6/6 layers to GPU
            [123] load_tensors:        CUDA0 model buffer size = 944.48 MiB
            """;

        LocalAiGpuLogEvidence evidence = WindowsLocalAiGpuEvidenceProbe.ParseGpuLoadEvidence(log);

        Assert.Equal(89, evidence.OffloadedLayers);
        Assert.Equal(89, evidence.TotalLayers);
        Assert.Equal(21_439_992_299L, evidence.CudaModelBufferBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    public void ParseGpuLoadEvidence_FullDraftCannotProvePartialTargetOffload(int offloaded)
    {
        string log = $"""
            srv    load_model: loading model 'bolt.gguf'
            load_tensors: offloaded {offloaded}/89 layers to GPU
            load_tensors:        CUDA0 model buffer size = 19000.00 MiB
            common_speculative_init_result: loading draft model 'lilicorr.gguf'
            load_tensors: offloaded 6/6 layers to GPU
            load_tensors:        CUDA0 model buffer size = 944.48 MiB
            """;

        Assert.Throws<InvalidDataException>(() =>
            WindowsLocalAiGpuEvidenceProbe.ParseGpuLoadEvidence(log));
    }

    [Fact]
    public void ParseGpuLoadEvidence_OldFullLoadCannotProveLatestPartialLoad()
    {
        const string log = """
            [123] srv    load_model: loading model 'bolt.gguf'
            [123] load_tensors: offloaded 89/89 layers to GPU
            [123] load_tensors:        CUDA0 model buffer size = 20446.77 MiB
            [456] srv    load_model: loading model 'bolt.gguf'
            [456] load_tensors: offloaded 80/89 layers to GPU
            [456] load_tensors:        CUDA0 model buffer size = 19000.00 MiB
            """;

        Assert.Throws<InvalidDataException>(() =>
            WindowsLocalAiGpuEvidenceProbe.ParseGpuLoadEvidence(log));
    }

    [Fact]
    public void ResolveGpuMemoryEvidence_SkipsPostLoadProbeWhenCudaBufferIsReported()
    {
        const long totalBytes = 48L * 1024 * 1024 * 1024;
        const long freeBytes = 47L * 1024 * 1024 * 1024;
        var baseline = new HostHardwareInfo(
            Architecture.Arm64,
            null,
            null,
            [new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA RTX Spark N1X",
                GpuVisibleMemoryBytes: totalBytes,
                FreeGpuVisibleMemoryBytes: freeBytes,
                StableId: "000F:01:00.0")],
            VulkanAvailable: false);
        var logEvidence = new LocalAiGpuLogEvidence(42, 42, 21L * 1024 * 1024 * 1024);
        bool postLoadProbeCalled = false;

        var evidence = WindowsLocalAiGpuEvidenceProbe.ResolveGpuMemoryEvidence(
            "000F:01:00.0",
            baseline,
            logEvidence,
            () =>
            {
                postLoadProbeCalled = true;
                return HostHardwareInfo.Unknown;
            });

        Assert.False(postLoadProbeCalled);
        Assert.Equal(totalBytes, evidence.TotalBytes);
        Assert.Equal(freeBytes, evidence.FreeBeforeBytes);
        Assert.Null(evidence.FreeAfterBytes);
    }

    [Fact]
    public void HasRequiredGpuLoadEvidence_AcceptsFullOffloadCudaBuffer()
    {
        var evidence = new LocalAiGpuLoadEvidence(
            ProcessId: 123,
            SelectedGpuId: "GPU-123",
            CudaModulePath: @"C:\LocalAI\ggml-cuda.dll",
            OffloadedLayers: 42,
            TotalLayers: 42,
            TotalGpuVisibleBytes: 8L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesBeforeLoad: 7L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesAfterLoad: 6L * 1024 * 1024 * 1024,
            CudaModelBufferBytes: 21L * 1024 * 1024 * 1024);

        bool accepted = VerifyLocalAiGpuLoadStep.HasRequiredGpuLoadEvidence(
            evidence,
            minimumDeltaBytes: 10L * 1024 * 1024 * 1024);

        Assert.True(accepted);
    }

    [Fact]
    public void HasRequiredGpuLoadEvidence_AcceptsFullOffloadCudaDelta()
    {
        var evidence = new LocalAiGpuLoadEvidence(
            ProcessId: 123,
            SelectedGpuId: "GPU-123",
            CudaModulePath: @"C:\LocalAI\ggml-cuda.dll",
            OffloadedLayers: 42,
            TotalLayers: 42,
            TotalGpuVisibleBytes: 24L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesBeforeLoad: 20L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesAfterLoad: 5L * 1024 * 1024 * 1024,
            CudaModelBufferBytes: null);

        bool accepted = VerifyLocalAiGpuLoadStep.HasRequiredGpuLoadEvidence(
            evidence,
            minimumDeltaBytes: 10L * 1024 * 1024 * 1024);

        Assert.True(accepted);
    }

    [Fact]
    public void HasRequiredGpuLoadEvidence_RejectsWhenNeitherMemoryProofMeetsThreshold()
    {
        var evidence = new LocalAiGpuLoadEvidence(
            ProcessId: 123,
            SelectedGpuId: "GPU-123",
            CudaModulePath: @"C:\LocalAI\ggml-cuda.dll",
            OffloadedLayers: 42,
            TotalLayers: 42,
            TotalGpuVisibleBytes: 8L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesBeforeLoad: 7L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesAfterLoad: 6L * 1024 * 1024 * 1024,
            CudaModelBufferBytes: 2L * 1024 * 1024 * 1024);

        bool accepted = VerifyLocalAiGpuLoadStep.HasRequiredGpuLoadEvidence(
            evidence,
            minimumDeltaBytes: 10L * 1024 * 1024 * 1024);

        Assert.False(accepted);
    }
}
