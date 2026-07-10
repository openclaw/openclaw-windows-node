using System.Diagnostics.Metrics;

namespace OpenClaw.Shared.Telemetry;

public enum OpenClawMeterName
{
    OpenClaw
}

/// <summary>
/// Stable Meter names used by OpenClaw metrics.
/// </summary>
public static class OpenClawMeters
{
    public static Meter OpenClawMeter { get; } = new(OpenClawMeterName.OpenClaw.ToTelemetryName());

    public static string ToTelemetryName(this OpenClawMeterName meter) =>
        meter switch
        {
            OpenClawMeterName.OpenClaw => "openclaw",
            _ => throw new ArgumentOutOfRangeException(nameof(meter), meter, "Unknown OpenClaw meter.")
        };

    internal static Meter ToMeter(this OpenClawMeterName meter) =>
        meter switch
        {
            OpenClawMeterName.OpenClaw => OpenClawMeter,
            _ => throw new ArgumentOutOfRangeException(nameof(meter), meter, "Unknown OpenClaw meter.")
        };
}
