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
    /// Read the stored operator device token and preserve why it was unavailable.
    /// </summary>
    DeviceTokenReadResult ReadStoredDeviceToken(string dataPath)
    {
        var token = TryReadStoredDeviceToken(dataPath);
        return string.IsNullOrWhiteSpace(token)
            ? DeviceTokenReadResult.Missing()
            : DeviceTokenReadResult.Resolved(token!);
    }

    /// <summary>
    /// Try to read the stored node device token from the identity file at the given path.
    /// Returns null if no token is stored or the file doesn't exist.
    /// </summary>
    string? TryReadStoredNodeDeviceToken(string dataPath);

    /// <summary>
    /// Read the stored node device token and preserve why it was unavailable.
    /// </summary>
    DeviceTokenReadResult ReadStoredNodeDeviceToken(string dataPath)
    {
        var token = TryReadStoredNodeDeviceToken(dataPath);
        return string.IsNullOrWhiteSpace(token)
            ? DeviceTokenReadResult.Missing()
            : DeviceTokenReadResult.Resolved(token!);
    }
}
