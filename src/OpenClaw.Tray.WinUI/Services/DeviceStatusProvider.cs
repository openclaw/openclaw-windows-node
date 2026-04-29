using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Microsoft.Win32;

namespace OpenClawTray.Services;

/// <summary>
/// Windows-specific implementation of IDeviceStatusProvider.
/// Uses WinRT for battery, P/Invoke for memory, registry for CPU name,
/// and a cached background sampler for CPU usage.
/// </summary>
public class DeviceStatusProvider : IDeviceStatusProvider
{
    private readonly IOpenClawLogger _logger;
    private double? _lastCpuUsage;
    private Timer? _cpuSampler;
    private PerformanceCounter? _cpuCounter;
    private bool _disposed;

    public DeviceStatusProvider(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts the background CPU usage sampler. Call once after construction.
    /// The first valid reading appears after ~2 seconds.
    /// </summary>
    public void StartCpuSampling()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // Prime the counter (first read is always 0)
            _cpuSampler = new Timer(_ =>
            {
                if (_disposed) return;
                try
                {
                    var counter = _cpuCounter;
                    if (counter != null)
                        _lastCpuUsage = Math.Round(counter.NextValue(), 1);
                }
                catch { /* counter unavailable or disposed */ }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.Warn($"CPU sampling unavailable: {ex.Message}");
        }
    }

    public object GetOsInfo()
    {
        return new
        {
            version = Environment.OSVersion.Version.ToString(),
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            machineName = Environment.MachineName,
            uptimeSeconds = Environment.TickCount64 / 1000
        };
    }

    public Task<object> GetCpuInfoAsync()
    {
        string? cpuName = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpuName = key?.GetValue("ProcessorNameString") as string;
        }
        catch
        {
            // Registry access may be restricted
        }

        object result = new
        {
            name = cpuName?.Trim(),
            logicalProcessors = Environment.ProcessorCount,
            usagePercent = _lastCpuUsage
        };

        return Task.FromResult(result);
    }

    public object GetMemoryInfo()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException("GlobalMemoryStatusEx failed");

        var totalBytes = (long)status.ullTotalPhys;
        var availableBytes = (long)status.ullAvailPhys;
        var usagePercent = totalBytes > 0
            ? Math.Round((1.0 - (double)availableBytes / totalBytes) * 100, 1)
            : 0.0;

        return new
        {
            totalBytes,
            availableBytes,
            usagePercent
        };
    }

    public object GetDiskInfo()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d =>
            {
                try
                {
                    var totalBytes = d.TotalSize;
                    var freeBytes = d.AvailableFreeSpace;
                    return (object?)new
                    {
                        name = d.Name,
                        label = d.VolumeLabel,
                        totalBytes,
                        freeBytes,
                        usagePercent = totalBytes > 0
                            ? Math.Round((1.0 - (double)freeBytes / totalBytes) * 100, 1)
                            : 0.0,
                        format = d.DriveFormat
                    };
                }
                catch { return null; }
            })
            .Where(d => d != null)
            .ToArray();

        return new { drives };
    }

    public object GetBatteryInfo()
    {
        try
        {
            var battery = global::Windows.Devices.Power.Battery.AggregateBattery;
            var report = battery.GetReport();

            if (report.Status == global::Windows.System.Power.BatteryStatus.NotPresent)
            {
                return new
                {
                    present = false,
                    chargePercent = (int?)null,
                    isCharging = false,
                    estimatedMinutesRemaining = (int?)null
                };
            }

            int? chargePercent = null;
            if (report.FullChargeCapacityInMilliwattHours.HasValue &&
                report.RemainingCapacityInMilliwattHours.HasValue &&
                report.FullChargeCapacityInMilliwattHours.Value > 0)
            {
                chargePercent = (int)Math.Round(
                    (double)report.RemainingCapacityInMilliwattHours.Value /
                    report.FullChargeCapacityInMilliwattHours.Value * 100);
            }

            bool isCharging = report.Status == global::Windows.System.Power.BatteryStatus.Charging;

            // Estimate minutes remaining when discharging
            int? estimatedMinutesRemaining = null;
            if (report.Status == global::Windows.System.Power.BatteryStatus.Discharging &&
                report.ChargeRateInMilliwatts.HasValue &&
                report.ChargeRateInMilliwatts.Value < 0 &&
                report.RemainingCapacityInMilliwattHours.HasValue)
            {
                var rateWatts = Math.Abs(report.ChargeRateInMilliwatts.Value);
                if (rateWatts > 0)
                {
                    estimatedMinutesRemaining = (int)(
                        (double)report.RemainingCapacityInMilliwattHours.Value / rateWatts * 60);
                }
            }

            return new
            {
                present = true,
                chargePercent,
                isCharging,
                estimatedMinutesRemaining
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"Battery info unavailable: {ex.Message}");
            return new
            {
                present = false,
                chargePercent = (int?)null,
                isCharging = false,
                estimatedMinutesRemaining = (int?)null
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain the timer: wait for any in-flight callback to complete
        // before disposing the counter it reads.
        using var timerDrained = new ManualResetEvent(false);
        if (_cpuSampler != null)
        {
            _cpuSampler.Dispose(timerDrained);
            timerDrained.WaitOne(TimeSpan.FromSeconds(3));
        }
        _cpuSampler = null;

        _cpuCounter?.Dispose();
        _cpuCounter = null;
    }

    #region P/Invoke

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

    #endregion
}
