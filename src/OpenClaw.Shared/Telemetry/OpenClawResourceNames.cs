namespace OpenClaw.Shared.Telemetry;

public enum OpenClawResourceName
{
    WindowsTray,
    WindowsNode
}

/// <summary>
/// Stable resource names used by OpenClaw telemetry exporters.
/// </summary>
public static class OpenClawResourceNames
{
    public static string ToServiceName(this OpenClawResourceName resource) =>
        resource switch
        {
            OpenClawResourceName.WindowsTray => "openclaw-windows-tray",
            OpenClawResourceName.WindowsNode => "openclaw-windows-node",
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown OpenClaw resource name.")
        };
}
