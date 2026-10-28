using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Systems;

/// <summary>
/// Samples this machine for the Gateway's <c>node.host.stats</c> node event.
/// The Gateway schema is closed, so only its fields are sent. Windows has no
/// load average, so that optional field is omitted.
/// </summary>
public static class HostStatsSampler
{
    public const string EventName = "node.host.stats";
    /// <summary>Matches the Gateway's node publishing cadence.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_NODE_HOST_STATS_INTERVAL_SECONDS"), out var seconds) && seconds >= 10
            ? seconds
            : 60);

    public static JsonObject? Sample()
    {
        if (!OperatingSystem.IsWindows() || !TryReadMemory(out var total, out var free))
            return null;
        var payload = new JsonObject
        {
            ["cpuCount"] = Math.Clamp(Environment.ProcessorCount, 1, 4096),
            ["memoryTotalBytes"] = total,
            ["memoryFreeBytes"] = Math.Min(free, total),
        };
        if (TryReadSystemDisk(out var diskTotal, out var diskFree))
        {
            payload["diskTotalBytes"] = diskTotal;
            payload["diskAvailableBytes"] = Math.Min(diskFree, diskTotal);
        }
        return payload;
    }

    private static bool TryReadSystemDisk(out long total, out long available)
    {
        total = available = 0;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root))
                return false;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return false;
            total = drive.TotalSize;
            available = drive.AvailableFreeSpace;
            return total > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadMemory(out long total, out long free)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            total = free = 0;
            return false;
        }
        total = (long)Math.Min(status.TotalPhys, long.MaxValue);
        free = (long)Math.Min(status.AvailPhys, long.MaxValue);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
