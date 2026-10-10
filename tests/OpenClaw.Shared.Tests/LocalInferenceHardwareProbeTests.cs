using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using Xunit.Abstractions;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Hardware-gated proof of CUDA-only Local AI qualification.
/// Skipped when no NVIDIA GPU is present. Set OPENCLAW_RUN_GPU_PROOF=1 to require it.
/// </summary>
public sealed class LocalInferenceHardwareProbeTests(ITestOutputHelper output)
{
    [NvidiaHardwareFact]
    public void ProbeReportsCudaMemoryWithoutADedicatedMemoryCap()
    {
        var reader = new RecordingCudaDeviceReader();
        HostHardwareInfo hardware = new CudaHostHardwareProbe(reader).Probe();
        Assert.NotEmpty(hardware.NvidiaGpus);
        Assert.Equal(hardware.Gpus.Count, reader.MemoryReadings.Count);

        foreach ((GpuInfo gpu, (long FreeBytes, long TotalBytes)? reading) in
            hardware.Gpus.Zip(reader.MemoryReadings))
        {
            Assert.NotNull(gpu.StableId);
            Assert.True(reading.HasValue, "Hardware proof requires readable CUDA memory.");
            Assert.Equal(reading.Value.TotalBytes, gpu.GpuVisibleMemoryBytes);
            Assert.Equal(reading.Value.FreeBytes, gpu.FreeGpuVisibleMemoryBytes);
            Assert.Null(gpu.SharedGpuMemoryBytes);
            output.WriteLine(
                $"{gpu.Name}: CUDA total={gpu.GpuVisibleMemoryBytes} free={gpu.FreeGpuVisibleMemoryBytes}");
        }

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.NotEqual(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        output.WriteLine($"qualification={result.Status}/{result.FailureCode}");
    }

    // Record the same native snapshot the production probe consumes, since
    // querying free memory a second time can race with other GPU workloads.
    private sealed class RecordingCudaDeviceReader : ICudaDeviceReader
    {
        private readonly NvcudaDeviceReader _reader = new();
        public List<(long FreeBytes, long TotalBytes)?> MemoryReadings { get; } = [];

        public CudaDriverAvailability TryInitialize() => _reader.TryInitialize();
        public int? TryReadDeviceCount() => _reader.TryReadDeviceCount();
        public int? TryReadCudaMajorVersion() => _reader.TryReadCudaMajorVersion();
        public int? TryReadDeviceHandle(int ordinal) => _reader.TryReadDeviceHandle(ordinal);
        public string? TryReadDeviceName(int device) => _reader.TryReadDeviceName(device);
        public string? TryReadDeviceUuid(int device) => _reader.TryReadDeviceUuid(device);

        public (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device)
        {
            var memory = _reader.TryReadMemoryInfo(device);
            MemoryReadings.Add(memory);
            return memory;
        }
    }
}
