using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.Tests;

public class LocalAiGpuVerificationTests
{
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
    public void HasRequiredGpuLoadEvidence_AcceptsFullOffloadNvmlDelta()
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
