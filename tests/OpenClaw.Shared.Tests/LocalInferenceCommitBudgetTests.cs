using OpenClaw.Shared.Inference;

namespace OpenClaw.Shared.Tests;

public class LocalInferenceCommitBudgetTests
{
    [Fact]
    public void Probe_PreservesCommitBudgetSeparatelyFromCudaAndPhysicalMemory()
    {
        var memory = new PhysicalMemorySnapshot(64, 32) { AvailableCommitBytes = 12 };
        HostHardwareInfo hardware = new CudaHostHardwareProbe(new Reader(), () => memory).Probe();

        Assert.Equal(12, hardware.AvailableCommitMemoryBytes);
        Assert.Equal(32, hardware.AvailablePhysicalMemoryBytes);
        Assert.Equal(48, Assert.Single(hardware.Gpus).FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void Probe_FailedMemoryReadKeepsCommitBudgetUnknown()
    {
        HostHardwareInfo hardware = new CudaHostHardwareProbe(new Reader(), () => null).Probe();

        Assert.Null(hardware.AvailableCommitMemoryBytes);
        Assert.Equal(48, Assert.Single(hardware.Gpus).FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void PhysicalMemoryProbe_ReadsCurrentWindowsCommitBudget()
    {
        if (!OperatingSystem.IsWindows())
            return;

        PhysicalMemorySnapshot memory = PhysicalMemoryProbe.Read();

        Assert.NotNull(memory.AvailableCommitBytes);
        Assert.True(memory.AvailableCommitBytes > 0);
    }

    private sealed class Reader : ICudaDeviceReader
    {
        public CudaDriverAvailability TryInitialize() => CudaDriverAvailability.Ready;
        public int? TryReadDeviceCount() => 1;
        public int? TryReadCudaMajorVersion() => 13;
        public int? TryReadDeviceHandle(int ordinal) => 0;
        public string? TryReadDeviceName(int device) => "NVIDIA RTX Spark N1X";
        public string? TryReadDeviceUuid(int device) => "GPU-commit-budget";
        public (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device) => (48, 64);
    }
}
