namespace OpenClaw.Shared;

/// <summary>
/// Reads stored device tokens from a DeviceIdentity file without requiring
/// a full DeviceIdentity instance. Enables testable credential resolution.
/// </summary>
public interface IDeviceIdentityReader
{
    /// <summary>
    /// Try to read the stored operator device token from the identity file at the given path.
    /// Returns null if no token is stored or the file doesn't exist.
    /// </summary>
    string? TryReadStoredDeviceToken(string dataPath);

    /// <summary>
    /// Try to read the stored node device token from the identity file at the given path.
    /// Returns null if no token is stored or the file doesn't exist.
    /// </summary>
    string? TryReadStoredNodeDeviceToken(string dataPath);
}
