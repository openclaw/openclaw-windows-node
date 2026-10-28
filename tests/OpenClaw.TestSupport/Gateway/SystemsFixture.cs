using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Synthetic Systems inventory for the fixture Gateway: a Gateway host, paired nodes,
/// workers, connected apps, pending pairings, host statistics and backups. Timestamps are
/// relative to the current time so the native page shows live and last-known states, and
/// statistics move between reads so sparklines collect history. Nothing is real or mutable.
/// </summary>
public sealed class SystemsFixture
{
    public const string InventoryName = "systems-inventory";
    public const string EmptyName = "systems-empty";
    public const string StudioNodeId = "fixture-studio";
    public const string MacNodeId = "fixture-macbook";
    public const string PiNodeId = "fixture-pi";
    public const string WorkerId = "worker:fixture-cloud:wk7f3a91";
    public const string StartingWorkerId = "worker:fixture-cloud:wkb00t42";
    public const string DestroyedWorkerId = "worker:fixture-cloud:wkdead00";

    private const long Gb = 1024L * 1024 * 1024;
    private readonly bool _empty;
    private int _reads;

    public SystemsFixture(bool empty) => _empty = empty;

    public string Name => _empty ? EmptyName : InventoryName;

    public static readonly string[] Methods =
    [
        "environments.list", "system.info", "node.list", "node.pair.list", "device.pair.list",
        "backup.status", "storage.locations.probe",
    ];

    public static readonly string[] Events =
    [
        "presence", "node.pair.requested", "node.pair.resolved", "node.hostStats", "node.runnerInventory.changed",
        "config.changed",
    ];

    public bool Handles(string method) => Methods.Contains(method, StringComparer.Ordinal);

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long Ago(TimeSpan span) => Now - (long)span.TotalMilliseconds;

    // Offline machines keep their last report: anchor at fixture start so reads repeat it.
    private static readonly long StartedAt = Now;

    private static long Before(TimeSpan span) => StartedAt - (long)span.TotalMilliseconds;

    public object Respond(string method, JsonElement parameters)
    {
        var sample = Interlocked.Increment(ref _reads);
        return method switch
        {
            "environments.list" => new { environments = _empty ? Array.Empty<object>() : Environments() },
            "system.info" => SystemInfo(sample),
            "node.list" => new { ts = Now, nodes = _empty ? Array.Empty<object>() : Nodes(sample) },
            "node.pair.list" => new { pending = _empty ? Array.Empty<object>() : NodePending(), paired = Array.Empty<object>() },
            "device.pair.list" => new { pending = _empty ? Array.Empty<object>() : DevicePending(), paired = Array.Empty<object>() },
            "backup.status" => _empty
                ? new { targets = Array.Empty<object>(), schedules = Array.Empty<object>(), locations = Array.Empty<object>() }
                : Backups(),
            "storage.locations.probe" => Probe(parameters),
            _ => throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown fixture Systems method."),
        };
    }

    /// <summary>Presence beacons for the hello snapshot.</summary>
    public object[] Presence() => _empty
        ? []
        :
        [
            new { host = "fixture-gw", mode = "gateway", platform = "linux", version = "fixture-1", ts = Ago(TimeSpan.FromSeconds(5)), reason = "self" },
            new
            {
                host = "Fixture Studio PC", deviceId = StudioNodeId, mode = "node", platform = "windows 11", deviceFamily = "Windows",
                version = "0.9.0", ip = "192.168.1.31", roles = new[] { "node" }, ts = Ago(TimeSpan.FromSeconds(20)),
                reason = "periodic", lastInputSeconds = 42,
            },
            new
            {
                host = "Fixture Control UI", instanceId = "fixture-browser-1", mode = "webchat", platform = "windows",
                version = "fixture-1", roles = new[] { "operator" }, ts = Ago(TimeSpan.FromSeconds(50)), reason = "connect",
            },
            new
            {
                host = "Fixture iPhone", instanceId = "fixture-iphone-1", mode = "ios", platform = "ios 18.2", deviceFamily = "iPhone",
                modelIdentifier = "iPhone16,1", version = "1.4.0", roles = new[] { "operator" }, ts = Ago(TimeSpan.FromMinutes(14)),
                reason = "disconnect",
            },
        ];

    /// <summary>Adds placement and exec bindings to browse sessions so machines show related sessions.</summary>
    public static JsonElement DecorateSession(string key, JsonElement row)
    {
        var node = JsonNode.Parse(row.GetRawText())!.AsObject();
        switch (key)
        {
            case GatewayScenario.LongSessionKey:
                node["placement"] = new JsonObject
                {
                    ["state"] = "active",
                    ["environmentId"] = "node:" + StudioNodeId,
                    ["runner"] = new JsonObject { ["kind"] = "device", ["status"] = "available", ["deviceId"] = StudioNodeId },
                };
                break;
            case GatewayScenario.OtherSessionKey:
                node["placement"] = new JsonObject { ["state"] = "active", ["environmentId"] = WorkerId };
                break;
            case GatewayScenario.EmptySessionKey:
                node["placement"] = new JsonObject { ["state"] = "reclaimed", ["environmentId"] = "node:" + StudioNodeId };
                break;
            case GatewayScenario.EdgeSessionKey:
                node["execNode"] = MacNodeId;
                break;
        }
        return JsonSerializer.SerializeToElement(node);
    }

    private static object[] Environments() =>
    [
        new
        {
            id = "gateway", type = "local", status = "available", platform = "linux",
            capabilities = new[] { "browser", "exec", "files" },
            desktopSetup = new { state = "ready" },
        },
        new
        {
            id = "node:" + StudioNodeId, type = "node", label = "Fixture Studio PC", status = "available", platform = "windows",
            sessionHost = true, workerSlots = new { total = 4, available = 3 },
            lastConnectedAtMs = Ago(TimeSpan.FromHours(3)), lastSeenAtMs = Ago(TimeSpan.FromSeconds(20)), lastSeenReason = "periodic",
            capabilities = new[] { "system", "canvas", "screen", "camera", "browser" },
            invocableCommands = new[] { "system.run", "system.notify", "canvas.present", "screen.capture", "camera.snap" },
            desktop = true, desktopAvailability = new { state = "unlocked" },
        },
        new
        {
            id = "node:" + MacNodeId, type = "node", label = "Fixture MacBook", status = "unavailable", platform = "macos",
            lastConnectedAtMs = Before(TimeSpan.FromDays(1)), lastDisconnectedAtMs = Before(TimeSpan.FromHours(2)),
            lastSeenAtMs = Before(TimeSpan.FromHours(2)), lastSeenReason = "node-disconnected",
            capabilities = new[] { "system", "screen" }, desktop = true,
        },
        new
        {
            id = "node:" + PiNodeId, type = "node", label = "Fixture Raspberry Pi", status = "available", platform = "linux",
            lastConnectedAtMs = Ago(TimeSpan.FromMinutes(40)), lastSeenAtMs = Ago(TimeSpan.FromMinutes(1)),
            capabilities = new[] { "system" },
            issues = new object[]
            {
                new { code = "update-required", action = "update-and-reconnect", updateCommand = "openclaw update", headlessReconnectCommand = "openclaw node restart" },
            },
        },
        new
        {
            id = WorkerId, type = "worker", status = "available", platform = "linux", trust = "disposable",
            capabilities = new[] { "exec", "browser" }, desktop = true,
            worker = new
            {
                providerId = "fixture-cloud", profileId = "gpu-small", state = "attached", ageMs = 5_400_000,
                attachedSessionIds = new[] { "00000000-0000-4000-8000-000000000003" }, tunnelStatus = "connected", desktop = true,
            },
        },
        new
        {
            id = StartingWorkerId, type = "worker", status = "starting", platform = "linux",
            worker = new
            {
                providerId = "fixture-cloud", profileId = "cpu-large", state = "provisioning", ageMs = 30_000,
                attachedSessionIds = Array.Empty<string>(), tunnelStatus = "connecting",
            },
        },
        new
        {
            id = DestroyedWorkerId, type = "worker", status = "unavailable",
            worker = new
            {
                providerId = "fixture-cloud", profileId = "gpu-small", state = "destroyed", ageMs = 90_000_000,
                attachedSessionIds = Array.Empty<string>(), tunnelStatus = "stopped",
            },
        },
    ];

    private static double Wave(int sample, double period, double amplitude) =>
        Math.Sin(sample * 2 * Math.PI / period) * amplitude;

    private object SystemInfo(int sample)
    {
        var used = 18.5 * Gb + Wave(sample, 7, 2.5) * Gb;
        return new
        {
            machineName = "Fixture Gateway", hostname = "fixture-gw", platform = "linux", release = "6.8.0-45-generic",
            arch = "x64", osLabel = "Ubuntu 24.04", lanAddress = "192.168.1.20", port = 18789, nodeVersion = "v22.11.0",
            pid = 4242, uptimeMs = 3L * 24 * 3600 * 1000 + 5L * 3600 * 1000 + (long)sample * 15_000,
            cpuCount = 8, cpuModel = "Fixture Xeon E-2388G",
            loadAverage = new[] { 1.6 + Wave(sample, 6, 0.9), 1.4, 1.2 },
            memoryTotalBytes = 32 * Gb, memoryFreeBytes = (long)(32 * Gb - used),
            disks = new object[]
            {
                new { path = "/", totalBytes = 512 * Gb, availableBytes = (long)(212 * Gb - sample * 0.05 * Gb) },
                new { path = "/data", totalBytes = 2048 * Gb, availableBytes = (long)(1310 * Gb + Wave(sample, 9, 4) * Gb) },
            },
        };
    }

    private static object[] Nodes(int sample) =>
    [
        new
        {
            nodeId = StudioNodeId, displayName = "Fixture Studio PC", platform = "windows", version = "0.9.0", coreVersion = "2026.10.1",
            remoteIp = "192.168.1.31", clientId = "openclaw-windows", clientMode = "node", deviceFamily = "Windows",
            modelIdentifier = "Surface Studio", paired = true, connected = true,
            connectedAtMs = Ago(TimeSpan.FromHours(3)), approvedAtMs = Ago(TimeSpan.FromDays(12)),
            lastSeenAtMs = Ago(TimeSpan.FromSeconds(20)), lastSeenReason = "periodic",
            caps = new[] { "system", "canvas", "screen", "camera", "browser" },
            commands = new[] { "system.run", "system.notify", "canvas.present", "screen.capture", "camera.snap" },
            permissions = new Dictionary<string, bool> { ["camera"] = false, ["screen"] = true, ["system"] = true },
            pathEnv = @"C:\Windows\System32;C:\Program Files\Fixture\bin",
            hostStats = new
            {
                cpuCount = 16, memoryTotalBytes = 64 * Gb,
                memoryFreeBytes = (long)(64 * Gb - (28 * Gb + Wave(sample, 5, 6) * Gb)),
                diskTotalBytes = 1024 * Gb, diskAvailableBytes = (long)(402 * Gb + Wave(sample, 8, 3) * Gb),
                updatedAtMs = Ago(TimeSpan.FromSeconds(12)),
            },
        },
        new
        {
            nodeId = MacNodeId, displayName = "Fixture MacBook", platform = "macos 15.1", version = "0.8.4", deviceFamily = "Mac",
            modelIdentifier = "MacBookPro18,3", paired = true, connected = false,
            approvedAtMs = Before(TimeSpan.FromDays(40)), lastSeenAtMs = Before(TimeSpan.FromHours(2)), lastSeenReason = "node-disconnected",
            caps = new[] { "system", "screen" }, commands = new[] { "system.run", "screen.capture" },
            hostStats = new
            {
                cpuCount = 10, loadAverage = new[] { 2.1, 1.9, 1.7 }, memoryTotalBytes = 32 * Gb, memoryFreeBytes = 9 * Gb,
                diskTotalBytes = 1024 * Gb, diskAvailableBytes = 380 * Gb, updatedAtMs = Before(TimeSpan.FromHours(2)),
            },
        },
        new
        {
            nodeId = PiNodeId, displayName = "Fixture Raspberry Pi", platform = "linux", version = "0.7.2", deviceFamily = "Linux",
            paired = true, connected = true, connectedAtMs = Ago(TimeSpan.FromMinutes(40)),
            lastSeenAtMs = Ago(TimeSpan.FromMinutes(1)), caps = new[] { "system" }, commands = new[] { "system.run" },
        },
    ];

    private static object[] NodePending() =>
    [
        new
        {
            requestId = "fixture-node-request", nodeId = "fixture-travel-laptop", displayName = "Fixture Travel Laptop",
            platform = "windows", version = "0.9.0", remoteIp = "10.0.0.42", ts = Ago(TimeSpan.FromMinutes(2)),
        },
    ];

    private static object[] DevicePending() =>
    [
        new
        {
            requestId = "fixture-device-request", deviceId = "fixture-tablet", displayName = "Fixture Tablet", platform = "ipados",
            role = "operator", scopes = new[] { "operator.read", "operator.write" }, remoteIp = "10.0.0.57",
            ts = Ago(TimeSpan.FromMinutes(5)),
        },
    ];

    private static object Backups()
    {
        var archiveOk = new
        {
            id = "fixture-backup-1", createdAt = Ago(TimeSpan.FromHours(3)), archivePath = "/fixture/backups/home.tar",
            status = "ok", kind = "archive", target = "b2-main", @namespace = "home", bytes = 1_300_000_000L,
            location = new
            {
                name = "b2-main", provider = "b2", locationId = "fixture-loc-1", key = "home/2026", @namespace = "home",
                plaintextBytes = 1_300_000_000L, storedBytes = 1_250_000_000L,
            },
        };
        var gitPushed = new
        {
            id = "fixture-backup-2", createdAt = Ago(TimeSpan.FromMinutes(45)), archivePath = "/fixture/backups/config.git",
            status = "ok", kind = "git", target = "config-repo", pushFailed = true, bytes = 4_200_000L,
        };
        var gitOk = new
        {
            id = "fixture-backup-3", createdAt = Ago(TimeSpan.FromDays(1)), archivePath = "/fixture/backups/config.git",
            status = "ok", kind = "git", target = "config-repo", bytes = 4_100_000L,
        };
        var snapshotFailed = new
        {
            id = "fixture-backup-4", createdAt = Ago(TimeSpan.FromMinutes(20)), archivePath = "/fixture/backups/sessions.db",
            status = "failed", kind = "sqlite-snapshot", target = "sessions-db", error = "Synthetic failure: the destination volume is full.",
        };
        return new
        {
            targets = new object[]
            {
                new { kind = "archive", target = "b2-main", @namespace = "home", latest = archiveOk, latestOk = archiveOk },
                new { kind = "git", target = "config-repo", latest = gitPushed, latestOk = gitOk },
                new { kind = "sqlite-snapshot", target = "sessions-db", latest = snapshotFailed },
            },
            schedules = new object[]
            {
                new { id = "fixture-schedule-1", mode = "offsite", target = "b2-main", @namespace = "home", enabled = true, everyMs = 86_400_000, nextRunAtMs = Now + 21 * 3_600_000L },
                new { id = "fixture-schedule-2", mode = "git", target = "config-repo", enabled = true, everyMs = 3_600_000, nextRunAtMs = Now + 15 * 60_000L },
                new { id = "fixture-schedule-3", mode = "offsite", target = "nas", @namespace = "home", enabled = true, everyMs = 604_800_000, nextRunAtMs = Now + 3 * 86_400_000L },
            },
            locations = new object[]
            {
                new { name = "b2-main", provider = "b2", displayTarget = "b2://fixture-backups/home", encrypted = true },
                new { name = "nas", provider = "smb", displayTarget = @"\\fixture-nas\backups", encrypted = false },
                new { name = "usb-drive", provider = "local", displayTarget = @"E:\OpenClaw", encrypted = true },
            },
        };
    }

    private static object Probe(JsonElement parameters)
    {
        GatewayScenario.ValidateProperties(parameters, "name");
        return GatewayScenario.RequiredString(parameters, "name") switch
        {
            "b2-main" => new { state = "ok", freeBytes = 820 * Gb, totalBytes = 1024 * Gb },
            "nas" => (object)new { state = "unavailable", message = "Synthetic: host unreachable." },
            "usb-drive" => new { state = "uninitialized" },
            _ => new { state = "wrong-key" },
        };
    }
}
