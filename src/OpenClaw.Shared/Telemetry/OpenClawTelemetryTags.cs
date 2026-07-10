namespace OpenClaw.Shared.Telemetry;

public enum OpenClawTelemetryTagKey
{
    Source,
    Outcome,
    ErrorCategory,
    ErrorType,
    Reason,
    Status
}

/// <summary>
/// Stable tag keys used by OpenClaw instrumentation.
/// </summary>
public static class OpenClawTelemetryTags
{
    public static string ToTelemetryName(this OpenClawTelemetryTagKey key) =>
        key switch
        {
            OpenClawTelemetryTagKey.Source => "openclaw.source",
            OpenClawTelemetryTagKey.Outcome => "openclaw.outcome",
            OpenClawTelemetryTagKey.ErrorCategory => "openclaw.error.category",
            OpenClawTelemetryTagKey.ErrorType => "error.type",
            OpenClawTelemetryTagKey.Reason => "openclaw.reason",
            OpenClawTelemetryTagKey.Status => "openclaw.status",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown OpenClaw telemetry tag key.")
        };
}
