using System.Text.Json;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.Shared;

/// <summary>
/// Manages a registry of paired gateways persisted to gateways.json.
/// Thread-safe — all public methods are serialized via an internal lock.
/// </summary>
public class GatewayRegistry
{
    private const string FileName = "gateways.json";

    private readonly string _filePath;
    private readonly string _directory;
    private readonly object _lock = new();
    private GatewayRegistryData _data = new();

    /// <summary>
    /// Runtime-only connection status of the active gateway (not persisted).
    /// </summary>
    public ConnectionStatus ActiveConnectionStatus { get; set; }

    /// <summary>
    /// Runtime-only reference to the connected gateway client (not persisted).
    /// </summary>
    public OpenClawGatewayClient? ActiveClient { get; set; }

    public GatewayRegistry(string dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
            throw new ArgumentException("Data path cannot be empty.", nameof(dataPath));

        _directory = dataPath;
        _filePath = Path.Combine(dataPath, FileName);
        Load();
    }

    /// <summary>
    /// Returns a clone of the active gateway record, or null if none is active.
    /// </summary>
    public GatewayRecord? GetActive()
    {
        lock (_lock)
        {
            var active = FindActive();
            return active?.Clone();
        }
    }

    /// <summary>
    /// Sets the active gateway by ID. Returns false if the ID is not found.
    /// </summary>
    public bool SetActive(string id)
    {
        lock (_lock)
        {
            var gw = _data.Gateways.FirstOrDefault(g => g.Id == id);
            if (gw == null) return false;
            _data.ActiveGatewayId = id;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Adds or updates a gateway record (matched by ID).
    /// </summary>
    public void AddOrUpdate(GatewayRecord record, bool setActive = true)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (string.IsNullOrWhiteSpace(record.Id))
            throw new ArgumentException("Gateway record must have an Id.", nameof(record));

        lock (_lock)
        {
            var existing = _data.Gateways.FirstOrDefault(g => g.Id == record.Id);
            if (existing != null)
            {
                existing.Url = record.Url;
                if (record.OperatorDeviceToken != null)
                    existing.OperatorDeviceToken = record.OperatorDeviceToken;
                if (record.NodeDeviceToken != null)
                    existing.NodeDeviceToken = record.NodeDeviceToken;
                // BootstrapToken: always overwrite (null = cleared after pairing)
                existing.BootstrapToken = record.BootstrapToken;
            }
            else
            {
                _data.Gateways.Add(record.Clone());
            }

            if (setActive)
                _data.ActiveGatewayId = record.Id;

            Save();
        }
    }

    /// <summary>
    /// Removes a gateway by ID. Clears activeGatewayId if it was the active one.
    /// Returns true if a record was removed.
    /// </summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _data.Gateways.RemoveAll(g => g.Id == id) > 0;
            if (removed && _data.ActiveGatewayId == id)
                _data.ActiveGatewayId = null;
            if (removed)
                Save();
            return removed;
        }
    }

    /// <summary>
    /// Returns a snapshot of all gateway records.
    /// </summary>
    public IReadOnlyList<GatewayRecord> GetAll()
    {
        lock (_lock)
        {
            return _data.Gateways.Select(g => g.Clone()).ToList();
        }
    }

    /// <summary>
    /// One-time migration: creates a gateway record from existing settings if the
    /// registry is empty. Returns true if migration occurred.
    /// </summary>
    public bool TryMigrateFromSettings(string? url, string? operatorToken, string? nodeDeviceToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        lock (_lock)
        {
            if (_data.Gateways.Count > 0)
                return false;

            var record = new GatewayRecord
            {
                Id = GatewayRecord.GenerateId(url),
                Url = url,
                OperatorDeviceToken = string.IsNullOrWhiteSpace(operatorToken) ? null : operatorToken,
                NodeDeviceToken = string.IsNullOrWhiteSpace(nodeDeviceToken) ? null : nodeDeviceToken,
            };

            _data.Gateways.Add(record);
            _data.ActiveGatewayId = record.Id;
            Save();
            return true;
        }
    }

    // ── Internal ──

    private GatewayRecord? FindActive() =>
        _data.Gateways.FirstOrDefault(g => g.Id == _data.ActiveGatewayId);

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _data = GatewayRegistryData.FromJson(json) ?? new GatewayRegistryData();
            }
        }
        catch (Exception)
        {
            _data = new GatewayRegistryData();
        }
    }

    /// <summary>
    /// Atomic save: write to temp file, then rename to avoid corruption.
    /// </summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            var json = _data.ToJson();
            File.WriteAllText(tempPath, json);
            McpAuthToken.TryRestrictSensitiveFileAcl(tempPath);
            File.Move(tempPath, _filePath, overwrite: true);
            // Re-apply ACL after move — Windows may inherit target's existing DACL
            McpAuthToken.TryRestrictSensitiveFileAcl(_filePath);
        }
        catch (Exception)
        {
            // Best-effort persistence — don't crash the app
        }
    }
}
