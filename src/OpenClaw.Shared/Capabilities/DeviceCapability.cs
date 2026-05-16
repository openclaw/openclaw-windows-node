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
/// Device metadata and system health/status capability.
/// device.info - static device metadata (no provider needed).
/// device.status - rich system health data via injected IDeviceStatusProvider.
/// </summary>
public class DeviceCapability : NodeCapabilityBase
{
    public override string Category => "device";

    private static readonly string[] _commands =
    [
        "device.info",
        "device.status"
    ];

    private static readonly HashSet<string> _validSections = new(
        ["os", "cpu", "memory", "disk", "battery"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IDeviceStatusProvider? _provider;

    public override IReadOnlyList<string> Commands => _commands;

    public DeviceCapability(IOpenClawLogger logger, IDeviceStatusProvider provider)
        : base(logger)
    {
        _provider = provider;
    }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "device.info" => HandleInfo(),
            "device.status" => await HandleStatusAsync(request),
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

    private async Task<NodeInvokeResponse> HandleStatusAsync(NodeInvokeRequest request)
    {
        if (_provider == null)
            return Error("Device status provider not available");

        var sections = GetStringArrayArg(request.Args, "sections");

        // Reject unknown section names
        var invalid = sections.Where(s => !_validSections.Contains(s)).ToArray();
        if (invalid.Length > 0)
        {
            return Error($"Unknown sections: {string.Join(", ", invalid)}. "
                + $"Valid: {string.Join(", ", _validSections)}");
        }

        bool all = sections.Length == 0;
        var result = new Dictionary<string, object?>
        {
            ["collectedAt"] = DateTime.UtcNow.ToString("o")
        };

        if (all || sections.Contains("os", StringComparer.OrdinalIgnoreCase))
            result["os"] = SafeCollect("os", () => _provider.GetOsInfo());

        if (all || sections.Contains("cpu", StringComparer.OrdinalIgnoreCase))
            result["cpu"] = await SafeCollectAsync("cpu", () => _provider.GetCpuInfoAsync());

        if (all || sections.Contains("memory", StringComparer.OrdinalIgnoreCase))
            result["memory"] = SafeCollect("memory", () => _provider.GetMemoryInfo());

        if (all || sections.Contains("disk", StringComparer.OrdinalIgnoreCase))
            result["disk"] = SafeCollect("disk", () => _provider.GetDiskInfo());

        if (all || sections.Contains("battery", StringComparer.OrdinalIgnoreCase))
            result["battery"] = SafeCollect("battery", () => WrapBatteryWithLegacyFields(_provider.GetBatteryInfo()));

        // Always ensure legacy battery fields exist for backward compatibility.
        // Old contract: { level: null, state: "unknown", lowPowerModeEnabled: false }
        // Covers: battery not requested (filtered out), provider threw (SafeCollect
        // returned { error }), or battery is null.
        {
            var hasBattery = result.TryGetValue("battery", out var batteryVal) && batteryVal != null;
            var isError = hasBattery && batteryVal!.GetType().GetProperty("error") != null;

            if (!hasBattery || isError)
            {
                string? errorMsg = null;
                if (isError)
                {
                    var errProp = batteryVal!.GetType().GetProperty("error")!.GetValue(batteryVal);
                    errorMsg = errProp?.ToString();
                }

                result["battery"] = new
                {
                    level = (double?)null,
                    state = "unknown",
                    lowPowerModeEnabled = false,
                    error = errorMsg
                };
            }
        }

        // Legacy fields preserved for backward compatibility with existing consumers.
        result["thermal"] = new { state = "nominal" };
        result["storage"] = SafeCollect("storage", () => GetStorageStatus());
        result["network"] = SafeCollect("network", () => GetNetworkStatus());
        result["uptimeSeconds"] = Environment.TickCount64 / 1000.0;

        return Success(result);
    }

    /// <summary>Per-section fault tolerance: one section failing doesn't kill the whole response.</summary>
    private object? SafeCollect(string section, Func<object> collector)
    {
        try { return collector(); }
        catch (Exception ex)
        {
            Logger.Warn($"device.status: {section} collection failed: {ex.Message}");
            return new { error = "collection failed" };
        }
    }

    private async Task<object?> SafeCollectAsync(string section, Func<Task<object>> collector)
    {
        try { return await collector(); }
        catch (Exception ex)
        {
            Logger.Warn($"device.status: {section} collection failed: {ex.Message}");
            return new { error = "collection failed" };
        }
    }

    /// <summary>
    /// Wraps the provider's battery result with legacy fields (level, state, lowPowerModeEnabled)
    /// so old consumers that read battery.level / battery.state continue to work.
    /// </summary>
    private static object WrapBatteryWithLegacyFields(object providerResult)
    {
        // Serialize the provider result to a dictionary so we can merge legacy fields.
        var json = System.Text.Json.JsonSerializer.Serialize(providerResult);
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)
            ?? new Dictionary<string, System.Text.Json.JsonElement>();

        // Map new fields to legacy equivalents.
        double? level = null;
        if (dict.TryGetValue("chargePercent", out var cp) && cp.ValueKind == System.Text.Json.JsonValueKind.Number)
            level = cp.GetDouble();

        var isCharging = dict.TryGetValue("isCharging", out var ic)
            && ic.ValueKind == System.Text.Json.JsonValueKind.True;

        var state = isCharging ? "charging" : (level.HasValue ? "discharging" : "unknown");

        var result = new Dictionary<string, object?>
        {
            // Legacy fields
            ["level"] = level,
            ["state"] = state,
            ["lowPowerModeEnabled"] = false,
        };

        // Merge all new fields from provider
        foreach (var kv in dict)
            result[kv.Key] = kv.Value;

        return result;
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

    #region Legacy helpers (backward compat)

    private static object GetStorageStatus()
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

        return new { totalBytes = 0L, freeBytes = 0L, usedBytes = 0L };
    }

    private static object GetNetworkStatus()
    {
        string[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Select(nic => nic.NetworkInterfaceType switch
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
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch { interfaces = []; }

        bool isAvailable;
        try { isAvailable = NetworkInterface.GetIsNetworkAvailable(); }
        catch { isAvailable = false; }

        return new
        {
            status = isAvailable ? "satisfied" : "unsatisfied",
            isExpensive = false,
            isConstrained = false,
            interfaces
        };
    }

    #endregion
}
