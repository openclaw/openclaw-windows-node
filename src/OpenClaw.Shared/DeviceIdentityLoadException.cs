namespace OpenClaw.Shared;

/// <summary>
/// Raised when a persisted device identity exists but cannot be loaded safely.
/// The identity file is left unchanged so recovery requires an explicit reset.
/// </summary>
public sealed class DeviceIdentityLoadException : Exception
{
    public const string RecoveryMessage =
        "Device identity could not be loaded or saved. OpenClaw did not replace an existing identity. Check access to the identity file shown in diagnostics. If it exists and you intend to reset pairing, move it aside explicitly, then reconnect.";

    public DeviceIdentityLoadException(string identityPath, Exception innerException)
        : base(RecoveryMessage, innerException)
    {
        IdentityPath = identityPath;
    }

    public string IdentityPath { get; }
}
