using Microsoft.Extensions.Logging;

namespace OpenClawTray.Services;

internal static class OpenTelemetryLogPolicy
{
    public const string TelemetryExporterCategory = "OpenClaw.Telemetry.Exporter";
    public const string ConnectionCategory = "OpenClaw.Telemetry.Connection";
    public const string NodeToolCategory = "OpenClaw.Telemetry.NodeTool";

    public static bool ShouldExport(string? category, LogLevel level) =>
        level is >= LogLevel.Information and < LogLevel.None &&
        category is TelemetryExporterCategory or ConnectionCategory or NodeToolCategory;
}
