using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public sealed record GatewayRegistrySnapshot(IReadOnlyList<GatewayRecord> Records, string? ActiveId);

/// <summary>
/// Pure data catalog of known gateway endpoints. Persistence only — no runtime state.
/// Thread-safe: lock-protected internal list; events fire outside the lock.
/// </summary>
public sealed class GatewayRegistry
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly string _gatewaysDir;
    private readonly IFileSystem _fs;
    private readonly IOpenClawLogger _logger;
    private List<GatewayRecord> _records = [];
    private string? _activeId;
    private GatewayRegistrySnapshot _persisted = new([], null);
    private string? _persistedInvalidJson;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public event EventHandler<GatewayRegistryChangedEventArgs>? Changed;

    /// <summary>
    /// Create a GatewayRegistry backed by the given data directory.
    /// </summary>
    /// <param name="dataDir">Root data directory (e.g. %APPDATA%/OpenClawTray).</param>
    /// <param name="fs">Filesystem abstraction for testability.</param>
    /// <param name="logger">Optional diagnostics sink for persistence problems.</param>
    public GatewayRegistry(string dataDir, IFileSystem? fs = null, IOpenClawLogger? logger = null)
    {
        _fs = fs ?? RealFileSystem.Instance;
        _logger = logger ?? NullLogger.Instance;
        _filePath = Path.Combine(dataDir, "gateways.json");
        _gatewaysDir = Path.Combine(dataDir, "gateways");
    }

    // ─── Query ───

    public IReadOnlyList<GatewayRecord> GetAll()
    {
        lock (_lock) return _records.ToList();
    }

    public GatewayRecord? GetById(string id)
    {
        lock (_lock) return _records.Find(r => r.Id == id);
    }

    public GatewayRecord? GetActive()
    {
        lock (_lock) return _activeId != null ? _records.Find(r => r.Id == _activeId) : null;
    }

    public string? ActiveGatewayId
    {
        get { lock (_lock) return _activeId; }
    }

    public GatewayRegistrySnapshot GetSnapshot()
    {
        lock (_lock) return new(_records.ToArray(), _activeId);
    }

    public static bool HasSameSetupAuthority(GatewayRegistrySnapshot left, GatewayRegistrySnapshot right) =>
        left.ActiveId == right.ActiveId &&
        left.Records.Select(record => record with { LastConnected = null }).SequenceEqual(
            right.Records.Select(record => record with { LastConnected = null }));

    public GatewayRegistrySnapshot CapturePersistedSnapshot()
    {
        lock (_lock)
        {
            using var lease = PersistenceFileLease.Acquire(_filePath);
            var data = _fs.FileExists(_filePath)
                ? JsonSerializer.Deserialize<RegistryData>(_fs.ReadAllText(_filePath), s_jsonOptions)
                    ?? throw new InvalidDataException("The saved Gateway registry is invalid.")
                : new RegistryData();
            if (!HasSameSetupAuthority(new(_records, _activeId), new(data.Gateways ?? [], data.ActiveId)))
                throw new InvalidOperationException("The Gateway registry has unsaved or external changes. Refresh before setup.");
            _persisted = new((data.Gateways ?? []).ToArray(), data.ActiveId);
            return new(_records.ToArray(), _activeId);
        }
    }

    public GatewayRegistrySnapshot ReconcileCompletedSetup(
        GatewayRegistrySnapshot baseline, GatewayRegistrySnapshot expectedOutput, string gatewayId) =>
        ReconcileSetupOutcome(baseline, expectedOutput, gatewayId);

    /// <summary>Settles known pipeline/rollback output on every outcome, including an empty registry.</summary>
    public GatewayRegistrySnapshot ReconcileSetupOutcome(
        GatewayRegistrySnapshot baseline, GatewayRegistrySnapshot expectedOutput, string? completedGatewayId = null) =>
        AdoptPersistedCore(baseline, expectedOutput, completedGatewayId);

    /// <summary>Explicit conflict recovery. Only the admitted, unchanged in-memory state may be replaced.</summary>
    public GatewayRegistrySnapshot AdoptPersistedSnapshot(GatewayRegistrySnapshot expectedMemory) =>
        AdoptPersistedCore(expectedMemory, null, null);

    private GatewayRegistrySnapshot AdoptPersistedCore(
        GatewayRegistrySnapshot baseline, GatewayRegistrySnapshot? expectedOutput, string? completedGatewayId)
    {
        GatewayRegistrySnapshot snapshot;
        bool changed;
        lock (_lock)
        {
            using var lease = PersistenceFileLease.Acquire(_filePath);
            if (!HasSameSetupAuthority(new(_records, _activeId), baseline))
                throw new InvalidOperationException("The Gateway registry changed during setup. Refresh before continuing.");
            var data = _fs.FileExists(_filePath)
                ? JsonSerializer.Deserialize<RegistryData>(_fs.ReadAllText(_filePath), s_jsonOptions)
                    ?? throw new InvalidDataException("The saved Gateway registry is invalid.")
                : new RegistryData();
            var records = data.Gateways ?? [];
            if (records.Any(record => string.IsNullOrWhiteSpace(record.Id)) ||
                records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != records.Count ||
                data.ActiveId is not null && records.All(record => record.Id != data.ActiveId) ||
                completedGatewayId is not null && data.ActiveId != completedGatewayId ||
                expectedOutput is not null && !HasSameSetupAuthority(new(records, data.ActiveId), expectedOutput))
                throw new InvalidDataException("The saved Gateway registry does not match this setup operation. Refresh before continuing.");
            var current = _records.ToDictionary(record => record.Id);
            _records = records.Select(record =>
                current.TryGetValue(record.Id, out var canonical) && canonical.LastConnected is { } connected &&
                (record.LastConnected is null || connected > record.LastConnected)
                    ? record with { LastConnected = connected } : record).ToList();
            _activeId = data.ActiveId;
            _persisted = new(records.ToArray(), data.ActiveId);
            snapshot = new(_records.ToArray(), _activeId);
            changed = !HasSameSetupAuthority(baseline, snapshot);
        }
        if (changed) Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot.Records, snapshot.ActiveId));
        return snapshot;
    }

    /// <summary>Applies an operation-owned state atomically; concurrent memory or disk authority is preserved.</summary>
    public GatewayRegistrySnapshot ReplaceSnapshotAndSave(GatewayRegistrySnapshot expectedMemory, GatewayRegistrySnapshot replacement)
    {
        GatewayRegistrySnapshot snapshot;
        lock (_lock)
        {
            if (!HasSameSetupAuthority(new(_records, _activeId), expectedMemory))
                throw new InvalidOperationException("The live Gateway registry changed during the operation.");
            var previousRecords = _records;
            var previousActive = _activeId;
            _records = replacement.Records.ToList();
            _activeId = replacement.ActiveId;
            try { SaveLocked(expectedMemory); }
            catch { _records = previousRecords; _activeId = previousActive; throw; }
            snapshot = new(_records.ToArray(), _activeId);
        }
        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot.Records, snapshot.ActiveId));
        return snapshot;
    }

    /// <summary>
    /// Returns the identity directory path for a given gateway ID.
    /// </summary>
    public string GetIdentityDirectory(string gatewayId)
    {
        return Path.Combine(_gatewaysDir, gatewayId);
    }

    /// <summary>Deletes only an operation's unchanged new key file after confirming no live or saved record adopted it.</summary>
    public bool RemoveUnregisteredIdentity(string gatewayId, DeviceIdentityReplacementTransaction creation)
    {
        if (!Guid.TryParse(gatewayId, out _))
            throw new ArgumentException("Candidate identity requires a generated gateway ID.", nameof(gatewayId));
        lock (_lock)
        {
            using var lease = PersistenceFileLease.Acquire(_filePath);
            var disk = _fs.FileExists(_filePath)
                ? JsonSerializer.Deserialize<RegistryData>(_fs.ReadAllText(_filePath), s_jsonOptions)
                    ?? throw new InvalidDataException("The saved Gateway registry is invalid.")
                : new RegistryData();
            if (_records.Any(record => record.Id == gatewayId) || disk.Gateways?.Any(record => record.Id == gatewayId) == true)
                return false;
            var directory = GetIdentityDirectory(gatewayId);
            var key = Path.GetFullPath(Path.Combine(directory, "device-key-ed25519.json"));
            if (!string.Equals(key, Path.GetFullPath(creation.IdentityPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("The creation transaction does not belong to this candidate.", nameof(creation));
            return DeviceIdentity.RemoveCreatedIdentity(creation);
        }
    }

    // ─── Mutate ───

    public GatewayRecord AddOrUpdate(GatewayRecord record)
    {
        List<GatewayRecord> snapshot;
        string? activeId;
        lock (_lock)
        {
            var idx = _records.FindIndex(r => r.Id == record.Id);
            if (idx >= 0)
                _records[idx] = record;
            else
                _records.Add(record);
            snapshot = _records.ToList();
            activeId = _activeId;
        }
        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot, activeId));
        return record;
    }

    public void Remove(string id)
    {
        List<GatewayRecord> snapshot;
        string? activeId;
        lock (_lock)
        {
            _records.RemoveAll(r => r.Id == id);
            if (_activeId == id) _activeId = null;
            snapshot = _records.ToList();
            activeId = _activeId;
        }
        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot, activeId));
    }

    public void SetActive(string? gatewayId)
    {
        List<GatewayRecord> snapshot;
        lock (_lock)
        {
            _activeId = gatewayId;
            snapshot = _records.ToList();
        }
        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot, gatewayId));
    }

    /// <summary>
    /// Atomically update a record in-place. The <paramref name="updater"/> runs under
    /// the registry lock so concurrent writes (e.g. clearing BootstrapToken while
    /// stamping LastConnected) don't overwrite each other.
    /// Returns the updated record, or null if the record was not found.
    /// </summary>
    public GatewayRecord? Update(string id, Func<GatewayRecord, GatewayRecord> updater)
    {
        GatewayRecord? updated;
        List<GatewayRecord> snapshot;
        string? activeId;
        lock (_lock)
        {
            var idx = _records.FindIndex(r => r.Id == id);
            if (idx < 0) return null;
            updated = updater(_records[idx]);
            ArgumentNullException.ThrowIfNull(updated, nameof(updater));
            _records[idx] = updated;
            snapshot = _records.ToList();
            activeId = _activeId;
        }
        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot, activeId));
        return updated;
    }

    /// <summary>
    /// Atomically update and persist a record. If persistence fails, the in-memory
    /// record is restored so a later lifecycle event can safely retry the update.
    /// </summary>
    public GatewayRecord? UpdateAndSave(string id, Func<GatewayRecord, GatewayRecord> updater)
    {
        GatewayRecord? updated;
        List<GatewayRecord> snapshot;
        string? activeId;
        lock (_lock)
        {
            var idx = _records.FindIndex(r => r.Id == id);
            if (idx < 0) return null;

            var original = _records[idx];
            updated = updater(original);
            ArgumentNullException.ThrowIfNull(updated, nameof(updater));
            _records[idx] = updated;

            try
            {
                SaveLocked();
                updated = _records[idx];
            }
            catch
            {
                _records[idx] = original;
                throw;
            }

            snapshot = _records.ToList();
            activeId = _activeId;
        }

        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot, activeId));
        return updated;
    }

    // ─── Persistence ───

    public void Save() => Save(expected: null);

    public void Save(GatewayRegistrySnapshot? expected)
    {
        lock (_lock)
            SaveLocked(expected);
    }

    private void SaveLocked(GatewayRegistrySnapshot? expected = null)
    {
        using var lease = PersistenceFileLease.Acquire(_filePath);
        var diskJson = _fs.FileExists(_filePath) ? _fs.ReadAllText(_filePath) : null;
        RegistryData disk;
        try
        {
            disk = diskJson is null
                ? new RegistryData()
                : JsonSerializer.Deserialize<RegistryData>(diskJson, s_jsonOptions)
                    ?? throw new InvalidDataException("The saved Gateway registry is invalid.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException &&
            _persistedInvalidJson is not null &&
            string.Equals(diskJson, _persistedInvalidJson, StringComparison.Ordinal))
        {
            disk = new RegistryData();
        }
        var diskSnapshot = new GatewayRegistrySnapshot(disk.Gateways ?? [], disk.ActiveId);
        if (!HasSameSetupAuthority(diskSnapshot, _persisted) ||
            expected is not null && !HasSameSetupAuthority(diskSnapshot, expected))
            throw new InvalidOperationException("The saved Gateway registry changed. Reload before saving.");
        var diskById = diskSnapshot.Records.ToDictionary(record => record.Id);
        var records = _records.Select(record =>
            diskById.TryGetValue(record.Id, out var current) &&
            (record with { LastConnected = null }) == (current with { LastConnected = null }) &&
            current.LastConnected is { } stamp && (record.LastConnected is null || stamp > record.LastConnected)
                ? record with { LastConnected = stamp } : record).ToList();
        var data = new RegistryData { Gateways = records, ActiveId = _activeId };
        var json = JsonSerializer.Serialize(data, s_jsonOptions);

        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null && !_fs.DirectoryExists(dir))
            _fs.CreateDirectory(dir);

        // Atomic write: unique temp file then rename. Keep the registry lock
        // through the move so concurrent saves cannot publish stale snapshots.
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            _fs.WriteAllText(tempPath, json);
            _fs.MoveFile(tempPath, _filePath, overwrite: true);
            _records = records;
            _persisted = new(records.ToArray(), _activeId);
            _persistedInvalidJson = null;
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (_fs.FileExists(tempPath))
                _fs.DeleteFile(tempPath);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to delete temporary gateway registry file '{tempPath}': {ex.Message}");
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            using var lease = PersistenceFileLease.Acquire(_filePath);
            if (!_fs.FileExists(_filePath))
            {
                _records = [];
                _activeId = null;
                _persisted = new([], null);
                _persistedInvalidJson = null;
                return;
            }
            string? json = null;
            try
            {
                json = _fs.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<RegistryData>(json, s_jsonOptions)
                    ?? throw new InvalidDataException("The saved Gateway registry is invalid.");
                _records = data.Gateways ?? [];
                _activeId = data.ActiveId;
                _persisted = new(_records.ToArray(), _activeId);
                _persistedInvalidJson = null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                _records = [];
                _activeId = null;
                _persisted = new([], null);
                _persistedInvalidJson = json;
                _logger.Warn($"Gateway registry file '{_filePath}' is not valid JSON; starting with an empty registry. {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Find a gateway record by URL. Used during migration and setup code apply.
    /// </summary>
    public GatewayRecord? FindByUrl(string url)
    {
        lock (_lock) return _records.Find(r =>
            GatewayUrlsEquivalent(r.Url, url));
    }

    /// <summary>
    /// Find all gateway records with an equivalent URL.
    /// </summary>
    public IReadOnlyList<GatewayRecord> FindAllByUrl(string url)
    {
        lock (_lock) return _records.Where(r =>
            GatewayUrlsEquivalent(r.Url, url)).ToList();
    }

    private static bool GatewayUrlsEquivalent(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!GatewayUrlHelper.TryNormalizeWebSocketUrl(left, out var normalizedLeft) ||
            !GatewayUrlHelper.TryNormalizeWebSocketUrl(right, out var normalizedRight))
        {
            return false;
        }

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(normalizedLeft, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(normalizedRight, UriKind.Absolute, out var rightUri))
        {
            return false;
        }

        return string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            leftUri.Port == rightUri.Port &&
            IsLoopbackHost(leftUri.Host) &&
            IsLoopbackHost(rightUri.Host) &&
            string.Equals(leftUri.PathAndQuery, rightUri.PathAndQuery, StringComparison.Ordinal);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Migrate credentials from legacy SettingsManager fields to GatewayRecord.
    /// Idempotent: skips if a record for the same URL already exists.
    /// Identity file is COPIED (not moved) for rollback safety.
    /// </summary>
    public bool MigrateFromSettings(
        string? gatewayUrl,
        string? token,
        string? bootstrapToken,
        bool useSshTunnel,
        string? sshUser,
        string? sshHost,
        int sshRemotePort,
        int sshLocalPort,
        string settingsDir,
        IOpenClawLogger? logger = null) =>
        MigrateFromSettings(
            gatewayUrl,
            token,
            bootstrapToken,
            useSshTunnel,
            sshUser,
            sshHost,
            sshPort: 22,
            sshRemotePort,
            sshLocalPort,
            settingsDir,
            logger);

    public bool MigrateFromSettings(
        string? gatewayUrl,
        string? token,
        string? bootstrapToken,
        bool useSshTunnel,
        string? sshUser,
        string? sshHost,
        int sshPort,
        int sshRemotePort,
        int sshLocalPort,
        bool includeBrowserProxyForward,
        string settingsDir,
        IOpenClawLogger? logger = null) =>
        MigrateFromSettingsCore(
            gatewayUrl,
            token,
            bootstrapToken,
            useSshTunnel,
            sshUser,
            sshHost,
            sshPort,
            sshRemotePort,
            sshLocalPort,
            includeBrowserProxyForward,
            settingsDir,
            logger);

    public bool MigrateFromSettings(
        string? gatewayUrl,
        string? token,
        string? bootstrapToken,
        bool useSshTunnel,
        string? sshUser,
        string? sshHost,
        int sshPort,
        int sshRemotePort,
        int sshLocalPort,
        string settingsDir,
        IOpenClawLogger? logger = null)
        => MigrateFromSettingsCore(
            gatewayUrl,
            token,
            bootstrapToken,
            useSshTunnel,
            sshUser,
            sshHost,
            sshPort,
            sshRemotePort,
            sshLocalPort,
            includeBrowserProxyForward: false,
            settingsDir,
            logger);

    private bool MigrateFromSettingsCore(
        string? gatewayUrl,
        string? token,
        string? bootstrapToken,
        bool useSshTunnel,
        string? sshUser,
        string? sshHost,
        int sshPort,
        int sshRemotePort,
        int sshLocalPort,
        bool includeBrowserProxyForward,
        string settingsDir,
        IOpenClawLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return false;

        // Idempotent: don't duplicate if already migrated
        if (FindByUrl(gatewayUrl) != null)
        {
            logger?.Info($"[Registry] Migration skipped — record already exists for {gatewayUrl}");
            return false;
        }

        var id = Guid.NewGuid().ToString();
        var isLocal = LocalGatewayUrlClassifier.IsLocalGatewayUrl(gatewayUrl);
        var setupManagedDistroName =
            isLocal && !useSshTunnel
                ? TryReadSetupManagedDistroName(settingsDir, gatewayUrl)
                : null;
        var record = new GatewayRecord
        {
            Id = id,
            Url = gatewayUrl,
            IsLocal = isLocal,
            FriendlyName = setupManagedDistroName is null
                ? null
                : $"Local ({setupManagedDistroName})",
            SetupManagedDistroName = setupManagedDistroName,
            SharedGatewayToken = string.IsNullOrWhiteSpace(bootstrapToken) ? token : null,
            BootstrapToken = !string.IsNullOrWhiteSpace(bootstrapToken) ? bootstrapToken : null,
            SshTunnel = useSshTunnel
                ? new SshTunnelConfig(
                    sshUser ?? "",
                    sshHost ?? "",
                    sshRemotePort,
                    sshLocalPort,
                    includeBrowserProxyForward,
                    sshPort)
                : null
        };

        AddOrUpdate(record);
        SetActive(id);

        // Copy identity file to per-gateway directory (rollback safe — original stays)
        var legacyIdentity = Path.Combine(settingsDir, "device-key-ed25519.json");
        var newIdentityDir = GetIdentityDirectory(id);
        if (File.Exists(legacyIdentity))
        {
            try
            {
                if (!Directory.Exists(newIdentityDir))
                    Directory.CreateDirectory(newIdentityDir);
                var dest = Path.Combine(newIdentityDir, "device-key-ed25519.json");
                if (!File.Exists(dest))
                    File.Copy(legacyIdentity, dest, overwrite: false);
                logger?.Info($"[Registry] Identity file copied to {newIdentityDir}");
            }
            catch (Exception ex)
            {
                logger?.Warn($"[Registry] Failed to copy identity file: {ex.Message}");
            }
        }

        Save();
        logger?.Info($"[Registry] Migrated gateway {gatewayUrl} → record {id}");
        return true;
    }

    private static string? TryReadSetupManagedDistroName(
        string settingsDir,
        string gatewayUrl)
    {
        try
        {
            var direct = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
            var localRoot = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsDirectoryName = Path.GetFileName(
                settingsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var preferredDataDirectory = settingsDirectoryName.EndsWith(
                "-Dev",
                StringComparison.OrdinalIgnoreCase)
                    ? "OpenClawTray-Dev"
                    : "OpenClawTray";
            var alternateDataDirectory = preferredDataDirectory == "OpenClawTray"
                ? "OpenClawTray-Dev"
                : "OpenClawTray";
            var candidates = string.IsNullOrWhiteSpace(direct)
                ? new[]
                {
                    Path.Combine(localRoot, preferredDataDirectory, "setup-state.json"),
                    Path.Combine(localRoot, alternateDataDirectory, "setup-state.json"),
                }
                : [Path.Combine(direct, "setup-state.json")];
            foreach (var statePath in candidates)
            {
                if (!File.Exists(statePath))
                    continue;
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
                if (document.RootElement.TryGetProperty("DistroName", out var distroElement) &&
                    !string.IsNullOrWhiteSpace(distroElement.GetString()) &&
                    document.RootElement.TryGetProperty("GatewayUrl", out var gatewayUrlElement) &&
                    GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
                        gatewayUrl,
                        gatewayUrlElement.GetString()))
                {
                    return distroElement.GetString();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class RegistryData
    {
        public List<GatewayRecord>? Gateways { get; set; }
        public string? ActiveId { get; set; }
    }
}

public sealed class GatewayRegistryChangedEventArgs : EventArgs
{
    public IReadOnlyList<GatewayRecord> Records { get; }
    public string? ActiveGatewayId { get; }
    public GatewayRegistryChangedEventArgs(IReadOnlyList<GatewayRecord> records, string? activeGatewayId = null)
    {
        Records = records;
        ActiveGatewayId = activeGatewayId;
    }
}
