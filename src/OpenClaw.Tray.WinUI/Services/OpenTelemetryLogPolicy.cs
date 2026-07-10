using Microsoft.Extensions.Logging;

namespace OpenClawTray.Services;

internal static class OpenTelemetryLogPolicy
{
    public const string TelemetryExporterCategory = "OpenClaw.Telemetry.Exporter";

    public static bool ShouldExport(string? category, LogLevel level) =>
        level is >= LogLevel.Information and < LogLevel.None &&
        category is not null &&
        category.StartsWith("OpenClaw.Telemetry.", StringComparison.Ordinal);
}
