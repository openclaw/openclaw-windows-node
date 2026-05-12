using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

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
    private List<GatewayRecord> _records = [];
    private string? _activeId;

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
    public GatewayRegistry(string dataDir, IFileSystem? fs = null)
    {
        _fs = fs ?? RealFileSystem.Instance;
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

    /// <summary>
    /// Returns the identity directory path for a given gateway ID.
    /// </summary>
    public string GetIdentityDirectory(string gatewayId)
    {
        return Path.Combine(_gatewaysDir, gatewayId);
    }

    // ─── Mutate ───

    public GatewayRecord AddOrUpdate(GatewayRecord record)
    {
        List<GatewayRecord> snapshot;
        lock (_lock)
        {
            var idx = _records.FindIndex(r => r.Id == record.Id);
            if (idx >= 0)
                _records[idx] = record;
            else
                _records.Add(record);
            snapshot = _records.ToList();
        }
        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot));
        return record;
    }

    public void Remove(string id)
    {
        List<GatewayRecord> snapshot;
        lock (_lock)
        {
            _records.RemoveAll(r => r.Id == id);
            if (_activeId == id) _activeId = null;
            snapshot = _records.ToList();
        }
        Changed?.Invoke(this, new GatewayRegistryChangedEventArgs(snapshot));
    }

    public void SetActive(string gatewayId)
    {
        lock (_lock) _activeId = gatewayId;
    }

    // ─── Persistence ───

    public void Save()
    {
        RegistryData data;
        lock (_lock)
        {
            data = new RegistryData { Gateways = _records.ToList(), ActiveId = _activeId };
        }
        var json = JsonSerializer.Serialize(data, s_jsonOptions);

        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null && !_fs.DirectoryExists(dir))
            _fs.CreateDirectory(dir);

        // Atomic write: temp file then rename
        var tempPath = _filePath + ".tmp";
        _fs.WriteAllText(tempPath, json);
        // On Windows, File.Move with overwrite works as atomic rename
        File.Move(tempPath, _filePath, overwrite: true);
    }

    public void Load()
    {
        if (!_fs.FileExists(_filePath))
            return;

        try
        {
            var json = _fs.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<RegistryData>(json, s_jsonOptions);
            if (data != null)
            {
                lock (_lock)
                {
                    _records = data.Gateways ?? [];
                    _activeId = data.ActiveId;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupted file — start fresh
        }
    }

    /// <summary>
    /// Find a gateway record by URL. Used during migration and setup code apply.
    /// </summary>
    public GatewayRecord? FindByUrl(string url)
    {
        lock (_lock) return _records.Find(r =>
            string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase));
    }

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
        var record = new GatewayRecord
        {
            Id = id,
            Url = gatewayUrl,
            IsLocal = LocalGatewayUrlClassifier.IsLocalGatewayUrl(gatewayUrl),
            SharedGatewayToken = string.IsNullOrWhiteSpace(bootstrapToken) ? token : null,
            BootstrapToken = !string.IsNullOrWhiteSpace(bootstrapToken) ? bootstrapToken : null,
            SshTunnel = useSshTunnel
                ? new SshTunnelConfig(sshUser ?? "", sshHost ?? "", sshRemotePort, sshLocalPort)
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

    private sealed class RegistryData
    {
        public List<GatewayRecord>? Gateways { get; set; }
        public string? ActiveId { get; set; }
    }
}

public sealed class GatewayRegistryChangedEventArgs : EventArgs
{
    public IReadOnlyList<GatewayRecord> Records { get; }
    public GatewayRegistryChangedEventArgs(IReadOnlyList<GatewayRecord> records)
    {
        Records = records;
    }
}
