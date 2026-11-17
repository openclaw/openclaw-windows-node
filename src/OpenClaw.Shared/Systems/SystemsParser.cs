// Validates reported Systems payloads. Usage: SystemsParser.ParseEnvironments(response).
using System.Text.Json;

namespace OpenClaw.Shared.Systems;

/// <summary>
/// Tolerant readers for the Systems Gateway payloads. Malformed entries are
/// skipped so one bad row cannot hide the rest of the inventory; a missing
/// top-level collection is a protocol error and throws.
/// </summary>
public static class SystemsParser
{
    public static IReadOnlyList<SystemEnvironment> ParseEnvironments(JsonElement payload)
    {
        var items = RequiredArray(payload, "environments");
        var result = new List<SystemEnvironment>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (ParseEnvironment(item) is { } environment)
                result.Add(environment);
        }
        return result;
    }

    public static SystemEnvironment? ParseEnvironment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            String(item, "id") is not { } id ||
            String(item, "type") is not { } type)
        {
            return null;
        }

        return new SystemEnvironment
        {
            Id = id,
            Type = type,
            Label = String(item, "label"),
            Status = ParseStatus(String(item, "status")),
            Platform = String(item, "platform"),
            LastConnectedAt = Time(item, "lastConnectedAtMs"),
            LastDisconnectedAt = Time(item, "lastDisconnectedAtMs"),
            LastSeenAt = Time(item, "lastSeenAtMs"),
            LastSeenReason = String(item, "lastSeenReason"),
            Capabilities = Strings(item, "capabilities"),
            InvocableCommands = Strings(item, "invocableCommands"),
            Desktop = Bool(item, "desktop"),
            DesktopAvailability = Object(item, "desktopAvailability") is { } desktop
                ? String(desktop, "state") switch
                {
                    "locked" => DesktopLockState.Locked,
                    "unlocked" => DesktopLockState.Unlocked,
                    _ => DesktopLockState.Unknown,
                }
                : null,
            Issues = Array(item, "issues")?.EnumerateArray()
                .Where(issue => issue.ValueKind == JsonValueKind.Object && String(issue, "code") is not null)
                .Select(issue => new RuntimeTargetIssue(String(issue, "code")!, String(issue, "message")))
                .ToArray() ?? System.Array.Empty<RuntimeTargetIssue>(),
            Worker = Object(item, "worker") is { } worker && String(worker, "providerId") is { } provider
                ? new WorkerMetadata(
                    provider,
                    String(worker, "profileId"),
                    String(worker, "state") ?? "unknown",
                    String(worker, "tunnelStatus"),
                    String(worker, "error"),
                    Bool(worker, "desktop"))
                : null,
        };
    }

    public static SystemStatus ParseStatus(string? status) => status switch
    {
        "available" => SystemStatus.Online,
        "unavailable" => SystemStatus.Offline,
        "starting" => SystemStatus.Starting,
        "stopping" => SystemStatus.Stopping,
        "error" => SystemStatus.Error,
        _ => SystemStatus.Unknown,
    };

    public static GatewaySystemInfo? ParseSystemInfo(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || ParseMeasurements(payload) is not { } measurements)
            return null;
        return new GatewaySystemInfo
        {
            MachineName = String(payload, "machineName") ?? "",
            Hostname = String(payload, "hostname") ?? "",
            Platform = String(payload, "platform") ?? "",
            Release = String(payload, "release") ?? "",
            Arch = String(payload, "arch") ?? "",
            OsLabel = String(payload, "osLabel") ?? "",
            LanAddress = String(payload, "lanAddress"),
            Port = Int(payload, "port"),
            NodeVersion = String(payload, "nodeVersion") ?? "",
            UptimeMs = Long(payload, "uptimeMs") ?? 0,
            CpuModel = String(payload, "cpuModel"),
            Measurements = measurements,
        };
    }

    /// <summary>Reads a <c>hostStats</c> object from <c>node.list</c> or a <c>node.hostStats</c> event.</summary>
    public static NodeHostStats? ParseNodeHostStats(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            Time(value, "updatedAtMs") is not { } updatedAt ||
            ParseMeasurements(value) is not { } measurements)
        {
            return null;
        }
        return new NodeHostStats(measurements, updatedAt);
    }

    public static HostMeasurements? ParseMeasurements(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            Int(value, "cpuCount") is not { } cpuCount ||
            Long(value, "memoryTotalBytes") is not { } memoryTotal ||
            Long(value, "memoryFreeBytes") is not { } memoryFree ||
            cpuCount <= 0 || memoryTotal <= 0 || memoryFree < 0 || memoryFree > memoryTotal)
        {
            return null;
        }

        // loadAverage is [1, 5, 15] minutes; only the one-minute value is shown.
        double? load1 = null;
        if (Array(value, "loadAverage") is { } load && load.GetArrayLength() == 3 &&
            load[0] is { ValueKind: JsonValueKind.Number } first && first.TryGetDouble(out var one) && double.IsFinite(one) && one >= 0)
        {
            load1 = one;
        }

        IReadOnlyList<DiskStats>? disks = null;
        if (Array(value, "disks") is { } diskArray)
        {
            disks = diskArray.EnumerateArray()
                .Where(disk => disk.ValueKind == JsonValueKind.Object)
                .Select(disk => String(disk, "path") is { } path && Long(disk, "totalBytes") is { } total && Long(disk, "availableBytes") is { } available &&
                    total > 0 && available >= 0 && available <= total
                    ? new DiskStats(path, total, available)
                    : null)
                .OfType<DiskStats>()
                .ToArray();
        }

        var diskTotal = Long(value, "diskTotalBytes");
        var diskAvailable = Long(value, "diskAvailableBytes");
        var validDisk = diskTotal > 0 && diskAvailable >= 0 && diskAvailable <= diskTotal;
        return new HostMeasurements
        {
            CpuCount = cpuCount,
            Load1 = load1,
            MemoryTotalBytes = memoryTotal,
            MemoryFreeBytes = memoryFree,
            DiskTotalBytes = validDisk ? diskTotal : null,
            DiskAvailableBytes = validDisk ? diskAvailable : null,
            Disks = disks,
        };
    }

    /// <summary>Reads the <c>placement</c> projection of one <c>sessions.list</c> row.</summary>
    public static SessionPlacementInfo? ParseSessionPlacement(JsonElement row)
    {
        if (Object(row, "placement") is not { } placement)
            return null;
        var runner = Object(placement, "runner");
        return new SessionPlacementInfo(
            String(placement, "state") switch
            {
                "local" => SessionPlacementState.Local,
                "requested" => SessionPlacementState.Requested,
                "provisioning" => SessionPlacementState.Provisioning,
                "syncing" => SessionPlacementState.Syncing,
                "starting" => SessionPlacementState.Starting,
                "active" => SessionPlacementState.Active,
                "draining" => SessionPlacementState.Draining,
                "reconciling" => SessionPlacementState.Reconciling,
                "reclaimed" => SessionPlacementState.Reclaimed,
                "failed" => SessionPlacementState.Failed,
                _ => SessionPlacementState.Unknown,
            },
            String(placement, "environmentId"),
            runner is { } r ? String(r, "deviceId") : null);
    }

    private static JsonElement RequiredArray(JsonElement value, string name) =>
        Array(value, name) ?? throw new InvalidOperationException($"The Gateway returned an invalid {name} list.");

    internal static JsonElement? Array(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) &&
        array.ValueKind == JsonValueKind.Array ? array : null;

    internal static JsonElement? Object(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var obj) &&
        obj.ValueKind == JsonValueKind.Object ? obj : null;

    internal static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static bool? Bool(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static long? Long(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        if (property.TryGetInt64(out var result))
            return result >= 0 ? result : null;
        return property.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && d <= long.MaxValue ? (long)d : null;
    }

    private static int? Int(JsonElement value, string name) =>
        Long(value, name) is { } l && l <= int.MaxValue ? (int)l : null;

    private static DateTimeOffset? Time(JsonElement value, string name) =>
        Long(value, name) is { } ms && ms <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static IReadOnlyList<string> Strings(JsonElement value, string name) =>
        Array(value, name)?.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
            .Select(entry => entry.GetString()!)
            .ToArray() ?? System.Array.Empty<string>();
}
