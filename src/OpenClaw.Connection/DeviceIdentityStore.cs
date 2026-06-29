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
    /// Writes atomically via temp-file + rename to prevent torn writes from
    /// silently rotating device identity on crash/power-loss.
    /// </summary>
    public static void ClearStoredTokens(string identityDir, IOpenClawLogger? logger = null)
    {
        try
        {
            DeviceIdentity.TryClearAllDeviceTokens(identityDir, logger);
        }
        catch (Exception ex)
        {
            logger?.Warn($"[IdentityStore] Failed to clear device tokens: {ex.Message}");
        }
    }
}
