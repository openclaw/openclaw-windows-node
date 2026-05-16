using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Implementation of IDeviceIdentityStore that delegates to DeviceIdentity.
/// Used by the manager to write device tokens received from the gateway.
/// </summary>
public sealed class DeviceIdentityStore : IDeviceIdentityStore
{
    private readonly IOpenClawLogger _logger;

    public DeviceIdentityStore(IOpenClawLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public void StoreToken(string identityPath, string token, string[]? scopes, string role)
    {
        try
        {
            var identity = new DeviceIdentity(identityPath, _logger);
            identity.Initialize();
            identity.StoreDeviceTokenForRole(role, token, scopes);
            _logger.Info($"[IdentityStore] Stored {role} device token at {identityPath}");
        }
        catch (Exception ex)
        {
            _logger.Error($"[IdentityStore] Failed to store {role} device token: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear stored device tokens from an identity file, keeping the keypair intact.
    /// Strips DeviceToken, DeviceTokenScopes, NodeDeviceToken, and NodeDeviceTokenScopes
    /// from the identity JSON while preserving keys, deviceId, algorithm, etc.
    /// </summary>
    public static void ClearStoredTokens(string identityDir, IOpenClawLogger? logger = null)
    {
        var keyPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (!File.Exists(keyPath)) return;
        try
        {
            var json = File.ReadAllText(keyPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            using var ms = new MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name is "DeviceToken" or "DeviceTokenScopes" or "NodeDeviceToken" or "NodeDeviceTokenScopes")
                    continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.Flush();

            File.WriteAllBytes(keyPath, ms.ToArray());
            logger?.Info($"[IdentityStore] Cleared stored device tokens from {identityDir}");
        }
        catch (Exception ex)
        {
            logger?.Warn($"[IdentityStore] Failed to clear device tokens: {ex.Message}");
        }
    }
}
