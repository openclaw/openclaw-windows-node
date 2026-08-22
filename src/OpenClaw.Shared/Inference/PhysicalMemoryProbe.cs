using System;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>Installed and currently-free physical memory, in bytes.</summary>
public readonly record struct PhysicalMemorySnapshot(long TotalBytes, long AvailableBytes)
{
    /// <summary>Percentage of physical memory currently in use, rounded to one decimal.</summary>
    public double UsagePercent => TotalBytes > 0
        ? Math.Round((1.0 - (double)AvailableBytes / TotalBytes) * 100, 1)
        : 0.0;
}

/// <summary>
/// Single owner of the <c>GlobalMemoryStatusEx</c> P/Invoke. Both the device
/// status capability and the local-inference hardware probe need installed RAM,
/// and duplicating the interop struct in two assemblies is how the two copies
/// drift apart.
/// </summary>
public static class PhysicalMemoryProbe
{
    /// <summary>
    /// Read installed and available physical memory.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Win32 query failed.</exception>
    public static PhysicalMemorySnapshot Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException("GlobalMemoryStatusEx failed");

        return new PhysicalMemorySnapshot((long)status.ullTotalPhys, (long)status.ullAvailPhys);
    }

    /// <summary>
    /// Non-throwing variant for callers (like the hardware probe) that must
    /// degrade to "unknown" rather than fail.
    /// </summary>
    public static PhysicalMemorySnapshot? TryRead()
    {
        // slopwatch-ignore: SW003 Probe is best-effort by contract; callers treat null as "unknown memory".
        try { return Read(); } catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
