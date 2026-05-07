using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Mcp;
using NSec.Cryptography;

namespace OpenClaw.Shared;

/// <summary>
/// Manages device identity (keypair) for node authentication using Ed25519
/// </summary>
public class DeviceIdentity
{
    private readonly string _keyPath;
    private readonly IOpenClawLogger _logger;
    private Key? _privateKey;
    private PublicKey? _publicKey;
    private string? _deviceId;
    private string? _deviceToken;
    private string[]? _deviceTokenScopes;
    private string? _nodeDeviceToken;
    private string[]? _nodeDeviceTokenScopes;
    
    private static readonly SignatureAlgorithm Ed25519Algorithm = SignatureAlgorithm.Ed25519;
    
    public string DeviceId => _deviceId ?? throw new InvalidOperationException("Device not initialized");
    public string PublicKeyBase64Url => _publicKey != null ? Base64UrlEncode(_publicKey.Export(KeyBlobFormat.RawPublicKey)) : throw new InvalidOperationException("Device not initialized");
    public string? DeviceToken => _deviceToken;
    public IReadOnlyList<string>? DeviceTokenScopes => _deviceTokenScopes;
    public string? NodeDeviceToken => _nodeDeviceToken;
    public IReadOnlyList<string>? NodeDeviceTokenScopes => _nodeDeviceTokenScopes;

    public static string? TryReadStoredDeviceToken(string dataPath, IOpenClawLogger? logger = null) =>
        TryReadStoredDeviceTokenForRole(dataPath, "operator", logger);

    public static string? TryReadStoredDeviceTokenForRole(string dataPath, string role, IOpenClawLogger? logger = null)
    {
        var tokenRole = ParseDeviceTokenRole(role);
        var keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        if (!File.Exists(keyPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(keyPath));
            var tokenPropertyName = tokenRole == DeviceTokenRole.Node
                ? nameof(DeviceKeyData.NodeDeviceToken)
                : nameof(DeviceKeyData.DeviceToken);

            if (doc.RootElement.TryGetProperty(tokenPropertyName, out var deviceToken) &&
                deviceToken.ValueKind == JsonValueKind.String)
            {
                var value = deviceToken.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (IOException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
        }

        return null;
    }

    public static bool HasStoredDeviceToken(string dataPath, IOpenClawLogger? logger = null) =>
        !string.IsNullOrWhiteSpace(TryReadStoredDeviceToken(dataPath, logger));

    public static bool HasStoredDeviceTokenForRole(string dataPath, string role, IOpenClawLogger? logger = null) =>
        !string.IsNullOrWhiteSpace(TryReadStoredDeviceTokenForRole(dataPath, role, logger));
    
    public DeviceIdentity(string dataPath, IOpenClawLogger? logger = null)
    {
        _keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        _logger = logger ?? NullLogger.Instance;
    }
    
    /// <summary>
    /// Initialize the device identity - loads existing or generates new keypair
    /// </summary>
    public void Initialize()
    {
        if (File.Exists(_keyPath))
        {
            LoadExisting();
        }
        else
        {
            GenerateNew();
        }
    }
    
    private void LoadExisting()
    {
        try
        {
            var json = File.ReadAllText(_keyPath);
            var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
            
            if (data == null || string.IsNullOrEmpty(data.PrivateKeyBase64))
            {
                _logger.Warn("Invalid device key file, generating new");
                GenerateNew();
                return;
            }
            
            var privateKeyBytes = Convert.FromBase64String(data.PrivateKeyBase64);
            _privateKey = Key.Import(Ed25519Algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
            _publicKey = _privateKey.PublicKey;
            _deviceId = data.DeviceId;
            _deviceToken = data.DeviceToken;
            _deviceTokenScopes = NormalizeScopes(data.DeviceTokenScopes);
            _nodeDeviceToken = data.NodeDeviceToken;
            _nodeDeviceTokenScopes = NormalizeScopes(data.NodeDeviceTokenScopes);
            
            _logger.Info($"Loaded Ed25519 device identity: {_deviceId?[..16]}...");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load device key: {ex.Message}");
            GenerateNew();
        }
    }
    
    private void GenerateNew()
    {
        _logger.Info("Generating new Ed25519 device keypair...");
        
        // Generate Ed25519 keypair using NSec
        _privateKey = Key.Create(Ed25519Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _publicKey = _privateKey.PublicKey;
        
        // Get raw 32-byte public key
        var publicKeyBytes = _publicKey.Export(KeyBlobFormat.RawPublicKey);
        
        // Device ID is SHA256 hash of raw 32-byte public key (hex encoded)
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(publicKeyBytes);
        _deviceId = Convert.ToHexString(hashBytes).ToLowerInvariant();
        
        // Export private key for storage
        var privateKeyBytes = _privateKey.Export(KeyBlobFormat.RawPrivateKey);
        
        // Save to disk
        var data = new DeviceKeyData
        {
            PrivateKeyBase64 = Convert.ToBase64String(privateKeyBytes),
            PublicKeyBase64 = Convert.ToBase64String(publicKeyBytes),
            DeviceId = _deviceId,
            Algorithm = "Ed25519",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        if (!string.IsNullOrEmpty(dir))
            McpAuthToken.TryRestrictDataDirectoryAcl(dir);
        
        File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        McpAuthToken.TryRestrictSensitiveFileAcl(_keyPath);
        _logger.Info($"Generated new Ed25519 device identity: {_deviceId}");
    }
    
    /// <summary>
    /// Sign a payload for device authentication
    /// Payload format: v2|{deviceId}|{client.id}|{client.mode}|{role}|{scopes}|{signedAtMs}|{token}|{nonce}
    /// IMPORTANT: {token} is the auth.token from the connect request, NOT the device token!
    /// </summary>
    public string SignPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_privateKey == null || _deviceId == null)
            throw new InvalidOperationException("Device not initialized");
        
        // Build the payload to sign
        var payload = BuildDebugPayload(nonce, signedAtMs, clientId, authToken);
        
        // Sign with Ed25519
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Algorithm.Sign(_privateKey, dataBytes);
        
        // Return base64url encoded signature
        return Base64UrlEncode(signature);
    }

    /// <summary>
    /// Sign a v3 connect payload for operator/client connections.
    /// Format: v3|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}|{platform}|{deviceFamily}
    /// </summary>
    public string SignConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        if (_privateKey == null)
            throw new InvalidOperationException("Device not initialized");

        var payload = BuildConnectPayloadV3(
            nonce,
            signedAtMs,
            clientId,
            clientMode,
            role,
            scopes,
            authToken,
            platform,
            deviceFamily);

        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Algorithm.Sign(_privateKey, dataBytes);
        return Base64UrlEncode(signature);
    }

    /// <summary>
    /// Build the v3 connect payload string for signing/debugging.
    /// Format: v3|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}|{platform}|{deviceFamily}
    /// </summary>
    public string BuildConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");

        var scopesCsv = string.Join(",", scopes ?? Array.Empty<string>());
        var safeToken = authToken ?? string.Empty;
        var safeNonce = nonce ?? string.Empty;

        return $"v3|{_deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{safeToken}|{safeNonce}|{platform}|{deviceFamily}";
    }

    /// <summary>
    /// Sign a v2 connect payload for compatibility mode.
    /// Format: v2|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}
    /// </summary>
    public string SignConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken)
    {
        if (_privateKey == null)
            throw new InvalidOperationException("Device not initialized");

        var payload = BuildConnectPayloadV2(
            nonce,
            signedAtMs,
            clientId,
            clientMode,
            role,
            scopes,
            authToken);

        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Algorithm.Sign(_privateKey, dataBytes);
        return Base64UrlEncode(signature);
    }

    /// <summary>
    /// Build the v2 connect payload string for signing/debugging.
    /// Format: v2|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}
    /// </summary>
    public string BuildConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");

        var scopesCsv = string.Join(",", scopes ?? Array.Empty<string>());
        var safeToken = authToken ?? string.Empty;
        var safeNonce = nonce ?? string.Empty;

        return $"v2|{_deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{safeToken}|{safeNonce}";
    }
    
    /// <summary>
    /// Build the payload string (for debugging)
    /// Format: v2|{deviceId}|{clientId}|{clientMode}|{role}||{signedAtMs}|{token}|{nonce}
    /// IMPORTANT: {token} is the auth.token from connect request!
    /// </summary>
    public string BuildDebugPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");
            
        // - clientId must match client.id in connect request
        // - clientMode = "node"
        // - role = "node" 
        // - scopes = empty
        // - token = the auth.token being used in the connect request
        return $"v2|{_deviceId}|{clientId}|node|node||{signedAtMs}|{authToken}|{nonce}";
    }
    
    /// <summary>
    /// Store the device token received after pairing approval
    /// </summary>
    public void StoreDeviceToken(string token)
    {
        StoreDeviceTokenCore(token, null);
    }

    public void StoreDeviceTokenWithScopes(string token, IEnumerable<string>? scopes)
    {
        StoreDeviceTokenCore(token, NormalizeScopes(scopes));
    }

    public void StoreDeviceTokenForRole(string role, string token, IEnumerable<string>? scopes = null)
    {
        var tokenRole = ParseDeviceTokenRole(role);
        if (tokenRole == DeviceTokenRole.Node)
        {
            StoreNodeDeviceTokenCore(token, NormalizeScopes(scopes));
            return;
        }

        StoreDeviceTokenCore(token, NormalizeScopes(scopes));
    }

    private static DeviceTokenRole ParseDeviceTokenRole(string role) => role switch
    {
        "operator" => DeviceTokenRole.Operator,
        "node" => DeviceTokenRole.Node,
        _ => throw new ArgumentOutOfRangeException(nameof(role), "Device token role must be 'operator' or 'node'.")
    };

    private void StoreDeviceTokenCore(string token, string[]? scopes)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Device token cannot be empty.", nameof(token));

        _deviceToken = token;
        _deviceTokenScopes = scopes;
        
        // Update the key file with the token
        try
        {
            if (File.Exists(_keyPath))
            {
                var json = File.ReadAllText(_keyPath);
                var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
                if (data != null)
                {
                    data.DeviceToken = token;
                    data.DeviceTokenScopes = scopes;
                    File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                    McpAuthToken.TryRestrictSensitiveFileAcl(_keyPath);
                    _logger.Info("Device token stored");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to store device token: {ex.Message}");
        }
    }

    private void StoreNodeDeviceTokenCore(string token, string[]? scopes)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Device token cannot be empty.", nameof(token));

        _nodeDeviceToken = token;
        _nodeDeviceTokenScopes = scopes;

        try
        {
            if (File.Exists(_keyPath))
            {
                var json = File.ReadAllText(_keyPath);
                var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
                if (data != null)
                {
                    data.NodeDeviceToken = token;
                    data.NodeDeviceTokenScopes = scopes;
                    File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                    _logger.Info("Node device token stored");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to store node device token: {ex.Message}");
        }
    }

    private static string[]? NormalizeScopes(IEnumerable<string>? scopes)
    {
        if (scopes == null)
            return null;

        var normalized = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }
    
    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
    
    private enum DeviceTokenRole
    {
        Operator,
        Node
    }

    private class DeviceKeyData
    {
        public string? PrivateKeyBase64 { get; set; }
        public string? PublicKeyBase64 { get; set; }
        public string? DeviceId { get; set; }
        public string? DeviceToken { get; set; }
        public string[]? DeviceTokenScopes { get; set; }
        public string? NodeDeviceToken { get; set; }
        public string[]? NodeDeviceTokenScopes { get; set; }
        public string? Algorithm { get; set; }
        public long CreatedAt { get; set; }
    }
}
