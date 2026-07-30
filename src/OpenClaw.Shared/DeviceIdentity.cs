using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Mcp;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace OpenClaw.Shared;

/// <summary>
/// Manages device identity (keypair) for node authentication using Ed25519
/// </summary>
public class DeviceIdentity
{
    private readonly string _keyPath;
    private readonly IOpenClawLogger _logger;
    private readonly IDeviceIdentityFileSystem _fileSystem;
    private byte[]? _privateKey;
    private byte[]? _publicKey;
    private string? _deviceId;
    private string? _deviceToken;
    private string[]? _deviceTokenScopes;
    private string? _nodeDeviceToken;
    private string[]? _nodeDeviceTokenScopes;
    
    public string DeviceId => _deviceId ?? throw new InvalidOperationException("Device not initialized");
    public string PublicKeyBase64Url => _publicKey != null ? Base64UrlEncode(_publicKey) : throw new InvalidOperationException("Device not initialized");
    public string? DeviceToken => _deviceToken;
    public IReadOnlyList<string>? DeviceTokenScopes => _deviceTokenScopes;
    public string? NodeDeviceToken => _nodeDeviceToken;
    public IReadOnlyList<string>? NodeDeviceTokenScopes => _nodeDeviceTokenScopes;

    public static string? TryReadStoredDeviceToken(string dataPath, IOpenClawLogger? logger = null) =>
        ResolveStoredToken(dataPath, ReadStoredDeviceToken(dataPath, logger));

    public static DeviceTokenReadResult ReadStoredDeviceToken(string dataPath, IOpenClawLogger? logger = null) =>
        ReadStoredDeviceTokenForRole(dataPath, "operator", logger);

    public static string? TryReadStoredDeviceTokenForRole(string dataPath, string role, IOpenClawLogger? logger = null) =>
        ResolveStoredToken(dataPath, ReadStoredDeviceTokenForRole(dataPath, role, logger));

    public static DeviceTokenReadResult ReadStoredDeviceTokenForRole(
        string dataPath,
        string role,
        IOpenClawLogger? logger = null) =>
        ReadStoredDeviceTokenForRole(dataPath, role, logger, DeviceIdentityFileSystem.Instance);

    internal static DeviceTokenReadResult ReadStoredDeviceTokenForRole(
        string dataPath,
        string role,
        IOpenClawLogger? logger,
        IDeviceIdentityFileSystem fileSystem)
    {
        var tokenRole = ParseDeviceTokenRole(role);
        var keyPath = Path.Combine(dataPath, "device-key-ed25519.json");

        try
        {
            if (!fileSystem.IdentityFileExists(keyPath))
                return DeviceTokenReadResult.Missing("Identity file is missing.");

            var json = fileSystem.ReadAllText(keyPath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Identity file root is not a JSON object.");

            var data = document.RootElement.Deserialize<DeviceKeyData>()
                ?? throw new InvalidDataException("Identity JSON did not contain an object.");
            _ = ValidateAndReconstruct(data);

            var token = tokenRole == DeviceTokenRole.Node
                ? data.NodeDeviceToken
                : data.DeviceToken;

            return string.IsNullOrWhiteSpace(token)
                ? DeviceTokenReadResult.Missing($"No stored {role} device token.")
                : DeviceTokenReadResult.Resolved(token);
        }
        catch (IOException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
            return DeviceTokenReadResult.Unreadable(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
            return DeviceTokenReadResult.Unreadable(ex.Message);
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
            return DeviceTokenReadResult.Corrupt(ex.Message);
        }
        catch (Exception ex) when (
            ex is FormatException
                or ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or CryptographicException)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
            return DeviceTokenReadResult.Corrupt(ex.Message);
        }
    }

    public static bool HasStoredDeviceToken(string dataPath, IOpenClawLogger? logger = null) =>
        !string.IsNullOrWhiteSpace(TryReadStoredDeviceToken(dataPath, logger));

    public static bool HasStoredDeviceTokenForRole(string dataPath, string role, IOpenClawLogger? logger = null) =>
        !string.IsNullOrWhiteSpace(TryReadStoredDeviceTokenForRole(dataPath, role, logger));

    private static string? ResolveStoredToken(string dataPath, DeviceTokenReadResult result)
    {
        if (result.Status == DeviceTokenReadStatus.Resolved)
            return result.Token;
        if (result.Status == DeviceTokenReadStatus.Missing)
            return null;

        var keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        Exception cause = result.Status == DeviceTokenReadStatus.Unreadable
            ? new IOException(result.Detail ?? "Identity file could not be read.")
            : new InvalidDataException(result.Detail ?? "Identity file is invalid.");
        throw new DeviceIdentityLoadException(keyPath, cause);
    }

    /// <summary>
    /// Sets the operator <c>DeviceToken</c> field to <c>null</c> in
    /// <c>device-key-ed25519.json</c> without deleting the file.
    /// Preserves all other fields (Ed25519 keypair, algorithm, timestamps,
    /// NodeDeviceToken).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the token was cleared; <c>false</c> if the file was
    /// absent or the <c>DeviceToken</c> field was already null/empty
    /// (idempotent skip).
    /// </returns>
    public static bool TryClearDeviceToken(string dataPath, IOpenClawLogger? logger = null) =>
        TryClearDeviceTokenForRole(dataPath, "operator", logger);

    /// <summary>
    /// Atomically clears <em>all</em> device-token fields (DeviceToken,
    /// DeviceTokenScopes, NodeDeviceToken, NodeDeviceTokenScopes) from
    /// <c>device-key-ed25519.json</c> while preserving the Ed25519 keypair,
    /// deviceId, algorithm, and all other properties. Uses raw JSON filtering
    /// so unknown/extra fields are preserved, and writes atomically via
    /// temp-file + rename.
    /// </summary>
    /// <returns>
    /// <c>true</c> if at least one token field was present and cleared;
    /// <c>false</c> if the file was absent or already had no tokens.
    /// </returns>
    public static bool TryClearAllDeviceTokens(string dataPath, IOpenClawLogger? logger = null)
    {
        var keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        if (!File.Exists(keyPath))
            return false;

        try
        {
            var json = File.ReadAllText(keyPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                logger?.Warn("Failed to clear all device tokens: device-key-ed25519.json root is not a JSON object.");
                return false;
            }

            var data = root.Deserialize<DeviceKeyData>();
            if (data == null)
            {
                logger?.Warn("Failed to clear all device tokens: device-key-ed25519.json did not contain an object.");
                return false;
            }
            _ = ValidateAndReconstruct(data);

            bool hadTokens = false;
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name is "DeviceToken" or "DeviceTokenScopes" or "NodeDeviceToken" or "NodeDeviceTokenScopes")
                    {
                        hadTokens = true;
                        continue;
                    }
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            if (!hadTokens)
                return false;

            var content = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            AtomicWriteKeyFileRaw(keyPath, content);
            logger?.Info("All device tokens cleared from device-key-ed25519.json (keypair preserved).");
            return true;
        }
        catch (IOException ex)
        {
            logger?.Warn($"Failed to clear all device tokens: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.Warn($"Failed to clear all device tokens: {ex.Message}");
            return false;
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Failed to clear all device tokens: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (
            ex is FormatException
                or ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or CryptographicException)
        {
            logger?.Warn($"Failed to clear all device tokens: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the role-specific device token field to <c>null</c> in
    /// <c>device-key-ed25519.json</c> without deleting the file. Preserves the
    /// Ed25519 keypair and unrelated role tokens.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the token was cleared; <c>false</c> if the file was
    /// absent or the role token was already null/empty.
    /// </returns>
    public static bool TryClearDeviceTokenForRole(string dataPath, string role, IOpenClawLogger? logger = null)
    {
        var tokenRole = ParseDeviceTokenRole(role);
        var keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        if (!File.Exists(keyPath))
            return false;

        try
        {
            var json = File.ReadAllText(keyPath);
            var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
            if (data == null)
                return false;

            var token = tokenRole == DeviceTokenRole.Node
                ? data.NodeDeviceToken
                : data.DeviceToken;
            if (string.IsNullOrEmpty(token))
                return false; // already null — idempotent

            if (tokenRole == DeviceTokenRole.Node)
            {
                data.NodeDeviceToken = null;
                data.NodeDeviceTokenScopes = null;
            }
            else
            {
                data.DeviceToken = null;
                data.DeviceTokenScopes = null;
            }

            AtomicWriteKeyFile(keyPath, data);
            logger?.Info($"{(tokenRole == DeviceTokenRole.Node ? "NodeDeviceToken" : "DeviceToken")} cleared from device-key-ed25519.json (file preserved).");
            return true;
        }
        catch (IOException ex)
        {
            logger?.Warn($"Failed to clear device token: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.Warn($"Failed to clear device token: {ex.Message}");
            return false;
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Failed to clear device token: {ex.Message}");
            return false;
        }
    }
    
    public DeviceIdentity(string dataPath, IOpenClawLogger? logger = null)
        : this(dataPath, logger, DeviceIdentityFileSystem.Instance)
    {
    }

    internal DeviceIdentity(
        string dataPath,
        IOpenClawLogger? logger,
        IDeviceIdentityFileSystem fileSystem)
    {
        _keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        _logger = logger ?? NullLogger.Instance;
        _fileSystem = fileSystem;
    }
    
    /// <summary>
    /// Initialize the device identity. Existing files load fail-closed; a new
    /// identity is published only when the path is conclusively absent.
    /// </summary>
    public void Initialize()
    {
        bool identityExists;
        try
        {
            identityExists = _fileSystem.IdentityFileExists(_keyPath);
        }
        catch (Exception ex) when (IsIdentityLoadFailure(ex))
        {
            throw CreateLoadException(ex);
        }

        if (identityExists)
        {
            LoadExisting();
            return;
        }

        GenerateNewOrLoadWinner();
    }

    private void LoadExisting()
    {
        try
        {
            var json = _fileSystem.ReadAllText(_keyPath);
            var data = JsonSerializer.Deserialize<DeviceKeyData>(json)
                ?? throw new InvalidDataException("Identity JSON did not contain an object.");

            var material = ValidateAndReconstruct(data);
            ApplyIdentity(data, material.PrivateKey, material.PublicKey, material.DeviceId);

            _logger.Info($"Loaded Ed25519 device identity: {_deviceId![..16]}...");
        }
        catch (DeviceIdentityLoadException)
        {
            throw;
        }
        catch (Exception ex) when (IsIdentityLoadFailure(ex))
        {
            throw CreateLoadException(ex);
        }
    }

    private static IdentityMaterial ValidateAndReconstruct(DeviceKeyData data)
    {
        if (string.IsNullOrWhiteSpace(data.PrivateKeyBase64))
            throw new InvalidDataException("Identity private key is missing.");

        var privateKey = Convert.FromBase64String(data.PrivateKeyBase64);
        if (privateKey.Length != Ed25519.SecretKeySize)
        {
            throw new InvalidDataException(
                $"Identity private key must be {Ed25519.SecretKeySize} bytes.");
        }

        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);

        if (!string.IsNullOrWhiteSpace(data.PublicKeyBase64))
        {
            var storedPublicKey = Convert.FromBase64String(data.PublicKeyBase64);
            if (!CryptographicOperations.FixedTimeEquals(publicKey, storedPublicKey))
                throw new InvalidDataException("Identity public key does not match the private key.");
        }

        var deviceId = ComputeDeviceId(publicKey);
        if (string.IsNullOrWhiteSpace(data.DeviceId))
            throw new InvalidDataException("Identity device ID is missing.");
        if (!string.Equals(data.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Identity device ID does not match the keypair.");
        if (!string.IsNullOrWhiteSpace(data.Algorithm) &&
            !string.Equals(data.Algorithm, "Ed25519", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Identity algorithm is not Ed25519.");
        }

        return new IdentityMaterial(privateKey, publicKey, deviceId);
    }

    private void GenerateNewOrLoadWinner()
    {
        try
        {
            GenerateNewOrLoadWinnerCore();
        }
        catch (DeviceIdentityLoadException)
        {
            throw;
        }
        catch (Exception ex) when (IsIdentityLoadFailure(ex))
        {
            throw CreateLoadException(ex);
        }
    }

    private void GenerateNewOrLoadWinnerCore()
    {
        _logger.Info("Generating new Ed25519 device keypair...");

        var privateKey = new byte[Ed25519.SecretKeySize];
        RandomNumberGenerator.Fill(privateKey);
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
        var deviceId = ComputeDeviceId(publicKey);

        var data = new DeviceKeyData
        {
            PrivateKeyBase64 = Convert.ToBase64String(privateKey),
            PublicKeyBase64 = Convert.ToBase64String(publicKey),
            DeviceId = deviceId,
            Algorithm = "Ed25519",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.DirectoryExists(dir))
        {
            _fileSystem.CreateDirectory(dir);
        }
        if (!string.IsNullOrEmpty(dir))
            McpAuthToken.TryRestrictDataDirectoryAcl(dir);

        if (!TryCreateKeyFile(data))
        {
            _logger.Info("Another process created the Ed25519 device identity; loading the persisted identity.");
            LoadCreateWinner();
            return;
        }

        ApplyIdentity(data, privateKey, publicKey, deviceId);
        _logger.Info($"Generated new Ed25519 device identity: {deviceId}");
    }

    private void LoadCreateWinner()
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                LoadExisting();
                return;
            }
            catch (DeviceIdentityLoadException ex) when (
                attempt < maxAttempts &&
                IsTransientSharingFailure(ex.InnerException))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(attempt * 10));
            }
        }
    }

    private bool TryCreateKeyFile(DeviceKeyData data)
    {
        var json = JsonSerializer.Serialize(data, JsonSerializerOptionsCache.WriteIndented);
        var dir = Path.GetDirectoryName(_keyPath);
        var tempDir = string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
        var tempPath = Path.Combine(
            tempDir,
            $".{Path.GetFileName(_keyPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            _fileSystem.WriteAllText(tempPath, json);
            McpAuthToken.TryRestrictSensitiveFileAcl(tempPath);

            try
            {
                _fileSystem.MoveFileNoOverwrite(tempPath, _keyPath);
            }
            catch (IOException ex) when (IsAlreadyExists(ex))
            {
                return false;
            }

            McpAuthToken.TryRestrictSensitiveFileAcl(_keyPath);
            return true;
        }
        finally
        {
            try
            {
                if (_fileSystem.FileExists(tempPath))
                    _fileSystem.DeleteFile(tempPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"DeviceIdentity.TryCreateKeyFile: temp cleanup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ApplyIdentity(
        DeviceKeyData data,
        byte[] privateKey,
        byte[] publicKey,
        string deviceId)
    {
        _privateKey = privateKey;
        _publicKey = publicKey;
        _deviceId = deviceId;
        _deviceToken = data.DeviceToken;
        _deviceTokenScopes = NormalizeScopes(data.DeviceTokenScopes);
        _nodeDeviceToken = data.NodeDeviceToken;
        _nodeDeviceTokenScopes = NormalizeScopes(data.NodeDeviceTokenScopes);
    }

    private DeviceIdentityLoadException CreateLoadException(Exception ex)
    {
        _logger.Error(
            $"Failed to load device key. Identity path left unchanged: {DescribeException(ex)}");
        return new DeviceIdentityLoadException(_keyPath, ex);
    }

    private static bool IsIdentityLoadFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or CryptographicException;

    private static bool IsAlreadyExists(IOException ex)
    {
        var nativeError = ex.HResult & 0xFFFF;
        return nativeError is 17 or 80 or 183;
    }

    private static bool IsTransientSharingFailure(Exception? ex)
    {
        if (ex is not IOException ioException)
            return false;

        var nativeError = ioException.HResult & 0xFFFF;
        return nativeError is 32 or 33;
    }

    private static string ComputeDeviceId(byte[] publicKey)
    {
        var hashBytes = SHA256.HashData(publicKey);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
    
    /// <summary>
    /// Sign a payload for device authentication.
    /// </summary>
    [Obsolete("Use SignConnectPayloadV3 instead. This method hardcodes v2 format with node-specific values.")]
    public string SignPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_privateKey == null || _deviceId == null)
            throw new InvalidOperationException("Device not initialized");
        
        // Build the payload to sign
        var payload = BuildDebugPayload(nonce, signedAtMs, clientId, authToken);
        
        // Sign with Ed25519
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = SignEd25519(dataBytes);
        
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
        var signature = SignEd25519(dataBytes);
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

        return $"v3|{_deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{safeToken}|{safeNonce}|{NormalizeAuthMetadata(platform)}|{NormalizeAuthMetadata(deviceFamily)}";
    }

    private static string NormalizeAuthMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            builder.Append(character is >= 'A' and <= 'Z'
                ? (char)(character + 32)
                : character);
        }

        return builder.ToString();
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
        var signature = SignEd25519(dataBytes);
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
    /// Build the legacy v2 payload string for node connections.
    /// </summary>
    [Obsolete("Use BuildConnectPayloadV3 instead. This method hardcodes v2 format with node-specific values.")]
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

        try
        {
            var data = ReadCurrentIdentityForTokenUpdate();
            data.DeviceToken = token;
            data.DeviceTokenScopes = scopes;
            AtomicWriteKeyFile(_keyPath, data);
            _deviceToken = token;
            _deviceTokenScopes = scopes;
            _logger.Info("Device token stored");
        }
        catch (DeviceIdentityLoadException)
        {
            throw;
        }
        catch (Exception ex) when (IsIdentityLoadFailure(ex))
        {
            throw CreateLoadException(ex);
        }
    }

    private void StoreNodeDeviceTokenCore(string token, string[]? scopes)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Device token cannot be empty.", nameof(token));

        try
        {
            var data = ReadCurrentIdentityForTokenUpdate();
            data.NodeDeviceToken = token;
            data.NodeDeviceTokenScopes = scopes;
            AtomicWriteKeyFile(_keyPath, data);
            _nodeDeviceToken = token;
            _nodeDeviceTokenScopes = scopes;
            _logger.Info("Node device token stored");
        }
        catch (DeviceIdentityLoadException)
        {
            throw;
        }
        catch (Exception ex) when (IsIdentityLoadFailure(ex))
        {
            throw CreateLoadException(ex);
        }
    }

    private DeviceKeyData ReadCurrentIdentityForTokenUpdate()
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");
        if (!File.Exists(_keyPath))
            throw new FileNotFoundException("Device identity file is missing.", _keyPath);

        var json = File.ReadAllText(_keyPath);
        var data = JsonSerializer.Deserialize<DeviceKeyData>(json)
            ?? throw new InvalidDataException("Identity file was empty or invalid.");
        var material = ValidateAndReconstruct(data);
        if (!string.Equals(material.DeviceId, _deviceId, StringComparison.Ordinal))
            throw new InvalidDataException("Identity file changed while updating its device token.");

        return data;
    }

    /// <summary>
    /// Atomic write of device-key JSON: serialize to a sibling temp file
    /// (<c>.&lt;name&gt;.&lt;guid&gt;.tmp</c>), lock its ACL, then
    /// <see cref="File.Move(string,string,bool)"/> with overwrite=true. The
    /// rename is atomic on NTFS — a process-kill or power-loss mid-write
    /// either leaves the existing key file intact or replaces it wholesale,
    /// never a torn/zero-byte file that the next LoadOrCreate would silently
    /// rotate the identity over.
    /// Same shape as <see cref="OpenClaw.Shared.Mcp.McpAuthToken"/>.
    /// </summary>
    private static void AtomicWriteKeyFile(string path, DeviceKeyData data)
    {
        var json = JsonSerializer.Serialize(data, JsonSerializerOptionsCache.WriteIndented);
        AtomicWriteKeyFileRaw(path, json);
    }

    /// <summary>
    /// Atomically writes pre-serialized JSON content to a device-key file path
    /// using temp-file + rename. Use this when restoring a backup or writing
    /// content that is already serialized.
    /// </summary>
    public static void AtomicWriteKeyFileRaw(string path, string jsonContent)
    {
        var dir = Path.GetDirectoryName(path);
        var tempDir = string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
        var tempPath = Path.Combine(tempDir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, jsonContent);
            McpAuthToken.TryRestrictSensitiveFileAcl(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"DeviceIdentity.AtomicWriteKeyFile: write failed for '{path}': {ex.GetType().Name}: {ex.Message}");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception delEx) { System.Diagnostics.Trace.WriteLine($"DeviceIdentity.AtomicWriteKeyFile: temp cleanup failed: {delEx.GetType().Name}: {delEx.Message}"); }
            throw;
        }
        McpAuthToken.TryRestrictSensitiveFileAcl(path);
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

    private static string DescribeException(Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        return ex.InnerException == null
            ? message
            : $"{message} (inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message})";
    }

    private byte[] SignEd25519(byte[] data)
    {
        if (_privateKey == null)
            throw new InvalidOperationException("Device not initialized");

        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(_privateKey, 0, data, 0, data.Length, signature, 0);
        return signature;
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

    private sealed record IdentityMaterial(
        byte[] PrivateKey,
        byte[] PublicKey,
        string DeviceId);

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
