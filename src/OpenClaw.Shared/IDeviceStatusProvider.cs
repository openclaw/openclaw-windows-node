using System;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Provider interface for platform-specific device status data collection.
/// Each method returns an object that will be serialized to JSON.
/// Implementations should handle their own error cases gracefully.
/// </summary>
public interface IDeviceStatusProvider : IDisposable
{
    /// <summary>OS version, architecture, machine name, uptime.</summary>
    object GetOsInfo();

    /// <summary>CPU name, logical processor count, usage percent (may be null during warm-up).</summary>
    Task<object> GetCpuInfoAsync();

    /// <summary>Total/available memory in bytes and usage percent.</summary>
    object GetMemoryInfo();

    /// <summary>Fixed drive info: name, label, total/free bytes, usage percent, format.</summary>
    object GetDiskInfo();

    /// <summary>Battery presence, charge level, charging state, estimated time remaining.</summary>
    object GetBatteryInfo();
}
