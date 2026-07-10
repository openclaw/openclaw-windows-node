namespace OpenClaw.Shared.Telemetry;

/// <summary>
/// Stable tag keys used by OpenClaw instrumentation.
/// </summary>
public static class OpenClawTelemetryTags
{
    public const string Source = "openclaw.source";
    public const string Outcome = "openclaw.outcome";
    public const string ErrorCategory = "openclaw.errorCategory";
    public const string ErrorType = "error.type";
    public const string Exporter = "openclaw.exporter";
    public const string ExporterProtocol = "openclaw.exporter.protocol";
    public const string Reason = "openclaw.reason";
    public const string Signal = "openclaw.signal";
    public const string Status = "openclaw.status";
}
