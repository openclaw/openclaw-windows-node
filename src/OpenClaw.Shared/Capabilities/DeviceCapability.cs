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

    public DeviceCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return Task.FromResult(request.Command switch
        {
            "device.info" => HandleInfo(),
            "device.status" => HandleStatus(),
            _ => Error($"Unknown command: {request.Command}")
        });
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

    private NodeInvokeResponse HandleStatus()
    {
        Logger.Info("device.status");

        var storage = GetStorageStatus(Logger);
        var network = GetNetworkStatus(Logger);

        return Success(new
        {
            battery = new
            {
                level = (double?)null,
                state = "unknown",
                lowPowerModeEnabled = false
            },
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
