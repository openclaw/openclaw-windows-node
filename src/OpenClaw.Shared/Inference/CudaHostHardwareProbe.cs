using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// The CUDA device facts the host probe needs, as a seam so failure handling can
/// be covered without NVIDIA hardware.
/// </summary>
internal interface ICudaDeviceReader
{
    CudaDriverAvailability TryInitialize();
    int? TryReadDeviceCount();
    int? TryReadCudaMajorVersion();
    int? TryReadDeviceHandle(int ordinal);
    string? TryReadDeviceName(int device);
    string? TryReadDeviceUuid(int device);
    (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device);
}

/// <summary>Reads device facts from the real NVIDIA driver.</summary>
internal sealed class NvcudaDeviceReader : ICudaDeviceReader
{
    public CudaDriverAvailability TryInitialize() => NvcudaDriver.TryInitialize();

    public int? TryReadDeviceCount() => NvcudaDriver.TryReadDeviceCount();

    public int? TryReadCudaMajorVersion() => NvcudaDriver.TryReadCudaMajorVersion();

    public int? TryReadDeviceHandle(int ordinal) => NvcudaDriver.TryReadDeviceHandle(ordinal);

    public string? TryReadDeviceName(int device) => NvcudaDriver.TryReadDeviceName(device);

    public string? TryReadDeviceUuid(int device) => NvcudaDriver.TryReadDeviceUuid(device);

    public (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device) =>
        NvcudaDriver.WithContext(device, NvcudaDriver.TryReadMemoryInfo, null);
}

public interface IHostHardwareProbe
{
    HostHardwareInfo Probe();
}

/// <summary>
/// Reads the CUDA driver's device identity and allocator-visible total/free
/// memory on this adapter. This is the sole GPU-memory source for
/// Local AI qualification, including UMA devices.
/// </summary>
/// <remarks>
/// <para>
/// Use <c>cuMemGetInfo</c> without DXGI or NVML dedicated-memory caps. Those
/// figures can describe only the carveout on unified-memory devices such as
/// the 48 GB RTX Spark with a 16 GB carveout, incorrectly excluding supported
/// devices. No separate host/shared-memory figure is added to CUDA's totals.
/// </para>
/// <para>
/// Only an absent driver or a driver reporting no device proves that this
/// machine has no NVIDIA GPU. Other read failures keep the device in the list
/// with the missing facts left null, so qualification reports the retryable
/// <c>HardwareFactsIncomplete</c> state instead of the definitive
/// <c>NoNvidiaGpu</c> state that permanently hides Local AI.
/// </para>
/// </remarks>
public sealed class CudaHostHardwareProbe : IHostHardwareProbe
{
    internal const string UnidentifiedGpuName = "NVIDIA GPU";

    private readonly ICudaDeviceReader _reader;

    public CudaHostHardwareProbe()
        : this(new NvcudaDeviceReader())
    {
    }

    internal CudaHostHardwareProbe(ICudaDeviceReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public HostHardwareInfo Probe()
    {
        PhysicalMemorySnapshot? physicalMemory = null;
        try { physicalMemory = PhysicalMemoryProbe.TryRead(); } catch { }

        IReadOnlyList<GpuInfo> gpus;
        try { gpus = CaptureCudaGpus(); } catch { gpus = []; }

        return new HostHardwareInfo(
            RuntimeInformation.OSArchitecture,
            physicalMemory?.TotalBytes,
            physicalMemory?.AvailableBytes,
            gpus,
            VulkanAvailable: false);
    }

    private IReadOnlyList<GpuInfo> CaptureCudaGpus()
    {
        // Only an absent driver or a driver that reports no device is proof that
        // this machine has no NVIDIA GPU. A driver that fails to initialize (for
        // example on a driver/runtime mismatch) leaves presence unknown, so it
        // must stay retryable instead of permanently hiding Local AI.
        switch (Read(() => (CudaDriverAvailability?)_reader.TryInitialize()))
        {
            case CudaDriverAvailability.Absent:
            case CudaDriverAvailability.NoDevice:
                return [];
            case CudaDriverAvailability.Ready:
                break;
            default:
                return [Unidentified()];
        }

        // Every failure from here on is a partial read, not an absent GPU.
        if (Read(() => _reader.TryReadDeviceCount()) is not { } devices)
            return [Unidentified()];

        if (devices <= 0)
            return [];

        int? cudaMajorVersion = Read(() => _reader.TryReadCudaMajorVersion());

        return Enumerable.Range(0, devices)
            .Select(ordinal => CaptureGpu(ordinal, cudaMajorVersion))
            .ToList();
    }

    private GpuInfo CaptureGpu(int ordinal, int? cudaMajorVersion)
    {
        // Contained per device so one failing device or entry point cannot
        // discard the devices that read cleanly.
        try
        {
            if (_reader.TryReadDeviceHandle(ordinal) is not { } device)
                return Unidentified(cudaMajorVersion);

            // Every fact is read independently so one failure cannot discard the
            // others, and a missing UUID keeps the device visible as incomplete
            // rather than absent.
            var identifiedGpu = new GpuInfo(
                GpuVendor.Nvidia,
                Read(() => _reader.TryReadDeviceName(device)) ?? UnidentifiedGpuName,
                CudaMajorVersion: cudaMajorVersion,
                StableId: Read(() => _reader.TryReadDeviceUuid(device)));

            if (Read(() => _reader.TryReadMemoryInfo(device)) is not { } memory)
            {
                return identifiedGpu;
            }

            return identifiedGpu with
            {
                GpuVisibleMemoryBytes = memory.TotalBytes,
                FreeGpuVisibleMemoryBytes = memory.FreeBytes,
            };
        }
        catch
        {
            return Unidentified(cudaMajorVersion);
        }
    }

    private static T? Read<T>(Func<T?> read)
        where T : struct
    {
        try { return read(); } catch { return null; }
    }

    private static string? Read(Func<string?> read)
    {
        try { return read(); } catch { return null; }
    }

    private static GpuInfo Unidentified(int? cudaMajorVersion = null) =>
        new(GpuVendor.Nvidia, UnidentifiedGpuName, CudaMajorVersion: cudaMajorVersion);
}
