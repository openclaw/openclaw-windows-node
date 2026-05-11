namespace OpenClaw.Shared;

/// <summary>
/// Default implementation of <see cref="IDeviceIdentityReader"/> that delegates
/// to the static <see cref="DeviceIdentity.TryReadStoredDeviceToken"/> methods.
/// </summary>
public sealed class DeviceIdentityFileReader : IDeviceIdentityReader
{
    public static readonly DeviceIdentityFileReader Instance = new();

    public string? TryReadStoredDeviceToken(string dataPath) =>
        DeviceIdentity.TryReadStoredDeviceToken(dataPath);

    public string? TryReadStoredNodeDeviceToken(string dataPath) =>
        DeviceIdentity.TryReadStoredDeviceTokenForRole(dataPath, "node");
}
