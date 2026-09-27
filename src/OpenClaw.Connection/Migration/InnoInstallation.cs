namespace OpenClaw.Connection.Migration;

public enum InnoInstallationStatus
{
    NotInstalled,
    Detected,
    Unsupported,
    InspectionFailed
}

/// <summary>
/// Read-only installation evidence, not authorization to migrate or uninstall.
/// </summary>
public sealed record InnoInstallation(
    string InstallDirectory,
    string ExecutablePath,
    string UninstallerPath,
    string Architecture,
    Version Version);

public sealed record InnoInstallationDetection(
    InnoInstallationStatus Status,
    InnoInstallation? Installation = null,
    string? Reason = null);

public interface IInnoInstallationDetector
{
    InnoInstallationDetection Detect();
}
