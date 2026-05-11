using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

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
}
