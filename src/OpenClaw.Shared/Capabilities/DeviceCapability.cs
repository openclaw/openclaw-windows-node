using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Device metadata and lightweight health/status capability.
/// </summary>
public class DeviceCapability : NodeCapabilityBase
{
    public override string Category => "device";

    private static readonly string[] _commands =
    [
        "device.info",
        "device.status"
    ];

    public override IReadOnlyList<string> Commands => _commands;

    /// <summary>
    /// Optional platform-specific battery provider.
    /// When set, <c>device.status</c> calls this to populate the <c>battery</c> section.
    /// When null (default), battery is reported as <c>state=unknown, level=null</c>.
    /// Follows the same event-delegation pattern used by <see cref="CameraCapability"/>
    /// and <see cref="ScreenCapability"/> to keep WinRT out of the Shared project.
    /// </summary>
    public event Func<Task<DeviceBatteryStatus?>>? BatteryStatusRequested;

    public DeviceCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "device.info" => HandleInfo(),
            "device.status" => await HandleStatusAsync(),
            _ => Error($"Unknown command: {request.Command}")
        };
    }

    private NodeInvokeResponse HandleInfo()
    {
        Logger.Info("device.info");

        var assembly = typeof(DeviceCapability).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        return Success(new
        {
            deviceName = Environment.MachineName,
            modelIdentifier = GetModelIdentifier(),
            systemName = OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
            systemVersion = RuntimeInformation.OSDescription,
            appVersion = version,
            appBuild = assembly.GetName().Version?.ToString() ?? version,
            locale = CultureInfo.CurrentCulture.Name
        });
    }

    private async Task<NodeInvokeResponse> HandleStatusAsync()
    {
        Logger.Info("device.status");

        DeviceBatteryStatus? batteryData = null;
        if (BatteryStatusRequested != null)
        {
            try
            {
                batteryData = await BatteryStatusRequested();
            }
            catch (Exception ex)
            {
                Logger.Warn($"device.status: battery provider threw: {ex.Message}");
            }
        }

        object battery;
        if (batteryData != null)
        {
            battery = new
            {
                level = batteryData.ChargePercent.HasValue ? batteryData.ChargePercent.Value / 100.0 : (double?)null,
                state = batteryData.IsCharging ? "charging" : (batteryData.Present ? "unplugged" : "unknown"),
                lowPowerModeEnabled = false,
                present = batteryData.Present
            };
        }
        else
        {
            battery = new
            {
                level = (double?)null,
                state = "unknown",
                lowPowerModeEnabled = false,
                present = false
            };
        }

        var storage = GetStorageStatus(Logger);
        var network = GetNetworkStatus(Logger);

        return Success(new
        {
            battery,
            thermal = new
            {
                state = "nominal"
            },
            storage,
            network,
            uptimeSeconds = Environment.TickCount64 / 1000.0
        });
    }

    private static string GetModelIdentifier()
    {
        var processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(processorIdentifier))
        {
            return processorIdentifier;
        }

        return $"{RuntimeInformation.OSArchitecture}".ToLowerInvariant();
    }

    private static object GetStorageStatus(IOpenClawLogger logger)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                ?? Path.GetPathRoot(AppContext.BaseDirectory)
                ?? string.Empty;
            var drive = !string.IsNullOrWhiteSpace(root)
                ? new DriveInfo(root)
                : DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);

            if (drive is { IsReady: true })
            {
                var totalBytes = drive.TotalSize;
                var freeBytes = drive.AvailableFreeSpace;
                return new
                {
                    totalBytes,
                    freeBytes,
                    usedBytes = Math.Max(0, totalBytes - freeBytes)
                };
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: storage status unavailable: {ex.Message}");
        }

        return new
        {
            totalBytes = 0L,
            freeBytes = 0L,
            usedBytes = 0L
        };
    }

    private static object GetNetworkStatus(IOpenClawLogger logger)
    {
        var interfaces = Array.Empty<string>();
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Select(MapInterfaceType)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: network interfaces unavailable: {ex.Message}");
        }

        var isAvailable = false;
        try
        {
            isAvailable = NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: network availability unavailable: {ex.Message}");
        }

        return new
        {
            status = isAvailable ? "satisfied" : "unsatisfied",
            isExpensive = false,
            isConstrained = false,
            interfaces
        };
    }

    private static string MapInterfaceType(NetworkInterface nic)
    {
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "wifi",
            NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.FastEthernetT => "wired",
            NetworkInterfaceType.Ppp
                or NetworkInterfaceType.Wwanpp
                or NetworkInterfaceType.Wwanpp2 => "cellular",
            _ => "other"
        };
    }
}

/// <summary>
/// Battery status returned by the platform-specific <see cref="DeviceCapability.BatteryStatusRequested"/> provider.
/// </summary>
public sealed class DeviceBatteryStatus
{
    /// <summary>Whether a battery is physically present.</summary>
    public bool Present { get; init; }

    /// <summary>Battery charge level 0–100, or null if unavailable.</summary>
    public int? ChargePercent { get; init; }

    /// <summary>Whether the device is currently charging.</summary>
    public bool IsCharging { get; init; }

    /// <summary>Estimated minutes of battery life remaining, or null if unknown or charging.</summary>
    public int? EstimatedMinutesRemaining { get; init; }
}

